# Oracle Provider — Verification Report (vs SqlServer reference & framework contracts)

> Audit of `Projects/Dotmim.Sync.Oracle` on branch `claude/oracle-provider-implementation`,
> verified against the SqlServer provider, the MySQL provider (canonical inline-SQL reference)
> and the Core orchestrator contracts. Build: **0 errors** (110 analyzer warnings).

## Verdict

The inline-SQL re-architecture is the correct Dotmim.Sync approach for Oracle and the
**table-level engine is faithful to the framework**: adapter wiring, conflict guards, change
selection SQL, triggers, tracking table and the no-stored-procedures pattern all match the
reference providers. However, **`OracleScopeBuilder` was never migrated to the v1.x scope
model** — it still implements a pre-v1.0 / invented schema with parameter names the
orchestrator cannot bind. Sync will fail at the very first step (scope handling), before any
table command runs. That, plus a handful of targeted gaps below, is what separates this from
a functional provider.

---

## 1. What is correct (verified against the framework)

| Area | Verified against | Status |
|---|---|---|
| `ParameterPrefix => ":"`, `BindByName = true` on every command | `DbSyncAdapter.cs:33`, `BaseOrchestrator.SetCommands.cs` | ✅ |
| Bulk off via `base(..., false)`; batch types map to single-row text | MySQL/SQLite pattern | ✅ |
| `SupportsOutputParameters` left at base default `true` (`DbSyncAdapter.cs:38`) + `:sync_row_count := SQL%ROWCOUNT` OUT bind in PL/SQL blocks | `InternalSetUpsertsParameters`, `ApplyRow` read-back | ✅ |
| Conflict guard `(v_ts IS NULL OR v_ts <= :sync_min_timestamp OR v_scope = :sync_scope_id OR :sync_force_write = 1)` | identical to SqlServer MERGE guard and MySQL SP guard | ✅ |
| `SelectChanges` (RIGHT JOIN tracking, `timestamp > :sync_min_timestamp`, scope exclusion), `SelectInitializedChanges` (LEFT JOIN + UNION tombstones), `SelectRow` | MySQL `GetChanges` templates | ✅ |
| Triggers: `MERGE … USING DUAL`, `update_scope_id = NULL` for local changes, shared clock | SqlServer/MySQL trigger semantics | ✅ |
| One clock: `TimestampValue` used in triggers, apply-merge, `UpdateUntrackedRows` **and** `GetLocalTimestamp` | MySQL `ROUND(UNIX_TIMESTAMP(...)*10000)` pattern | ✅ |
| Tracking table columns: PKs, `update_scope_id`, `timestamp`, `sync_row_is_tombstone`, `last_change_datetime` | canonical schema | ✅ |
| `UpdateMetadata`/`SelectMetadata` → `(default, false)` | MySQL does exactly this (`MySqlSyncAdapter.cs:174-177`) | ✅ acceptable parity |
| Stored-proc builder methods → `null` | SQLite pattern | ✅ |
| `Reset` deletes base **then** tracking (delete-trigger tombstones get cleaned); framework does bind `sync_row_count` for Reset | `InternalSetResetParameters` | ✅ |
| Scope ids as `RAW(16)`: orchestrator converts param values via `parameter.DbType` (`TryConvertFromDbType`) and reads with `reader.GetGuid()` | `ScopeInfos.cs:160-170`, `ScopeInfoClients.cs:358` | ✅ viable (once names fixed, see C1) |
| `OracleDbMetadata` implements all 10 `DbMetadata` abstracts, outbound on managed `DbType` | `DbMetadata.cs` | ✅ |
| CoreProvider surface complete (`CanBeServerProvider`, `ConstraintsLevelAction.OnTableLevel`, `GetScopeBuilder/SyncAdapter/DatabaseBuilder/Metadata`, `ShouldRetryOn`, `EnsureSyncException`) | `CoreProvider.cs` | ✅ |
| Test harness plumbing: `ProviderType.Oracle`, `HelperDatabase` (user=database model), `OracleTcpTests`, `OracleConflictTests` | `Setup.cs:554-588` | ✅ partial (see I2) |

---

## 2. Critical — must fix before any sync can run

### C1. `OracleScopeBuilder` implements the wrong scope schema and wrong parameter names
The framework (v1.x) writes/reads scopes by **canonical column & parameter names**:

- `scope_info` (PK = `sync_scope_name` only — **no `sync_scope_id`**):
  `sync_scope_name`, `sync_scope_schema`, `sync_scope_setup`, `sync_scope_version`,
  `sync_scope_last_clean_timestamp`, `sync_scope_properties`
  (`BaseOrchestrator.ScopeInfos.cs:469-474` set, `:490-495` read).
- `scope_info_client` (PK = `sync_scope_id, sync_scope_name, sync_scope_hash`):
  `sync_scope_parameters`, `scope_last_sync_timestamp`, `scope_last_server_sync_timestamp`,
  `scope_last_sync_duration`, `scope_last_sync`, `sync_scope_errors`, `sync_scope_properties`
  (`ScopeInfoClients.cs:337-346` set, `:354-368` read).

