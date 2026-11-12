using Equibles.CommonStocks.Data.Models;
using Equibles.Integrations.Sec.Models.Responses;
using Equibles.IntegrationTests.Helpers;
using Equibles.Sec.FinancialFacts.HostedService.Services;
using Microsoft.Extensions.DependencyInjection;
using Newtonsoft.Json;
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
            var importer = scope.ServiceProvider.GetRequiredService<FinancialFactsImportService>();
            await importer.ImportProvidedFacts(issuer, response, CancellationToken.None);
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
