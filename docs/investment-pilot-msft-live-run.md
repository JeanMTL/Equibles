# MSFT one-shot SEC Company Facts run

This is an operator runbook only. It is not authorization to deploy, modify the NAS database, or make a live SEC request. Keep the PR Draft until the owner reviews and separately authorizes the deployment and first live run.

## Production issuer preflight (read-only)

The one-shot runner requires exactly one issuer for CIK 0000789019 and a US presentation listing with ticker MSFT. Equibles may store SEC CIKs either zero-padded or canonical, so the preflight and runner compare both forms. The runner fails before contacting SEC if the match is absent, duplicated, or has a different presentation listing.

At the NAS SSH shell, run this one read-only command. It prompts for sudo interactively, performs a SELECT only, does not print credentials, and does not start/restart a service:

```sh
sudo docker compose -f /volume1/docker/investment-equibles/compose.yaml exec -T db psql -U equibles -d equibles -X -A -F '|' -c "SELECT i.\"Id\", i.\"Name\", i.\"Cik\", l.\"Ticker\", l.\"MarketCountryCode\", l.\"Active\" FROM \"EquityIssuer\" i LEFT JOIN \"EquityIssuerPresentation\" p ON p.\"EquityIssuerId\" = i.\"Id\" LEFT JOIN \"EquityListing\" l ON l.\"Id\" = p.\"EquityListingId\" WHERE ltrim(i.\"Cik\", '0') = '789019' ORDER BY i.\"Id\""
```

Expected evidence is exactly one row, with `MSFT|US|t` (case-insensitive ticker) and a Microsoft issuer name. Zero rows means no issuer is seeded; multiple rows or a different listing means ambiguous identity. In either case do not run the SEC importer. Do not insert rows with ad-hoc SQL or invoke the original Worker.

If there are zero rows, this PR provides a separate explicit initializer. Before invoking it, collect and review the SEC identity capture (`cik=789019`, Microsoft entity name) and Nasdaq listing capture (`MSFT`, `US5949181045`, `XNAS`, US, USD, Microsoft) from the fixed official URLs named in the evidence fields. Save them as a local JSON file at `/volume1/docker/investment-equibles/data/msft-issuer-evidence.json` with this schema:

```json
{
  "cik": "0000789019",
  "issuerName": "Microsoft Corporation",
  "isin": "US5949181045",
  "ticker": "MSFT",
  "marketIdentifierCode": "XNAS",
  "marketCountryCode": "US",
  "tradingCurrency": "USD",
  "quoteUnitMultiplier": 1,
  "cikSourceUrl": "https://data.sec.gov/api/xbrl/companyfacts/CIK0000789019.json",
  "listingSourceUrl": "https://www.nasdaq.com/market-activity/stocks/msft",
  "secIdentityCaptureJson": "{\"cik\":789019,\"entityName\":\"MICROSOFT CORPORATION\"}",
  "listingCaptureJson": "{\"ticker\":\"MSFT\",\"isin\":\"US5949181045\",\"mic\":\"XNAS\",\"country\":\"US\",\"currency\":\"USD\",\"issuerName\":\"Microsoft Corporation\"}"
}
```

The JSON above is a schema example, not evidence to use as-is. Fill the capture strings from reviewed official-source evidence and keep the file out of Git. The initializer makes no HTTP requests, accepts only these exact MSFT identifiers/source URLs, verifies both capture payloads, and writes through `EquityDirectoryIdentityImporter`'s transaction and conflict checks. It refuses to merge an existing different CIK owner or occupied ticker. It is idempotent. Take and verify the DB backup first; execute the initializer only under separate deployment/write authorization, then rerun the read-only SELECT and require exactly one row before the SEC import.

## Hard limits in code

