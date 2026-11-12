using Equibles.CommonStocks.BusinessLogic.Directory;
using Equibles.CommonStocks.Data.Models;
using Equibles.CommonStocks.Repositories;
using Equibles.InvestmentPilot;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;

namespace Equibles.UnitTests.InvestmentPilot;

public sealed class MsftIssuerBootstrapperTests
{
    [Theory]
    [InlineData("0000789019")]
    [InlineData("789019")]
    public async Task ImportAsync_UsesOnlyTheValidatedMsftListingImporter(string cik)
    {
        EquityDirectoryListingInput captured = null;
        var listingId = Guid.NewGuid();
        var importer = new EquityDirectoryIdentityImporter(Substitute.For<IServiceScopeFactory>());
        importer
            .ImportListing(
                Arg.Do<EquityDirectoryListingInput>(input => captured = input),
                Arg.Any<CancellationToken>()
            )
            .Returns(listingId);
        var bootstrapper = new MsftIssuerBootstrapper(importer);

        var result = await bootstrapper.ImportAsync(Evidence(cik));

        result.Should().Be(listingId);
        captured.Should().NotBeNull();
        captured.Source.Should().Be(MsftIssuerBootstrapper.Source);
        captured.SourceIssuerIdentifier.Should().Be(MsftIssuerBootstrapper.IssuerCik);
        captured.IssuerCik.Should().Be(cik);
        captured.Ticker.Should().Be("MSFT");
        captured.MarketIdentifierCode.Should().Be("XNAS");
        captured.Isin.Should().Be("US5949181045");
        captured.SecurityType.Should().Be(EquitySecurityKind.OrdinaryShare);
        captured.TradingCurrency.Should().Be("USD");
        captured.QuoteUnitMultiplier.Should().Be(1m);
        captured.SourceUrl.Should().Be(MsftIssuerBootstrapper.ListingSourceUrl);
        captured.PayloadJson.Should().Contain(MsftIssuerBootstrapper.CikSourceUrl);
        captured.PayloadJson.Should().Contain("\"cik\":789019");
        await importer.Received(1).ImportListing(captured, Arg.Any<CancellationToken>());
    }

    [Theory]
    [InlineData("AAPL", "XNAS", "US5949181045")]
    [InlineData("MSFT", "XNYS", "US5949181045")]
    [InlineData("MSFT", "XNAS", "US0378331005")]
    public async Task ImportAsync_RejectsOtherTickerVenueOrSecurityBeforeWriting(
        string ticker,
        string mic,
        string isin
    )
    {
        var importer = new EquityDirectoryIdentityImporter(Substitute.For<IServiceScopeFactory>());
        var bootstrapper = new MsftIssuerBootstrapper(importer);
        var evidence = Evidence("0000789019") with
        {
            Ticker = ticker,
            MarketIdentifierCode = mic,
            Isin = isin,
        };

        var act = () => bootstrapper.ImportAsync(evidence);

        await act.Should().ThrowAsync<InvalidDataException>();
        await importer.DidNotReceiveWithAnyArgs().ImportListing(default);
    }

    [Fact]
    public async Task ImportAsync_RejectsMismatchedSourceIdentityBeforeWriting()
    {
        var importer = new EquityDirectoryIdentityImporter(Substitute.For<IServiceScopeFactory>());
        var bootstrapper = new MsftIssuerBootstrapper(importer);
        var evidence = Evidence("0000789019") with
        {
            SecIdentityCaptureJson = "{\"cik\":1017413,\"entityName\":\"APPLE INC\"}",
        };

        var act = () => bootstrapper.ImportAsync(evidence);

        await act.Should().ThrowAsync<InvalidDataException>();
        await importer.DidNotReceiveWithAnyArgs().ImportListing(default);
    }

    [Fact]
    public async Task ImportAsync_RejectsNonAllowlistedSourceUrlsBeforeWriting()
    {
        var importer = new EquityDirectoryIdentityImporter(Substitute.For<IServiceScopeFactory>());
        var bootstrapper = new MsftIssuerBootstrapper(importer);
        var evidence = Evidence("0000789019") with
        {
            ListingSourceUrl = "https://example.org/MSFT",
        };

        var act = () => bootstrapper.ImportAsync(evidence);

        await act.Should().ThrowAsync<InvalidDataException>();
        await importer.DidNotReceiveWithAnyArgs().ImportListing(default);
    }

    internal static MsftIssuerBootstrapEvidence Evidence(string cik) =>
        new(
            cik,
            "Microsoft Corporation",
            "US5949181045",
            "MSFT",
            "XNAS",
            "US",
            "USD",
            1m,
            MsftIssuerBootstrapper.CikSourceUrl,
            MsftIssuerBootstrapper.ListingSourceUrl,
            "{\"cik\":789019,\"entityName\":\"MICROSOFT CORPORATION\"}",
            "{\"ticker\":\"MSFT\",\"isin\":\"US5949181045\",\"mic\":\"XNAS\",\"country\":\"US\",\"currency\":\"USD\",\"issuerName\":\"Microsoft Corporation\"}"
        );
}
