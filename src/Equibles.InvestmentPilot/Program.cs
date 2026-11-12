using Equibles.CommonStocks.BusinessLogic.Directory;
using Equibles.Core.AutoWiring;
using Equibles.Data.Extensions;
using Equibles.Errors.BusinessLogic;
using Equibles.Migrations;
using Equibles.InvestmentPilot;
using Equibles.Sec.FinancialFacts.HostedService.Services;
using Microsoft.Extensions.Configuration;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

var initializeMsft = args.Length == 1 && args[0] == "--initialize-msft-issuer";
var executeMsft = args.Length == 1 && args[0] == "--execute-msft-once";
if (!initializeMsft && !executeMsft)
{
    Console.Error.WriteLine(
        "Use --initialize-msft-issuer with the read-only-mounted evidence file, or --execute-msft-once after reviewing SEC email, identity and database preflights."
    );
    return 2;
}

var contactEmail = initializeMsft ? null : Environment.GetEnvironmentVariable("Sec__ContactEmail");
if (executeMsft)
{
    try
    {
        SecContactEmail.Validate(contactEmail);
    }
    catch (InvalidOperationException exception)
    {
        Console.Error.WriteLine(exception.Message);
        return 2;
    }
}

var builder = Host.CreateApplicationBuilder([]);
Equibles.Plugins.PluginLoader.LoadAll();

var connectionString = builder.Configuration.GetConnectionString("DefaultConnection");
if (string.IsNullOrWhiteSpace(connectionString))
{
    Console.Error.WriteLine("ConnectionStrings__DefaultConnection is required; no SEC request was made.");
    return 2;
}

builder.Services.AddEquiblesFinancialDbContext(
    connectionString,
    modules => modules.AddAllModules(),
    migrationsAssembly: typeof(DesignTimeDbContextFactory).Assembly
);
builder.Services.AddAllRepositories();
builder.Services.AutoWireServicesFrom<ErrorManager>();
builder.Services.AddScoped<EquityDirectoryIdentityImporter>();
builder.Services.AddScoped<MsftIssuerBootstrapper>();
if (executeMsft)
{
    builder.Services.AddHttpClient<ISecCompanyFactsSource, BoundedSecCompanyFactsSource>()
        .ConfigurePrimaryHttpMessageHandler(BoundedSecCompanyFactsSource.CreateHttpClientHandler);
    builder.Services.AddScoped(serviceProvider => new FinancialFactsImportService(
        serviceProvider.GetRequiredService<IServiceScopeFactory>(),
        secEdgarClient: null,
        serviceProvider.GetRequiredService<ILogger<FinancialFactsImportService>>(),
        serviceProvider.GetRequiredService<ErrorReporter>()
    ));
    builder.Services.AddScoped<MsftSecImportRunner>();
}

using var host = builder.Build();
try
{
    using var scope = host.Services.CreateScope();
    using var stop = new CancellationTokenSource(TimeSpan.FromMinutes(5));
    if (initializeMsft)
    {
        if (!await scope.ServiceProvider.GetRequiredService<Equibles.Data.EquiblesFinancialDbContext>()
            .Database.CanConnectAsync(stop.Token))
            throw new InvalidOperationException("Equibles database is not reachable; no issuer was initialized.");
        var evidence = await MsftIssuerBootstrapper.ReadEvidenceFileAsync(stop.Token);
        var listingId = await scope.ServiceProvider
            .GetRequiredService<MsftIssuerBootstrapper>()
            .ImportAsync(evidence, stop.Token);
        Console.WriteLine(System.Text.Json.JsonSerializer.Serialize(
            new { ticker = MsftIssuerBootstrapper.Ticker, cik = MsftIssuerBootstrapper.IssuerCik, listingId, source = MsftIssuerBootstrapper.ListingSourceUrl },
            new System.Text.Json.JsonSerializerOptions { WriteIndented = true }
        ));
        return 0;
    }

    var runner = scope.ServiceProvider.GetRequiredService<MsftSecImportRunner>();
    using var importStop = new CancellationTokenSource(MsftSecImportRunner.TotalRunLimit);
    var report = await runner.RunOnceAsync(contactEmail, importStop.Token);
    Console.WriteLine(System.Text.Json.JsonSerializer.Serialize(report, new System.Text.Json.JsonSerializerOptions { WriteIndented = true }));
    return 0;
}
catch (Exception exception)
{
    // Intentionally omit configuration, request headers, and connection details from output.
    var action = initializeMsft ? "issuer initialization" : "one-shot import";
    Console.Error.WriteLine($"MSFT {action} failed: {exception.GetType().Name}: {exception.Message}");
    return 1;
}
