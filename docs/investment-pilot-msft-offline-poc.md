# MSFT offline import proof of concept

This POC is based on the NAS deployed Equibles revision `e6bf081dcd3658a6d41bd53e6d8df80d54351b36`.
It uses the existing `FinancialFactsImportService` parsing, quality, concept, upsert, fiscal
identity, and sync-status paths. It does not change the normal Worker or its registrations.

## One-shot, offline verification

From the Equibles repository root, run the integration test command below. The test fixtures
are explicitly synthetic and live only under `tests/Equibles.IntegrationTests/TestAssets/InvestmentPilot/`.
The test harness creates an isolated disposable ParadeDB database, seeds only MSFT/CIK
`0000789019`, passes the saved Company Facts JSON to `ImportProvidedFacts`, repeats the import,
then queries the inserted facts through the existing MCP tool. A separate test performs the same
import through the existing Web host and requests `/Stocks/MSFT/Financials`.

```powershell
dotnet test tests/Equibles.IntegrationTests/Equibles.IntegrationTests.csproj --filter "FullyQualifiedName~InvestmentPilotOfflineImport"
```

The import seam accepts an already-loaded response and validates its CIK against the issuer
before persistence. Tests substitute `ISecEdgarClient` and assert it is never called. The
integration entry point does not construct or run an application host containing background
workers; the Web test exercises only its existing test host and disposable test database.
Docker with the ParadeDB test image and the .NET 10 SDK are required to run these integration
tests. No NAS runtime configuration or production database is read or changed.

When a developer workstation has no Docker engine, the dedicated
`.github/workflows/msft-offline-poc.yml` job runs these same filtered tests on a disposable
GitHub-hosted runner. Testcontainers owns the temporary ParadeDB lifetime; the job has no NAS
credentials, does not publish database ports, and makes no SEC or other upstream API calls. This
is the preferred isolated execution path. Do not substitute the production NAS database. A NAS
fallback would need separate owner approval and an independently reviewed, resource-capped project
and database before any run.

## Provenance represented by the current schema

The importer now persists the ten-digit source CIK and the official per-CIK SEC Company Facts
endpoint on each imported fact, along with Company Facts taxonomy/tag, reported value, unit,
period start/end, fiscal year/period, filing form, filed date, accession number, and
`FinancialFactSource.CompanyFacts`. MCP's `GetFinancialFact` includes the source CIK and API
URL in each fact row. The synthetic submissions fixture cross-checks the CIK, accession,
filing date, report date, form, and primary document metadata.

The URL identifies the official Company Facts API payload source, not the individual filing's
primary document. This fixture includes a synthetic `primaryDocument` name but deliberately
does not fabricate a full filing URL or a `Document` row. Exact filing-document linkage remains
unverified and should be added only when a saved submissions record is paired with a real
filing's primary document metadata.

The numbers and accession in the fixtures are invented test values and must never be presented
as Microsoft's reported financials. No live SEC endpoint, paid data provider, Worker, NAS service,
or production database is used by this POC.
