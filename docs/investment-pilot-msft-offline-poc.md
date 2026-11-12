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

## Provenance represented by the current schema

The imported `FinancialFact` preserves the CIK through its related `EquityIssuer`, plus the
Company Facts taxonomy/tag, reported value, unit, period start/end, fiscal year/period, filing
form, filed date, accession number, and `FinancialFactSource.CompanyFacts`. The synthetic
submissions fixture cross-checks the CIK, accession, filing date, report date, form, and primary
document metadata.

The existing `FinancialFact` schema does **not** retain the Company Facts or filing source URL.
`DocumentId` is optional and this POC does not fabricate a `Document` record or claim an exact
filing URL was persisted. The source URL is therefore a known provenance gap, not a test pass.
Adding a URL field or immutable source-document linkage would require a separate schema and
migration review before real collection.

The numbers and accession in the fixtures are invented test values and must never be presented
as Microsoft's reported financials. No live SEC endpoint, paid data provider, Worker, NAS service,
or production database is used by this POC.
