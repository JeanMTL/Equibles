using Equibles.CommonStocks.Data.Models;
using Equibles.CommonStocks.Repositories;
using Equibles.CorporateActions.Repositories;
using Equibles.Data;
using Equibles.Errors.BusinessLogic;
using Equibles.Errors.Data.Models;
using Equibles.Integrations.Sec.Contracts;
using Equibles.Integrations.Sec.Models.Responses;
using Equibles.InvestmentPilot;
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

    [Theory]
    [InlineData("0000789019")]
    [InlineData("789019")]
    public async Task SyntheticMsftCompanyFacts_ImportsIdempotently_AndIsVisibleThroughMcp(
        string storedCik
    )
    {
        var issuer = Equibles.TestSupport.EquityIssuerSeed.Create(
            Ticker: "MSFT",
            Name: "Microsoft Corporation (synthetic test fixture)",
            Cik: storedCik,
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

        var source = new OfflineCompanyFactsSource(response);
        var runner = new MsftSecImportRunner(
            DbContext,
            importer,
            source,
            NullLogger<MsftSecImportRunner>()
        );
        var report = await runner.RunOnceAsync("owner@example.org", CancellationToken.None);

        report.RequestCount.Should().Be(1);
        report.DownloadedBodyBytes.Should().BeGreaterThan(0);
        report.SourceUrl.Should().Be(BoundedSecCompanyFactsSource.AllowedUrl);
        report.CandidateObservationsAfterDateFloor.Should().BeGreaterThan(0);
        report.UnsupportedTaxonomyObservations.Should().Be(0);
        report.PersistedSourceFactRows.Should().Be(3);

        var repeatRunner = new MsftSecImportRunner(
            DbContext,
            importer,
            new OfflineCompanyFactsSource(response),
            NullLogger<MsftSecImportRunner>()
        );
        var repeatReport = await repeatRunner.RunOnceAsync("owner@example.org", CancellationToken.None);
        repeatReport.NewFactRows.Should().Be(0);
        repeatReport.ChangedFactRows.Should().Be(0);
        repeatReport.PersistedSourceFactRows.Should().Be(3);

        var stored = await DbContext
            .Set<FinancialFact>()
            .Include(f => f.FinancialConcept)
            .Where(f => f.EquityIssuerId == issuer.Id)
            .ToListAsync();
        stored.Should().HaveCount(3);
        stored.Should().OnlyContain(f =>
            f.SourceCik == "0000789019"
            && f.SourceUrl == "https://data.sec.gov/api/xbrl/companyfacts/CIK0000789019.json"
        );
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
                && f.SourceCik == "0000789019"
                && f.SourceUrl == "https://data.sec.gov/api/xbrl/companyfacts/CIK0000789019.json"
            );

        await secClient.DidNotReceiveWithAnyArgs().GetCompanyFacts(default);
        var tool = new FinancialFactsTools(
            new FinancialFactRepository(DbContext),
            new FinancialConceptRepository(DbContext),
            new EquityIssuerRepository(DbContext),
            new StockSplitRepository(DbContext),
            ErrorManager,
            NullLogger<FinancialFactsTools>()
        );
        var result = await tool.GetFinancialFact("MSFT", "revenue");
        result.Should().Contain("2025 | FY");
        result.Should().Contain("123,456,789,000");
        result.Should().Contain("USD");
        result.Should().Contain("Source CIK");
        result.Should().Contain("0000789019");
        result.Should().Contain("https://data.sec.gov/api/xbrl/companyfacts/CIK0000789019.json");
    }

    private sealed class OfflineCompanyFactsSource(CompanyFactsResponse response) : ISecCompanyFactsSource
    {
        public Task<SecCompanyFactsDownload> FetchMsftAsync(
            string contactEmail,
            CancellationToken cancellationToken
        )
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(new SecCompanyFactsDownload(
                response,
                new Uri(BoundedSecCompanyFactsSource.AllowedUrl),
                RequestCount: 1,
                DownloadedBodyBytes: 128,
                RetrievedAtUtc: DateTimeOffset.UtcNow,
                ObservationCount: 3
            ));
        }
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
