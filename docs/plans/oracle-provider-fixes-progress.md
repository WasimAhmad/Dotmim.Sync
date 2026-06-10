# Oracle Provider Fixes — Progress Tracker

> Plan: [2026-06-10-oracle-provider-fixes.md](2026-06-10-oracle-provider-fixes.md)
> Source report: [docs/Oracle-Provider-Verification.md](../Oracle-Provider-Verification.md)
> Branch: `claude/oracle-provider-implementation`

Update this file as each task lands: set status, add the commit hash, note any deviation from the plan.

| # | Task | Fixes | Status | Commit | Notes |
|---|------|-------|--------|--------|-------|
| 0 | Baseline build + empty unit-test filter check | — | ✅ done | (no commit — verification only) | 0 errors; filter matches nothing |
| 1 | Rewrite `OracleScopeBuilder` (canonical v1.x schema, `DBMS_SQL.RETURN_RESULT`, case-preserved exists) | C1, C2 | ✅ done (spec ✅, quality ✅) | `fea1be64`, `4b43c13a`, `c78b8066` | Two plan amendments discovered & fixed: (1) ODP.NET rejects `DbType.Guid` → scope ids bind as String + `GuidToRaw` HEXTORAW byte-swap; (2) `OracleDbType.Clob` reports `DbType.Object` → params declared String, promoted to CLOB at execute time via `OracleScopeCommand` wrapper. `InternalsVisibility.cs` added here (Task 10 must not re-create). 11 unit tests passing. Minor carried forward: 1 new CA2100 (→ Task 11), interceptor-cast note (→ Task 15 docs). |
| 2 | (amended) `GetCommand` pre-creates Oracle-safe parameters; strip kept as defensive net | C3 + DbType.Guid | ✅ done (spec ✅, quality ✅) | `dfeebe48`, `e949ef1b` | Pre-created params exploit the framework's skip at Commands.cs:81 → no DbType.Guid throw, no ORA-01036. Review found+fixed: sync_row_count must be typed via `DbType` (ODP.NET dual-API output rule), filter params never CLOB, guid-string Raw binds, bool coercion, `(?![\w$#])` strip regex. 22 unit tests passing. Minor carried forward: optional `rawColumn == null` narrowing of the Guid.TryParse guard. |
| 3 | Filter custom joins in both select commands | C4 | ✅ done (spec ✅, quality ✅) | `d61454e9`, `c176bbe4` | `Join.Outer` → FULL OUTER JOIN (references emit invalid bare OUTER JOIN). Bonus: review uncovered pre-existing precedence bug — `CreateFilterWhereSide` now groups `((wheres) OR tombstone)` before the timestamp/scope guards (was silently bypassing both on filtered syncs). 27 unit tests. Parity caveats for Task 15/16: join-filtered deletes don't propagate (same as MySQL/SqlServer); `SELECT DISTINCT` + CLOB columns → ORA-00932 (docs note). |
| 4 | `OracleDatabaseBuilder`: USER_* views, case-preserved names, async, dead-code removal | M1 | ✅ done (spec ✅, quality ✅) | `e4d8245e` | TOCTOU in DropsTableIfExists accepted (Oracle <23c has no DROP IF EXISTS; runner holds connection). Doc nits for Task 15: RenameTable ignores newSchemaName (schema-local rename). |
| 5 | `ENABLE NOVALIDATE` + escaped table-name literals | M2 | ✅ done (spec ✅, quality ✅) | `37e16ec8`, `e7df4249` | NOVALIDATE state persists post-sync (SqlServer NOCHECK parity) — docs note for Task 15. Apostrophe-escape test added per review. |
| 6 | Transient detector: drop 1542/12154, add 60/2049, testable `IsTransient` | M4 | ✅ done (spec ✅, quality ✅) | `fdf78644`, `e7df4249` | ORA-17002 intentionally NOT added — live confirmation needed (Task 16); documented in code comment. |
| 7 | `GetDatabaseName()` = user/schema; `SyncException.InitialCatalog` | M5 | ✅ done (spec ✅, quality ✅) | `11f6a745` | Confirmed HelperDatabase round-trips the schema name correctly (old DataSource value was broken there). |
| 8 | Tracking `timestamp` index + 128-byte identifier guard | M6, M3 | ✅ done (spec ✅, quality ✅) | `5ddc13dd`, `e34eeb30` | PL/SQL block with two EXECUTE IMMEDIATE; derived names (PK_/_ts_idx) validated eagerly at construction. Docs notes for Task 15/16: re-provision with overwrite:true recovers a missing index after partial creation; 128-byte limit needs DB COMPATIBLE >= 12.2. |
| 9 | Single-evaluation timestamp clock | minor | ✅ done (spec ✅, quality ✅) | `5849dd13` | Formula verified term-by-term vs old constant. Task 16 probe added: CAST(ts AS DATE) must truncate (not round) fractional seconds. |
| 10 | RAW(16)→Guid inbound mapping (`InternalsVisibleTo` already landed in Task 1) | minor | ✅ done (controller-verified) | `678d9acd` | 6-line additive diff with 6-case theory; 47/47 unit suite verified by controller on the correct filter. |
| 11 | csproj parity (central version, SourceLink, no net7.0) + warning-clean build | packaging | ✅ done (review ✅) | `3164ba31` | 0 errors / 0 Oracle warnings on all 3 TFMs; packs as Dotmim.Sync.Oracle.1.3.0.nupkg; lock regenerated; CA1849/SA1514/CS0419 code fixes verified behavior-neutral (ordinals checked). |
| 12 | Register `OracleTcpFilterTests`/`OracleHttpTests`; commit EF wiring | I2, I3 | ✅ done (controller-verified) | `dbf148c4` | EF wiring was already committed pre-session in `2f8455dd`; this adds the two test classes (+34 lines, existing pattern) + genuinely-changed Tests/Samples lock files. SDK-drift noise in 9 unrelated provider lock files discarded. Classes compile; running them needs the live Oracle gate (Task 16). |
| 13 | CI: template docker step, `azure-pipelines-oracle.yml`, nuget pack | I4 | ✅ done (controller-verified) | `543a63af` | gvenzl/oracle-free:23-slim with readiness wait; 4 test jobs; Oracle pack added to BOTH Beta and Release nuget jobs matching sibling conventions; yaml parse-validated. |
| 14 | `Samples/HelloOracleSync` | I5 | ✅ done | `c4489a9b` | net8.0 console, SqlServer server → Oracle client; builds clean. |
| 15 | `docs/Oracle.md` + verification report status note | I6 | ✅ done | `1cd8b187` | Includes all review-discovered behavior notes (NOVALIDATE persistence, join-filter delete parity, DISTINCT+CLOB, interceptor wrapper, COMPATIBLE 12.2, overwrite recovery, schema-local rename). |
| 16 | Live-database validation (gvenzl/oracle-free; Tcp → Conflicts → Filter → Http) | gate | ☐ not started | | requires Docker/Oracle |

## Deferred by design

- **I7 — `UpdateMetadata`/`SelectMetadata` inline SQL:** intentionally not implemented; the MySQL provider ships without them (`MySqlSyncAdapter.cs:174-177`). Documented in `docs/Oracle.md` known limitations.

## Live-validation findings (fill during Task 16)

| Test class | Result | Defects found / fixed |
|---|---|---|
| OracleTcpTests | | |
| OracleConflictTests | | |
| OracleTcpFilterTests | | |
| OracleHttpTests | | |
