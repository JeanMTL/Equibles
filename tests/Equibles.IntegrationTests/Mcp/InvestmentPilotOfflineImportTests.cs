using Equibles.CommonStocks.Data.Models;
using Equibles.CommonStocks.Repositories;
using Equibles.CorporateActions.Repositories;
using Equibles.Data;
using Equibles.Errors.BusinessLogic;
using Equibles.Errors.Data.Models;
using Equibles.Integrations.Sec.Contracts;
using Equibles.Integrations.Sec.Models.Responses;
using Equibles.IntegrationTests.Helpers;
using Equibles.Sec.FinancialFacts.BusinessLogic;
using Equibles.Sec.FinancialFacts.Data.Models;
using Equibles.Sec.FinancialFacts.HostedService.Services;
using Equibles.Sec.FinancialFacts.Mcp.Tools;
using Equibles.Sec.FinancialFacts.Repositories;
using Equibles.Sec.Repositories;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using NSubstitute;
using Xunit;

namespace Equibles.IntegrationTests.Mcp;

[Collection(ParadeDbCollection.Name)]
public sealed class InvestmentPilotOfflineImportTests : ParadeDbMcpTestBase
{
    private readonly List<EquiblesFinancialDbContext> _importScopes = [];

    public InvestmentPilotOfflineImportTests(ParadeDbFixture fixture)
        : base(fixture) { }

    public override Task DisposeAsync()
    {
        foreach (var context in _importScopes)
            context.Dispose();
        return base.DisposeAsync();
    }

    [Fact]
    public async Task SyntheticMsftCompanyFacts_ImportsIdempotently_AndIsVisibleThroughMcp()
    {
        var issuer = Equibles.TestSupport.EquityIssuerSeed.Create(
            Ticker: "MSFT",
            Name: "Microsoft Corporation (synthetic test fixture)",
            Cik: "0000789019",
            FiscalYearEndMonth: 6,
            FiscalYearEndDay: 30
        );
        DbContext.Set<EquityIssuer>().Add(issuer);
        await DbContext.SaveChangesAsync();

        var fixturePath = Path.Combine(
            AppContext.BaseDirectory,
            "TestAssets",
            "InvestmentPilot",
            "msft-companyfacts.synthetic.json"
        );
        var response = JsonConvert.DeserializeObject<CompanyFactsResponse>(
            await File.ReadAllTextAsync(fixturePath)
        );
        response.Should().NotBeNull();
        response.Cik.Should().Be(789019);
        var submissionsPath = Path.Combine(
            AppContext.BaseDirectory,
            "TestAssets",
            "InvestmentPilot",
            "msft-submissions.synthetic.json"
        );
        var submissions = JObject.Parse(await File.ReadAllTextAsync(submissionsPath));
        submissions["cik"]!.Value<string>().Should().Be("0000789019");
        submissions["filings"]!["recent"]!["accessionNumber"]![0]!
            .Value<string>()
            .Should()
            .Be("0000789019-25-000001");

        var secClient = Substitute.For<ISecEdgarClient>();
        var errorReporter = Substitute.For<ErrorReporter>(
            Substitute.For<IServiceScopeFactory>(),
            Substitute.For<ILogger<ErrorReporter>>()
        );
        var importer = new FinancialFactsImportService(
            CreateImportScopeFactory(),
            secClient,
            NullLogger<FinancialFactsImportService>(),
            errorReporter
        );

        await importer.ImportProvidedFacts(issuer, response, CancellationToken.None);
        await importer.ImportProvidedFacts(issuer, response, CancellationToken.None);

        var stored = await DbContext
            .Set<FinancialFact>()
            .Include(f => f.FinancialConcept)
            .Where(f => f.EquityIssuerId == issuer.Id)
            .ToListAsync();
        stored.Should().HaveCount(3);
        stored
            .Should()
            .ContainSingle(f => f.FinancialConcept.Tag == "Revenues")
            .Which.Should()
            .Match<FinancialFact>(f =>
                f.Value == 123456789000m
                && f.Unit == "USD"
                && f.Form.ToString() == "10-K"
                && f.FiscalYear == 2025
                && f.FiledDate == new DateOnly(2025, 7, 30)
                && f.AccessionNumber == "0000789019-25-000001"
            );

        await secClient.DidNotReceiveWithAnyArgs().GetCompanyFacts(default);
        await errorReporter
            .DidNotReceive()
            .Report(
                Arg.Any<ErrorSource>(),
                Arg.Any<string>(),
                Arg.Any<Exception>(),
                Arg.Any<string>()
            );
        var tool = new FinancialFactsTools(
            new FinancialFactRepository(DbContext),
            new FinancialConceptRepository(DbContext),
            new EquityIssuerRepository(DbContext),
            new StockSplitRepository(DbContext),
            ErrorManager,
            NullLogger<FinancialFactsTools>()
        );
        var result = await tool.GetFinancialFact("MSFT", "revenue");
        result.Should().Contain("FY2025");
        result.Should().Contain("123,456,789,000");
        result.Should().Contain("USD");
    }

    private IServiceScopeFactory CreateImportScopeFactory()
    {
        var factory = Substitute.For<IServiceScopeFactory>();
        factory
            .CreateScope()
            .Returns(_ =>
            {
                var context = Fixture.CreateDbContext();
                _importScopes.Add(context);
                var provider = Substitute.For<IServiceProvider>();
                provider.GetService(typeof(EquiblesFinancialDbContext)).Returns(context);
                provider
                    .GetService(typeof(FinancialConceptRepository))
                    .Returns(new FinancialConceptRepository(context));
                provider
                    .GetService(typeof(FinancialFactsSyncStatusRepository))
                    .Returns(new FinancialFactsSyncStatusRepository(context));
                provider
                    .GetService(typeof(DocumentRepository))
                    .Returns(new DocumentRepository(context));
                provider
                    .GetService(typeof(ISharesOutstandingProvider))
                    .Returns(Substitute.For<ISharesOutstandingProvider>());
                provider
                    .GetService(typeof(EquityIssuerRepository))
                    .Returns(new EquityIssuerRepository(context));
                var scope = Substitute.For<IServiceScope>();
                scope.ServiceProvider.Returns(provider);
                return scope;
            });
        return factory;
    }
}
