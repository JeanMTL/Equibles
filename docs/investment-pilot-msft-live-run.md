# MSFT one-shot SEC Company Facts run

This is an operator runbook only. It is not authorization to run SEC requests or deploy to the NAS. The command is MSFT-only and does not start `Equibles.Worker.Host`.

## Hard limits in code

- One `GET` to `https://data.sec.gov/api/xbrl/companyfacts/CIK0000789019.json`; no caller-supplied CIK, URL, submissions archive, index, filing archive, pagination, redirect, or retry.
- CIK must be `0000789019`; SEC `entityName` must identify Microsoft; the existing Equibles issuer must already resolve to exactly one US `MSFT` listing. The runner never creates or merges issuers.
- At most one request, 25 MiB response body, 45-second request timeout, 5-minute total run deadline (also enforced by the container's `timeout` process wrapper), and 100,000 Company Facts observations. A 403/429, unexpected content type/encoding, identity mismatch, excess budget, or timeout aborts the run without retry.
- Only observations with SEC filing date from `2023-01-01` through the retrieval date are passed to the existing `FinancialFactsImportService`. It records units (including currency units), accession, form, filed date, period, CIK, and official API source URL. The one-shot path skips share-count/derived-security updates.
- The executable requires the exact `--execute-msft-once` argument. It checks the provenance migration and refuses pending migrations before making the SEC request. It never applies schema migrations itself.

## First-run gates

1. Review and merge the Draft PR, then deploy the reviewed Web/MCP version and let the normal application migration process apply `20261112003749_InvestmentPilotFactProvenance`. This POC does not deploy or migrate the NAS.
2. Before any first write, take a verified backup of the Equibles database and confirm the restore procedure. Check that Web/MCP are healthy, the original Worker is stopped/disabled, and the live DB reports the required migration applied with no pending migrations.
3. On the NAS, edit the ignored local `.env` only: set a genuine `SEC_CONTACT_EMAIL` and `MSFT_PILOT_DB_CONNECTION_STRING` for the existing Equibles database using `Host=127.0.0.1;Port=5432` (the one-shot container shares only the existing DB container's network namespace). Keep credentials out of GitHub, logs, and chat.
4. From the project directory, run exactly once after the gates above:

   ```sh
   sudo docker compose -f docker-compose.yml -f docker-compose.investment-pilot.yml build msft-sec-once
   sudo docker compose -f docker-compose.yml -f docker-compose.investment-pilot.yml run --rm --no-deps msft-sec-once
   ```

   The profile keeps this service out of routine `compose up`; it has no published ports, mounts, restart loop, or dependency startup. `--no-deps` prevents Compose from starting/recreating the DB or any other service. The DB must already be healthy.
5. Stop and do not rerun automatically if the process reports a budget/identity/status error. Save its JSON result and local Compose resource summary; then inspect the current Web financials page and MCP revenue query for MSFT. The report includes request count/body bytes/retrieval time, facts outside the date window, unsupported-taxonomy observations, filtered accessions, persisted sourced rows, changed/new/unchanged/removed fact counts, and units. The importer accepts recognized taxonomies and does not expose a separate semantic “unknown tag” metric; unsupported taxonomies are counted. Missing/unmapped SEC facts are not evidence of complete financial statements.

## Migration and rollback

The migration only adds nullable `SourceCik` (`varchar(10)`) and `SourceUrl` (`varchar(2048)`) to `FinancialFact`; it does not rewrite existing rows. Its `Down()` drops those two columns. Migration application happens during the reviewed Web deployment, never in the one-shot executable.

Rolling back only the schema after the pilot has written rows removes the source metadata from those rows, so do not treat that as a complete data rollback. The importer can upsert existing MSFT facts and retire superseded filing-XBRL fills; therefore deleting all MSFT rows by CIK is not a safe rollback. If the one-shot run must be reversed, stop further writes and restore the verified pre-run Equibles database backup using the site's established restore procedure. This may discard later database writes, which is why a consistent backup and a quiet maintenance window are required before any future live run.

No commands in this runbook have been executed against the NAS in this code/test phase.
