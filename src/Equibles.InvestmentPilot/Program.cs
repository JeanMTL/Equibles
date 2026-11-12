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

if (args.Length != 1 || args[0] != "--execute-msft-once")
{
    Console.Error.WriteLine("This executable is opt-in only. Run with --execute-msft-once after reviewing the local SEC email and database connection.");
    return 2;
}

var contactEmail = Environment.GetEnvironmentVariable("Sec__ContactEmail");
try
{
    SecContactEmail.Validate(contactEmail);
}
catch (InvalidOperationException exception)
{
    Console.Error.WriteLine(exception.Message);
    return 2;
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
builder.Services.AddHttpClient<ISecCompanyFactsSource, BoundedSecCompanyFactsSource>()
    .ConfigurePrimaryHttpMessageHandler(BoundedSecCompanyFactsSource.CreateHttpClientHandler);
builder.Services.AddScoped(serviceProvider => new FinancialFactsImportService(
    serviceProvider.GetRequiredService<IServiceScopeFactory>(),
    secEdgarClient: null,
    serviceProvider.GetRequiredService<ILogger<FinancialFactsImportService>>(),
    serviceProvider.GetRequiredService<ErrorReporter>()
));
builder.Services.AddScoped<MsftSecImportRunner>();

using var host = builder.Build();
try
{
    using var scope = host.Services.CreateScope();
    var runner = scope.ServiceProvider.GetRequiredService<MsftSecImportRunner>();
    using var stop = new CancellationTokenSource(MsftSecImportRunner.TotalRunLimit);
    var report = await runner.RunOnceAsync(contactEmail, stop.Token);
    Console.WriteLine(System.Text.Json.JsonSerializer.Serialize(report, new System.Text.Json.JsonSerializerOptions { WriteIndented = true }));
    return 0;
}
catch (Exception exception)
{
    // Intentionally omit configuration, request headers, and connection details from output.
    Console.Error.WriteLine($"MSFT one-shot import failed: {exception.GetType().Name}: {exception.Message}");
    return 1;
}
