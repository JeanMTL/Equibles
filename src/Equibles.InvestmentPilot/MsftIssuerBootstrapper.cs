using System.Text.Json;
using Equibles.CommonStocks.BusinessLogic.Directory;
using Equibles.CommonStocks.Data.Helpers;
using Equibles.CommonStocks.Data.Models;

namespace Equibles.InvestmentPilot;

/// <summary>
/// Imports one operator-reviewed pair of offline SEC identity and Nasdaq listing captures.
/// It performs no network requests and delegates persistence/conflict handling to Equibles'
/// existing transactional directory identity importer.
/// </summary>
public sealed class MsftIssuerBootstrapper(EquityDirectoryIdentityImporter identityImporter)
{
    public const string EvidenceFilePath = "/run/msft-issuer-evidence.json";
    public const string ListingSourceUrl = "https://www.nasdaq.com/market-activity/stocks/msft";
    public const string CikSourceUrl = BoundedSecCompanyFactsSource.AllowedUrl;
    public const string IssuerCik = "0000789019";
    public const string IssuerName = "Microsoft Corporation";
    public const string Isin = "US5949181045";
    public const string Ticker = "MSFT";
    public const string MarketIdentifierCode = "XNAS";
    public const string MarketCountryCode = "US";
    public const string TradingCurrency = "USD";
    public const string Source = "omnia-msft-single-issuer";
    private const int MaxEvidenceBytes = 128 * 1024;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
    };

    public async Task<Guid> ImportAsync(
        MsftIssuerBootstrapEvidence evidence,
        CancellationToken cancellationToken = default
    )
    {
        Validate(evidence);
        using var secCapture = JsonDocument.Parse(evidence.SecIdentityCaptureJson);
        using var listingCapture = JsonDocument.Parse(evidence.ListingCaptureJson);
        var input = new EquityDirectoryListingInput
        {
            Source = Source,
            SourceIssuerIdentifier = IssuerCik,
            IssuerCik = evidence.Cik,
            IssuerName = IssuerName,
            Isin = Isin,
            SecurityType = EquitySecurityKind.OrdinaryShare,
            Ticker = Ticker,
            MarketIdentifierCode = MarketIdentifierCode,
            MarketCountryCode = MarketCountryCode,
            TradingCurrency = TradingCurrency,
            QuoteUnitMultiplier = 1m,
            SourceUrl = ListingSourceUrl,
            PayloadJson = JsonSerializer.Serialize(
                new
                {
                    CikSourceUrl,
                    ListingSourceUrl,
                    SecIdentityCapture = secCapture.RootElement,
                    ListingCapture = listingCapture.RootElement,
                }
            ),
        };

        return await identityImporter.ImportListing(input, cancellationToken);
    }

    public static async Task<MsftIssuerBootstrapEvidence> ReadEvidenceFileAsync(
        CancellationToken cancellationToken = default
    )
    {
        var file = new FileInfo(EvidenceFilePath);
        if (!file.Exists || file.Length <= 0 || file.Length > MaxEvidenceBytes)
            throw new InvalidDataException(
                $"The MSFT evidence file must exist and be no larger than {MaxEvidenceBytes} bytes."
            );

        await using var stream = new FileStream(
            EvidenceFilePath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read
        );
        if (stream.Length <= 0 || stream.Length > MaxEvidenceBytes)
            throw new InvalidDataException(
                $"The MSFT evidence file must be no larger than {MaxEvidenceBytes} bytes."
            );
        var evidence = await JsonSerializer.DeserializeAsync<MsftIssuerBootstrapEvidence>(
            stream,
            JsonOptions,
            cancellationToken
        );
        Validate(evidence);
        return evidence;
    }

    private static void Validate(MsftIssuerBootstrapEvidence evidence)
    {
        if (
            evidence == null
            || CikNormalizer.Canonicalize(evidence.Cik) != "789019"
            || evidence.IssuerName != IssuerName
            || evidence.Isin != Isin
            || evidence.Ticker != Ticker
            || evidence.MarketIdentifierCode != MarketIdentifierCode
            || evidence.MarketCountryCode != MarketCountryCode
            || evidence.TradingCurrency != TradingCurrency
            || evidence.QuoteUnitMultiplier != 1m
            || evidence.CikSourceUrl != CikSourceUrl
            || evidence.ListingSourceUrl != ListingSourceUrl
            || !ValidSecIdentityCapture(evidence.SecIdentityCaptureJson)
            || !ValidListingCapture(evidence.ListingCaptureJson)
        )
            throw new InvalidDataException(
                "Bootstrap requires matching offline MSFT CIK and XNAS listing evidence from the two fixed official sources."
            );
    }

    private static bool ValidSecIdentityCapture(string json)
    {
        if (!ValidJsonSize(json))
            return false;
        try
        {
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;
            return root.ValueKind == JsonValueKind.Object
                && root.TryGetProperty("cik", out var cik)
                && cik.TryGetInt64(out var numericCik)
                && numericCik == 789019
                && root.TryGetProperty("entityName", out var entityName)
                && entityName.ValueKind == JsonValueKind.String
                && entityName.GetString()!.Contains("Microsoft", StringComparison.OrdinalIgnoreCase);
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static bool ValidListingCapture(string json)
    {
        if (!ValidJsonSize(json))
            return false;
        try
        {
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;
            return root.ValueKind == JsonValueKind.Object
                && StringFieldEquals(root, "ticker", Ticker)
                && StringFieldEquals(root, "isin", Isin)
                && StringFieldEquals(root, "mic", MarketIdentifierCode)
                && StringFieldEquals(root, "country", MarketCountryCode)
                && StringFieldEquals(root, "currency", TradingCurrency)
                && StringFieldEquals(root, "issuerName", IssuerName);
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static bool ValidJsonSize(string json) =>
        !string.IsNullOrWhiteSpace(json)
        && System.Text.Encoding.UTF8.GetByteCount(json) <= MaxEvidenceBytes;

    private static bool StringFieldEquals(JsonElement root, string name, string expected) =>
        root.TryGetProperty(name, out var value)
        && value.ValueKind == JsonValueKind.String
        && string.Equals(value.GetString(), expected, StringComparison.OrdinalIgnoreCase);
}

public sealed record MsftIssuerBootstrapEvidence(
    string Cik,
    string IssuerName,
    string Isin,
    string Ticker,
    string MarketIdentifierCode,
    string MarketCountryCode,
    string TradingCurrency,
    decimal QuoteUnitMultiplier,
    string CikSourceUrl,
    string ListingSourceUrl,
    string SecIdentityCaptureJson,
    string ListingCaptureJson
);
