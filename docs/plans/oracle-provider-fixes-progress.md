# Oracle Provider Fixes — Progress Tracker

> Plan: [2026-06-10-oracle-provider-fixes.md](2026-06-10-oracle-provider-fixes.md)
> Source report: [docs/Oracle-Provider-Verification.md](../Oracle-Provider-Verification.md)
> Branch: `claude/oracle-provider-implementation`

Update this file as each task lands: set status, add the commit hash, note any deviation from the plan.

| # | Task | Fixes | Status | Commit | Notes |
|---|------|-------|--------|--------|-------|
| 0 | Baseline build + empty unit-test filter check | — | ✅ done | (no commit — verification only) | 0 errors; filter matches nothing |
| 1 | Rewrite `OracleScopeBuilder` (canonical v1.x schema, `DBMS_SQL.RETURN_RESULT`, case-preserved exists) | C1, C2 | ✅ done (spec ✅, quality ✅) | `fea1be64`, `4b43c13a`, `c78b8066` | Two plan amendments discovered & fixed: (1) ODP.NET rejects `DbType.Guid` → scope ids bind as String + `GuidToRaw` HEXTORAW byte-swap; (2) `OracleDbType.Clob` reports `DbType.Object` → params declared String, promoted to CLOB at execute time via `OracleScopeCommand` wrapper. `InternalsVisibility.cs` added here (Task 10 must not re-create). 11 unit tests passing. Minor carried forward: 1 new CA2100 (→ Task 11), interceptor-cast note (→ Task 15 docs). |
| 2 | Strip unreferenced binds in `EnsureCommandParameters` | C3 | ☐ not started | | |
| 3 | Filter custom joins in both select commands | C4 | ☐ not started | | |
| 4 | `OracleDatabaseBuilder`: USER_* views, case-preserved names, async, dead-code removal | M1 | ☐ not started | | |
| 5 | `ENABLE NOVALIDATE` + escaped table-name literals | M2 | ☐ not started | | |
| 6 | Transient detector: drop 1542/12154, add 60/2049, testable `IsTransient` | M4 | ☐ not started | | |
| 7 | `GetDatabaseName()` = user/schema; `SyncException.InitialCatalog` | M5 | ☐ not started | | |
| 8 | Tracking `timestamp` index + 128-byte identifier guard | M6, M3 | ☐ not started | | |
| 9 | Single-evaluation timestamp clock | minor | ☐ not started | | |
| 10 | RAW(16)→Guid inbound mapping + `InternalsVisibleTo` | minor | ☐ not started | | |
| 11 | csproj parity (central version, SourceLink, no net7.0) + warning-clean build | packaging | ☐ not started | | |
| 12 | Register `OracleTcpFilterTests`/`OracleHttpTests`; commit EF wiring | I2, I3 | ☐ not started | | |
| 13 | CI: template docker step, `azure-pipelines-oracle.yml`, nuget pack | I4 | ☐ not started | | |
| 14 | `Samples/HelloOracleSync` | I5 | ☐ not started | | |
| 15 | `docs/Oracle.md` + verification report status note | I6 | ☐ not started | | |
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