`OracleScopeBuilder` instead creates an old/invented shape (`sync_scope_id` inside
`scope_info`, `sync_scope_client_id`, `sync_scope_client_name`, `sync_scope_filters`,
`sync_scope_last_client_sync_timestamp`, missing `sync_scope_hash`,
`sync_scope_last_clean_timestamp`, `sync_scope_errors`) and names its parameters
`:scopeName`, `:schema`, `:lastSync`, … — `DbSyncAdapter.InternalGetParameter` only probes
`@x` / `:x` / `in_x` / `x` for the canonical name `x`, so **no parameter ever receives a
value**, and the readers throw on missing columns.

**Fix:** rewrite `OracleScopeBuilder` 1:1 against `SqlScopeBuilder` /
`MySqlScopeInfoBuilder`: exact column names (quoted lowercase to match the rest of the
provider), parameters named `:sync_scope_name`, `:sync_scope_schema`, …, `scope_info`
keyed by name only, `scope_info_client` keyed by (id, name, hash). Keep `RAW(16)` for
`sync_scope_id` (works with `GetGuid`/`DbType.Guid`) or use `VARCHAR2(36)` like MySQL —
either is fine once names are canonical. CLOB for the four JSON columns is correct.

### C2. Scope-table exists check can never match the created table
`GetExistsScopeInfoTableCommand` / `NeedToCreateScopeInfoTableAsync` upper-case the name
(`'SCOPE_INFO'`, `OracleScopeBuilder.cs:136,517,542`) but `CREATE TABLE "scope_info"` is
quoted/case-preserved → stored as lowercase in `USER_TABLES`. First provision succeeds,
every subsequent run sees "not exists" → re-issues CREATE → `ORA-00955`.
**Fix:** one casing policy. Recommended: case-preserved everywhere (drop the `.ToUpper()`),
matching `OracleTableBuilder.CreateExistsTableCommand` which already compares case-preserved.

### C3. Unreferenced bind variables risk `ORA-01036`
The orchestrator adds parameters the Oracle SQL never references:
- `SelectRow`: framework adds `:sync_scope_id` + `:sync_row_count` (OUT) (`SetCommands.cs:77-89`); SQL uses only PK binds.
- `DeleteMetadata`: framework adds **all PK params** + `:sync_row_count`; SQL uses only `:sync_row_timestamp`.

SqlClient/MySqlConnector tolerate extra parameters; ODP.NET (BindByName) generally raises
`ORA-01036 illegal variable name/number`. SqlServer handles its own variant of this in
`EnsureCommandParameters` (removes `sync_row_count` for metadata commands) and
`EnsureCommandParametersValues` (nulls PKs for DeleteMetadata).
**Fix:** in `OracleSyncAdapter.EnsureCommandParameters`, remove any parameter whose
`:name` does not appear in `CommandText` (regex on word boundary). This also future-proofs
filter commands. Must be validated against a live Oracle once available.

### C4. Filter joins (`filter.Joins`) are not emitted
`CreateSelectIncrementalChangesCommand` / `CreateSelectInitializedChangesCommand` handle
`filter.Wheres` + `filter.CustomWheres` but ignore `filter.Joins`. SqlServer
(`AppendFilterJoins`) and MySQL (`[CUSTOM_JOINS]`) emit the join clauses; a filter whose
where references another table generates SQL that references an un-joined table → invalid.
**Fix:** port `CreateFilterCustomJoins` from MySQL/SqlServer into `OracleObjectNames` and
append between the tracking JOIN and the WHERE. Then register `OracleTcpFilterTests`.

---

## 3. Major — correctness/robustness gaps

