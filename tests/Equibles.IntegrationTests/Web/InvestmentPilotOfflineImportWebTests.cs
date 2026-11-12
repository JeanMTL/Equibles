using Equibles.CommonStocks.Data.Models;
using Equibles.Integrations.Sec.Contracts;
using Equibles.Integrations.Sec.Models.Responses;
using Equibles.IntegrationTests.Helpers;
using Equibles.Errors.BusinessLogic;
using Equibles.Sec.FinancialFacts.HostedService.Services;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json;
using NSubstitute;
using Xunit;

namespace Equibles.IntegrationTests.Web;

[Collection(WebHostCollection.Name)]
public sealed class InvestmentPilotOfflineImportWebTests
{
    private readonly WebHostFixture _fixture;

    public InvestmentPilotOfflineImportWebTests(WebHostFixture fixture) => _fixture = fixture;

    [Fact]
    public async Task SyntheticMsftFactsImportedThroughExistingService_RenderInExistingWebFinancials()
    {
        EquityIssuer issuer = null;
        await _fixture.ResetAndSeedAsync(db =>
        {
            issuer = Equibles.TestSupport.EquityIssuerSeed.Create(
                Ticker: "MSFT",
                Name: "Microsoft Corporation (synthetic test fixture)",
                Cik: "0000789019",
                FiscalYearEndMonth: 6,
                FiscalYearEndDay: 30
            );
            db.Set<EquityIssuer>().Add(issuer);
            return Task.CompletedTask;
        });

        var payloadPath = Path.Combine(
            AppContext.BaseDirectory,
            "TestAssets",
            "InvestmentPilot",
            "msft-companyfacts.synthetic.json"
        );
        var response = JsonConvert.DeserializeObject<CompanyFactsResponse>(
            await File.ReadAllTextAsync(payloadPath)
        );

        using (var scope = _fixture.Services.CreateScope())
        {
            // The importer is hosted by the Worker in production and intentionally
            // is not registered by the Web host. Exercise its shared persistence
            // path directly without starting/registering the Worker.
            var importer = new FinancialFactsImportService(
                _fixture.Services.GetRequiredService<IServiceScopeFactory>(),
                Substitute.For<ISecEdgarClient>(),
                scope.ServiceProvider.GetRequiredService<ILogger<FinancialFactsImportService>>(),
                scope.ServiceProvider.GetRequiredService<ErrorReporter>()
            );
            await importer.ImportProvidedFactsForPilot(issuer, response, CancellationToken.None);
        }

        var webResponse = await _fixture.Client.GetAsync("/Stocks/MSFT/Financials");
        webResponse.StatusCode.Should().Be(System.Net.HttpStatusCode.OK);
        var html = await webResponse.Content.ReadAsStringAsync();
        html.Should().Contain("Financials");
        html.Should().Contain("Revenue");
        html.Should().Contain("FY2025");
        html.Should().Contain("123");
    }
}
