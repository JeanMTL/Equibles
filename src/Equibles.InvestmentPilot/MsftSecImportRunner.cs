using Equibles.CommonStocks.Data.Models;
using Equibles.Data;
using Equibles.Integrations.Sec.Models.Responses;
using Equibles.Sec.Data.Models;
using Equibles.Sec.FinancialFacts.Data.Enums;
using Equibles.Sec.FinancialFacts.Data.Models;
using Equibles.Sec.FinancialFacts.HostedService.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Equibles.InvestmentPilot;

public sealed record MsftSecImportReport(
    string Cik,
    string EntityName,
    string SourceUrl,
    DateOnly FiledOnOrAfter,
    DateTimeOffset RetrievedAtUtc,
    int RequestCount,
    long DownloadedBodyBytes,
    int ObservationsInPayload,
    int CandidateObservationsAfterDateFloor,
    int ObservationsExcludedOutsideFiledDateRange,
    int UnsupportedTaxonomyObservations,
    int CandidateAccessionsAfterDateFloor,
    int PersistedSourceFactRows,
    int NewFactRows,
    int ChangedFactRows,
    int UnchangedFactRows,
    int RemovedFactRows,
    string[] Units
);

/// <summary>Validates database state, imports one bounded MSFT payload, then exits.</summary>
public sealed class MsftSecImportRunner(
    EquiblesFinancialDbContext db,
    FinancialFactsImportService importer,
    ISecCompanyFactsSource source,
    ILogger<MsftSecImportRunner> logger
)
{
    public const string Cik = "0000789019";
    public const string Ticker = "MSFT";
    public const string RequiredMigration = "20261112003749_InvestmentPilotFactProvenance";
    public static readonly DateOnly FiledDateFloor = new(2023, 1, 1);
    public static readonly TimeSpan TotalRunLimit = TimeSpan.FromMinutes(5);

    public async Task<MsftSecImportReport> RunOnceAsync(
        string contactEmail,
        CancellationToken cancellationToken
    )
    {
        SecContactEmail.Validate(contactEmail);
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(TotalRunLimit);

        await ValidateDatabaseAsync(deadline.Token);
        var issuer = await LoadExistingMsftIssuerAsync(deadline.Token);
        var before = await LoadFactsAsync(issuer.Id, deadline.Token);

        // Fetch occurs only after database schema and exact issuer identity are preflighted.
        var download = await source.FetchMsftAsync(contactEmail, deadline.Token);
        if (download.SourceUrl.AbsoluteUri != BoundedSecCompanyFactsSource.AllowedUrl)
            throw new InvalidDataException("The source returned a URL outside the MSFT SEC allowlist.");
        if (download.RequestCount != 1 || download.DownloadedBodyBytes > BoundedSecCompanyFactsSource.MaxResponseBytes)
            throw new InvalidDataException("The SEC fetch exceeded the configured one-request/byte budget.");
        if (download.Payload.Cik != 789019)
            throw new InvalidDataException("SEC payload CIK did not match the MSFT allowlist.");
        ValidateEntityName(download.Payload.EntityName);

        var filtered = FilterByFiledDate(
            download.Payload,
            FiledDateFloor,
            DateOnly.FromDateTime(DateTime.UtcNow),
            out var candidateCount,
            out var excludedObservationCount,
            out var unsupportedTaxonomyCount,
            out var units,
            out var accessions
        );
        if (candidateCount == 0)
            throw new InvalidDataException("No MSFT Company Facts observations meet the 2023-01-01 filing-date floor.");

        await importer.ImportProvidedFactsForPilot(issuer, filtered, deadline.Token);
        var after = await LoadFactsAsync(issuer.Id, deadline.Token);
        var added = after.Keys.Except(before.Keys).Count();
        var removed = before.Keys.Except(after.Keys).Count();
        var changed = after.Keys.Intersect(before.Keys).Count(key => after[key] != before[key]);
        var unchanged = after.Keys.Intersect(before.Keys).Count(key => after[key] == before[key]);

        var sourceFacts = await db.Set<FinancialFact>()
            .AsNoTracking()
            .Where(fact =>
                fact.EquityIssuerId == issuer.Id
                && fact.SourceCik == Cik
                && fact.SourceUrl == BoundedSecCompanyFactsSource.AllowedUrl
                && accessions.Contains(fact.AccessionNumber)
            )
            .CountAsync(deadline.Token);
        if (sourceFacts == 0)
            throw new InvalidDataException("The importer completed without facts from this payload's filed-date-filtered accessions carrying SEC provenance.");

        logger.LogInformation(
            "MSFT one-shot SEC import complete: requests={RequestCount}, bytes={Bytes}, candidates={Candidates}, new={NewRows}, changed={ChangedRows}, unchanged={UnchangedRows}, removed={RemovedRows}, sourcedFacts={SourcedFacts}",
            download.RequestCount,
            download.DownloadedBodyBytes,
            candidateCount,
            added,
            changed,
            unchanged,
            removed,
            sourceFacts
        );

        return new MsftSecImportReport(
            Cik,
            download.Payload.EntityName,
            download.SourceUrl.AbsoluteUri,
            FiledDateFloor,
            download.RetrievedAtUtc,
            download.RequestCount,
            download.DownloadedBodyBytes,
            download.ObservationCount,
            candidateCount,
            excludedObservationCount,
            unsupportedTaxonomyCount,
            accessions.Length,
            sourceFacts,
            added,
            changed,
            unchanged,
            removed,
            units
        );
    }

    private async Task ValidateDatabaseAsync(CancellationToken cancellationToken)
    {
        if (!await db.Database.CanConnectAsync(cancellationToken))
            throw new InvalidOperationException("Equibles database is not reachable; no SEC request was made.");

        var applied = (await db.Database.GetAppliedMigrationsAsync(cancellationToken)).ToHashSet(StringComparer.Ordinal);
        if (!applied.Contains(RequiredMigration))
            throw new InvalidOperationException(
                $"Required additive provenance migration {RequiredMigration} is not applied; no SEC request was made."
            );

        var pending = await db.Database.GetPendingMigrationsAsync(cancellationToken);
        if (pending.Any())
            throw new InvalidOperationException(
                "Database has pending migrations; the one-shot runner will not apply migrations. No SEC request was made."
            );
    }

    private async Task<EquityIssuer> LoadExistingMsftIssuerAsync(CancellationToken cancellationToken)
    {
        var matches = await db.Set<EquityIssuer>()
            .Include(issuer => issuer.Presentation)
            .ThenInclude(presentation => presentation.Listing)
            .Where(issuer => issuer.Cik == Cik)
            .ToListAsync(cancellationToken);
        if (matches.Count != 1)
            throw new InvalidOperationException("Expected exactly one existing Equibles issuer for CIK 0000789019; no SEC request was made.");

        var issuer = matches[0];
        if (issuer.Presentation?.Listing?.Ticker != Ticker || issuer.Presentation.Listing.MarketCountryCode != "US")
            throw new InvalidOperationException("The existing CIK record is not the expected US MSFT listing; no SEC request was made.");
        return issuer;
    }

    private async Task<Dictionary<FactKey, FactValue>> LoadFactsAsync(
        Guid issuerId,
        CancellationToken cancellationToken
    ) => await db.Set<FinancialFact>()
        .AsNoTracking()
        .Where(fact => fact.EquityIssuerId == issuerId)
        .ToDictionaryAsync(
            fact => new FactKey(
                fact.FinancialConceptId,
                fact.Unit,
                fact.PeriodStart,
                fact.PeriodEnd,
                fact.AccessionNumber,
                fact.DimensionsKey
            ),
            fact => new FactValue(
                fact.Value,
                fact.FiscalYear,
                fact.FiscalPeriod,
                fact.Form,
                fact.FiledDate,
                fact.SourceCik,
                fact.SourceUrl
            ),
            cancellationToken
        );

    private static CompanyFactsResponse FilterByFiledDate(
        CompanyFactsResponse response,
        DateOnly floor,
        DateOnly ceiling,
        out int candidateCount,
        out int excludedObservationCount,
        out int unsupportedTaxonomyObservationCount,
        out string[] units,
        out string[] accessions
    )
    {
        var facts = new Dictionary<string, Dictionary<string, CompanyFactConcept>>(StringComparer.Ordinal);
        var observedUnits = new HashSet<string>(StringComparer.Ordinal);
        var observedAccessions = new HashSet<string>(StringComparer.Ordinal);
        candidateCount = 0;
        unsupportedTaxonomyObservationCount = 0;

        foreach (var (taxonomy, concepts) in response.Facts)
        {
            var filteredConcepts = new Dictionary<string, CompanyFactConcept>(StringComparer.Ordinal);
            foreach (var (tag, concept) in concepts)
            {
                var filteredUnits = new Dictionary<string, List<CompanyFactValue>>(StringComparer.Ordinal);
                foreach (var (unit, values) in concept.Units)
                {
                    var selected = values.Where(value => value.Filed >= floor && value.Filed <= ceiling).ToList();
                    if (selected.Count == 0)
                        continue;
                    candidateCount += selected.Count;
                    if (!IsImporterSupportedTaxonomy(taxonomy))
                        unsupportedTaxonomyObservationCount += selected.Count;
                    observedUnits.Add(unit);
                    foreach (var accession in selected.Select(value => value.Accn).Where(value => !string.IsNullOrWhiteSpace(value)))
                        observedAccessions.Add(accession);
                    filteredUnits.Add(unit, selected);
                }
                if (filteredUnits.Count > 0)
                    filteredConcepts.Add(tag, new CompanyFactConcept
                    {
                        Label = concept.Label,
                        Description = concept.Description,
                        Units = filteredUnits,
                    });
            }
            if (filteredConcepts.Count > 0)
                facts.Add(taxonomy, filteredConcepts);
        }

        units = observedUnits.Order(StringComparer.Ordinal).ToArray();
        accessions = observedAccessions.Order(StringComparer.Ordinal).ToArray();
        var observationsInRange = response.Facts
            .Values
            .SelectMany(concepts => concepts.Values)
            .SelectMany(concept => concept.Units.Values)
            .SelectMany(values => values)
            .Count(value => value.Filed >= floor && value.Filed <= ceiling);
        excludedObservationCount = response.Facts
            .Values
            .SelectMany(concepts => concepts.Values)
            .SelectMany(concept => concept.Units.Values)
            .Sum(values => values.Count) - observationsInRange;
        return new CompanyFactsResponse
        {
            Cik = response.Cik,
            EntityName = response.EntityName,
            Facts = facts,
        };
    }

    private static bool IsImporterSupportedTaxonomy(string taxonomy) => taxonomy.ToLowerInvariant() is
        "us-gaap" or "dei" or "ifrs-full" or "srt" or "invest";

    private static void ValidateEntityName(string entityName)
    {
        if (string.IsNullOrWhiteSpace(entityName) || !entityName.Contains("MICROSOFT", StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("SEC entityName did not identify Microsoft; no facts were imported.");
    }

    private sealed record FactKey(
        Guid FinancialConceptId,
        string Unit,
        DateOnly PeriodStart,
        DateOnly PeriodEnd,
        string AccessionNumber,
        string DimensionsKey
    );

    private sealed record FactValue(
        decimal Value,
        int FiscalYear,
        SecFiscalPeriod FiscalPeriod,
        DocumentType Form,
        DateOnly FiledDate,
        string SourceCik,
        string SourceUrl
    );
}