| # | Issue | Location | Fix |
|---|---|---|---|
| M1 | `OracleConnection.Database` returns an **empty string** in ODP.NET, so `ExistsTableAsync`, `TableExistsAsync`, `ProcedureExistsAsync`, `SchemaExistsAsync`, `TriggerExistsAsync`, `TypeExistsAsync` query `ALL_TABLES WHERE OWNER = ''` when no schema is passed → always false (silently). Also these methods upper-case names while creation is case-preserving. | `OracleDatabaseBuilder.cs:80,117,148-149,186-187,224-225,469-470` | Use `USER_*` views (current schema) when no schema given, or `SYS_CONTEXT('USERENV','CURRENT_SCHEMA')`; align casing with creation policy. |
| M2 | `EnableConstraints` uses plain `ENABLE` (= ENABLE **VALIDATE**): Oracle rescans all rows and the command fails if any orphan exists; SqlServer re-enables with NOCHECK semantics (`CHECK CONSTRAINT ALL` without `WITH CHECK`). | `OracleObjectNames.cs:429-444` | `ENABLE NOVALIDATE` for parity. Also consider scoping disable/enable to constraints **on** the table only (each synced table gets its own command), and escape `'` in the interpolated table name. |
| M3 | Identifier length: trigger names append `_insert_trigger` etc., tracking adds `_tracking` with no length guard. OK on 12.2+ (128-byte identifiers); overflows the 30-byte limit on older versions. | `OracleObjectNames.cs:116-129` | State the 19c+ floor in docs (plan already targets it) or add a deterministic-hash truncation. |
| M4 | Transient detector retries on permanent errors: `1542` is mislabeled ("table or view does not exist" is **942**; 1542 = offline tablespace — neither transient); `12154` (cannot resolve connect identifier) is a config error. | `OracleTransientExceptionDetector.cs:17,34` | Remove 1542/12154; consider adding `ORA-00060` (deadlock) and `ORA-02049` (distributed lock timeout). |
| M5 | `GetDatabaseName()` returns `builder.DataSource` (host/TNS alias). In the Oracle model "database" = user/schema; SqlServer returns `InitialCatalog`. Affects logs, exception enrichment, and anything keying on database name. | `OracleSyncProvider.cs:102` | Return `builder.UserID`; set `syncException.InitialCatalog` likewise in `EnsureSyncException`. |
| M6 | No index on the tracking table. SqlServer creates `(timestamp_bigint, update_scope_id, sync_row_is_tombstone, PKs)` — `SelectChanges`/`DeleteMetadata` will full-scan large tracking tables. | `OracleObjectNames.CreateTrackingTableScript` | Add `CREATE INDEX ix_<tracking>_ts ON <tracking>("timestamp")` (or the composite) in `GetCreateTrackingTableCommandAsync`. |

---

## 4. Minor / polish

- `TimestampValue` calls `SYS_EXTRACT_UTC(SYSTIMESTAMP)` twice (seconds part + `FF6` part); a
  second-boundary between evaluations can skew one reading. Compute both parts from a single
  expression, e.g. `EXTRACT`-based on `(SYS_EXTRACT_UTC(SYSTIMESTAMP) - TIMESTAMP '1970-01-01 00:00:00')`.
- Oracle-sourced GUIDs: `GetManagedType` maps `RAW` → `byte[]`, so a RAW(16) Guid column loses
  Guid-ness when Oracle is the schema source (MySQL maps `char(36)` → Guid). Consider mapping
  `RAW(16)` → `Guid` as a heuristic, or document.
- `OracleDatabaseBuilder` uses `Task.Run` / `ContinueWith().Unwrap()` wrappers
  (`GetHelloAsync`, `ExistsTableAsync`, `DropsTableIfExistsAsync`, `RenameTableAsync`) —
  plain async/await; most CA1849 warnings cluster here.
- `GetHelloAsync` queries `V$VERSION` (needs privileges in locked-down environments);
  `PRODUCT_COMPONENT_VERSION` is a safer default.
- `OracleScopeBuilder.GetLocalTimestampAsync` (static) duplicates `GetLocalTimestampCommand`
  and has a useless `catch { throw; }` — remove.
- csproj: `Company` is `Microsoft`, `Version` pinned `1.0.0`, `favicon.ico` packed —
  align with the other provider csprojs before packaging.
- 110 analyzer warnings (CA1849 sync-over-async, CA1822 static members, CA2249) — the other
  providers build warning-clean; clean before PR.

## 5. Missing components (vs SqlServer provider & repo conventions)

| # | Component | Status |
|---|---|---|
| I1 | `OracleScopeBuilder` v1.x rewrite | **missing** (C1/C2) |
| I2 | `OracleTcpFilterTests`, `OracleHttpTests` in `Setup.cs` (Postgres/MySql/MariaDB all have 4 classes; Oracle has 2) | missing — gated on C4 + EF model |
| I3 | EF Core Oracle support in `AdventureWorksContext` (test DB materialization) | WIP, uncommitted |
| I4 | CI: no Oracle container/job in any pipeline yml; not packed in `azure-pipelines-nuget.yml` | missing |
| I5 | `Samples/` Oracle walkthrough | missing |
| I6 | Provider docs (identifier-casing contract, 19c+ floor, type table, no-stored-proc design) | missing |
| I7 | (Optional) `UpdateMetadata`/`SelectMetadata` inline SQL — MySQL ships without them, SqlServer has them; not required for the standard sync paths | parity-optional |

## 6. Suggested order of work

1. **C1 + C2** — rewrite scope builder (blocks everything; ~1 day against SqlScopeBuilder).
2. **C3** — strip unreferenced binds in `EnsureCommandParameters` (small, defensive).
3. **M1** — fix `OracleDatabaseBuilder` owner/casing (blocks HelperDatabase-driven tests).
4. First live run: `OracleTcpTests` single-table SqlServer→Oracle, then fix what surfaces
   (`PrepareAsync` on PL/SQL blocks, `DbType.Guid`↔RAW round-trip, clock resolution).
5. **C4 + I2** — filter joins + filter/http test classes.
6. M2–M6, minors, then I3–I6 (CI, sample, docs, packaging).