- One `GET` to `https://data.sec.gov/api/xbrl/companyfacts/CIK0000789019.json`; no caller-supplied CIK/URL, submissions archive, index, filing archive, pagination, redirect, or retry.
- SEC response CIK must equal 789019 and entity name must identify Microsoft. The database must already contain exactly one identity-resolved MSFT/US issuer.
- At most one request, 25 MiB response body, 45-second request timeout, five-minute total run deadline (also enforced by the container's `timeout` wrapper), and 100,000 Company Facts observations. A 403/429, unexpected content type/encoding, identity mismatch, excess budget, or timeout aborts without retry.
- Only observations with SEC filing date from `2023-01-01` through retrieval date are passed to the existing `FinancialFactsImportService`. It records source CIK, official API URL, accession, form, filed date, period and unit/currency. The one-shot path skips share-count/derived-security updates.
- The executable requires `--execute-msft-once`. It checks database reachability, exact issuer identity, the provenance migration and absence of pending migrations before making the SEC request. It never applies migrations itself.
- The container has a 1 GiB memory limit, 0.5 CPU and 64 PIDs. This is headroom for the fully materialized JSON plus up to 100k observations; actual peak memory is not measured yet and remains a first-run acceptance item.

## Deployment and first-run gates

The NAS project root is `/volume1/docker/investment-equibles`; the checkout is `/volume1/docker/investment-equibles/source`. The production base file is `compose.yaml`, while the pilot override will be in the reviewed source checkout as `docker-compose.investment-pilot.yml`. Do not run these commands until that exact reviewed commit is present in `source` and deployment is separately authorized.

1. Review and merge the Draft PR only after owner approval. Back up the Equibles DB using the commands below. Confirm the backup archive can be listed. Restore-test it into a disposable ParadeDB clone before relying on it for a production rollback; no current NAS backup archive or restore test was found during this read-only check.
2. Deploy the reviewed Web/MCP build through the normal approved deployment process so the additive migration `20261112003749_InvestmentPilotFactProvenance` is applied. Do not run migration commands from this one-shot container. Then verify migration state and health before any pilot import. The migration adds only nullable `SourceCik` and `SourceUrl` columns; its `Down()` drops those columns. The CI integration runs against disposable ParadeDB; production migration state has not yet been read because Docker access requires interactive sudo.
3. Set a genuine `SEC_CONTACT_EMAIL` only in the ignored local `.env` at the project root. The compose file derives the pilot connection from the existing `POSTGRES_USER` and `POSTGRES_PASSWORD`; no duplicate DB secret is needed. Never print, commit or send `.env` values.
4. First validate the exact merged file paths without printing resolved configuration (run from PowerShell):

   ```powershell
   ssh -t omnia-nas 'cd /volume1/docker/investment-equibles && EQUIBLES_SOURCE_DIR=/volume1/docker/investment-equibles/source SEC_CONTACT_EMAIL=compose-check@example.invalid docker compose -f compose.yaml -f source/docker-compose.investment-pilot.yml config --quiet'
   ```

   This only parses/validates Compose; `compose-check@example.invalid` is a non-routable placeholder and no SEC request is made. It is safe only after the override exists in the deployed checkout. The service uses `network_mode: service:db`, publishes no ports, has no bind mounts and no dependency startup. `--no-deps` below prevents Compose from starting/recreating DB, Web, MCP or Worker.
5. If the issuer query returned zero rows, set `MSFT_ISSUER_EVIDENCE_FILE` in the one command below to the reviewed local evidence file and run the initializer once. It does not require `SEC_CONTACT_EMAIL`, does not contact SEC/Nasdaq, and does not start any dependency; still, it writes issuer/listing rows, so only run it after separate write authorization and the verified backup gate:

   ```sh
   sudo env EQUIBLES_SOURCE_DIR=/volume1/docker/investment-equibles/source MSFT_ISSUER_EVIDENCE_FILE=/volume1/docker/investment-equibles/data/msft-issuer-evidence.json docker compose -f /volume1/docker/investment-equibles/compose.yaml -f /volume1/docker/investment-equibles/source/docker-compose.investment-pilot.yml run --rm --no-deps msft-issuer-init
   ```

   Then repeat the read-only issuer query. Stop if it returns anything other than one Microsoft/US/MSFT row.

6. From the NAS shell in the project root, after the above gates and only once, build the pilot against the exact installed source tree and invoke it:

   ```sh
   sudo env EQUIBLES_SOURCE_DIR=/volume1/docker/investment-equibles/source docker compose -f /volume1/docker/investment-equibles/compose.yaml -f /volume1/docker/investment-equibles/source/docker-compose.investment-pilot.yml build msft-sec-once msft-issuer-init
   sudo env EQUIBLES_SOURCE_DIR=/volume1/docker/investment-equibles/source docker compose -f /volume1/docker/investment-equibles/compose.yaml -f /volume1/docker/investment-equibles/source/docker-compose.investment-pilot.yml run --rm --no-deps msft-sec-once
   ```

   Compose reads `.env` relative to the project root. It will fail closed if `SEC_CONTACT_EMAIL` is unset, DB is unhealthy, migration is missing/pending, or the exact issuer preflight fails. The profile keeps the service out of routine `compose up`; it has no published ports, restart loop or dependency startup.
7. Stop and do not rerun automatically if the process reports a budget, identity or status error. Save the JSON result and resource summary, then inspect MSFT through the current Web financials page and MCP revenue query. The report includes request count/body bytes/retrieval time, date-excluded and unsupported-taxonomy observations, accessions, sourced facts, fact-row changes and units. Unsupported taxonomies are counted; semantic unknown tags and missing/unmapped filings are not reported as completeness evidence.

## Backup, restore and rollback

Before any future live write, use the project's own DB container to make a custom-format dump. These commands write only a new archive in the dedicated `backups` directory:

```sh
umask 077
backup="/volume1/docker/investment-equibles/backups/equibles-pre-msft-$(date -u +%Y%m%dT%H%M%SZ).dump"
sudo docker compose -f /volume1/docker/investment-equibles/compose.yaml exec -T db sh -c 'pg_dump -U "$POSTGRES_USER" -d "$POSTGRES_DB" -Fc' > "$backup"
sudo chmod 600 "$backup"
sudo docker compose -f /volume1/docker/investment-equibles/compose.yaml exec -T db pg_restore --list - < "$backup" >/dev/null
```

Listing the archive proves it is readable, not that it can restore. Before the first live run, restore a copy into an isolated disposable ParadeDB database/container and verify its schema/data; never use production as the restore test. No existing backup archive was present in the dedicated backups directory during the check for this PR.

If a live import later needs rollback, stop further writes and restore the pre-run backup using the site's reviewed recovery procedure during a planned quiet window. Restoring over the live database can discard all writes after the backup and requires stopping dependent application writes; do not improvise a production restore. Dropping only the provenance columns with migration `Down()` erases source metadata and is not a data rollback. The importer can upsert existing MSFT facts and retire superseded filing-XBRL fills, so deleting MSFT rows is not safe.

No database, container, NAS source checkout, or service was changed by preparing this runbook. No real SEC request has been made.
