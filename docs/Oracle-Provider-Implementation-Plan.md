# Oracle Provider — Implementation Plan to Reach "Fully Functional"

> Status of `Projects/Dotmim.Sync.Oracle` as analyzed against the core framework
> (`Projects/Dotmim.Sync.Core`) and the complete reference providers (MySQL, SQLite,
> PostgreSQL, SQL Server).

## 1. Verdict

The Oracle provider is **scaffolding-complete but operationally non-functional**. All the
required classes exist and compile-shaped against the abstract contracts, and the "easy"
surface (connection handling, scope-info table CRUD, exists/drop DDL, transient-error
detection) is largely in place. However, **the core change-tracking engine is missing or
wired incorrectly**, so an end-to-end sync cannot succeed today.

The single most important structural problem: the adapter routes the change-selection and
apply operations (`SelectChanges`, `SelectInitializedChanges`, `UpdateRow`, `DeleteRow`,
`Reset`) to **Oracle stored procedures that are never generated** — `OracleTableBuilder`
`GetCreateStoredProcedureCommandAsync` returns the placeholder `SELECT 1 FROM DUAL`
(`Builders/OracleTableBuilder.cs:494-505`). Even if they were generated, the framework's
parameter model cannot drive a result-set-returning Oracle stored procedure (no
`SYS_REFCURSOR` is supplied). The correct design — used by MySQL, SQLite and PostgreSQL — is
**inline SQL (`CommandType.Text`)** for selects/metadata and lightweight **anonymous PL/SQL
blocks** for the row upsert/delete.

This document inventories every issue and lays out a phased plan to make the provider real.

---

## 1b. Implementation status (this branch)

A first pass of the functional core has been implemented (re-architected to the inline-SQL design):

- **Done (code-complete, unverified — no build/Oracle env available in this session):**
  - **Adapter (Phase 1):** `ParameterPrefix => ":"`, `BindByName = true` on every command,
    all command types routed to inline `CommandType.Text`, bulk disabled, boolean coercion.
  - **Object names / engine (Phase 4):** real SQL for `SelectChanges`,
    `SelectInitializedChanges`, `SelectRow`; anonymous PL/SQL blocks for `UpdateRow`/`DeleteRow`
    (conflict-guarded, `:sync_row_count := SQL%ROWCOUNT`); `DeleteMetadata`, `Reset`,
    `UpdateUntrackedRows`, per-table enable/disable constraints; basic filter support.
  - **Tracking + clock + triggers (Phase 3):** canonical tracking table (`timestamp` column),
    one shared UTC epoch clock used by triggers **and** `GetLocalTimestamp`, MERGE-into-tracking
    triggers with no mutating-table read.
  - **Metadata (Phase 2):** rewritten to map on the managed `DbType` (dead switch removed).
  - **Table builder (Phase 5):** stored-proc methods are no-ops; `GetColumnsAsync`,
    `GetPrimaryKeysAsync`, `GetRelationsAsync` fixed; consistent (no-`ToUpper`) identifier
    lookups; robust column-type resolver.
  - **Scope builder (Phase 6):** GUID columns `RAW(16)` + `BindByName`; CLOB binds for large
    schema/setup JSON; `GetLocalTimestamp` aligned to the shared clock.
  - **Database builder:** `GetTableAsync`/`GetAllTablesAsync` implemented; rename/`Console`
    issues fixed.
  - **GUID strategy:** stored as `RAW(16)` everywhere (compatible with ODP.NET `DbType.Guid`
    binding and `OracleDataReader.GetGuid`).

- **Test-harness plumbing (Phase 7, partial — done):**
  - `ProviderType.Oracle = 32`; `HelperDatabase` fully wired (connection strings +
    admin connection, `GetSyncProvider`, `GetDatabaseType`, pools, and
    create/drop/exists/truncate/script with the Oracle "database = schema/user" model);
    `appsettings.json` Oracle entries; Oracle project reference added to the test project;
    Oracle added to `Dotmim.Sync.slnx`.

- **Remaining / must verify against a live Oracle DB (Phase 0, 7-rest, 8):**
  - Build the project and run against Oracle (XE/Free 23c or 19c+). Validate ODP.NET specifics:
    `PrepareAsync` on anonymous PL/SQL blocks, tolerance of unreferenced OUT params under
    `BindByName`, and `DbType.Guid ↔ RAW(16)` round-trips.
  - Verify the timestamp clock's monotonicity/resolution under load (sequence-backed fallback
    if needed).
  - **EF-Core test model support (the gate on the TcpTests matrix):** the test databases are
    materialized via EF Core; add an Oracle EF Core provider (`Oracle.EntityFrameworkCore`) and
    Oracle-compatible model configuration, then register `OracleTcpTests`/filter/conflict/http
    classes in `Setup.cs`.
  - Oracle CI container, NuGet packaging, sample, and filter-sync correctness.

---

## 2. How a Dotmim.Sync provider must behave (the mental model)

Understanding three framework mechanics explains most of the required changes.

### 2.1 The orchestrator builds and binds the parameters, not the provider

`BaseOrchestrator.InternalGetCommandAsync` (`Orchestrators/Commands/BaseOrchestrator.Commands.cs:59`)
calls `syncAdapter.GetCommand(...)` to obtain a command with **only `CommandText`/`CommandType`
set**. If the returned command has no parameters, the orchestrator adds them generically via
`InternalSet*Parameters` (`Orchestrators/Commands/BaseOrchestrator.SetCommands.cs`). Each
parameter is named:

```csharp
p.ParameterName = $"{syncAdapter.ParameterPrefix}{columnNames.NormalizedName}";   // e.g. @ProductId
p.ParameterName = $"{syncAdapter.ParameterPrefix}sync_scope_id";                  // e.g. @sync_scope_id
```

So the provider only controls **`ParameterPrefix`** and the **SQL text**; the parameter set,
order, and `DbType` are fixed by the framework. The generated SQL must therefore reference
exactly those bind names. Parameter lookups later are prefix-tolerant
(`DbSyncAdapter.InternalGetParameter` tries `@x`, `:x`, `in_x`, `x` —
`DbSyncAdapter.cs:110-119`), but **the literal names created in the command use the provider's
`ParameterPrefix`**.

**Consequence for Oracle:** the adapter must override `ParameterPrefix => ":"` and every piece
of generated SQL must use `:name` bind variables whose names match
`GetParsedColumnNames(column).NormalizedName` and the `sync_*` names.

### 2.2 The command is always `Prepare()`d and ODP.NET binds by position by default

After parameters are set, the orchestrator unconditionally calls `command.PrepareAsync()`
(`BaseOrchestrator.Commands.cs:144`). ODP.NET's `OracleCommand.BindByName` defaults to
**`false`** (positional binding). The framework adds parameters in a fixed order that will
**not** match the order they appear in our SQL, and our SQL uses named binds — so
**`BindByName = true` must be set on every `OracleCommand`** or every command will bind the
wrong values (or fail).

### 2.3 Change tracking is timestamp-based and must share one clock

The algorithm (see `docs/HowDoesItWorks.rst`) depends on a per-row, monotonically increasing
`timestamp` stored in the tracking table, compared against `sync_min_timestamp`. The value
returned by `GetLocalTimestampCommand` **must be drawn from the same clock** that the triggers
write into the tracking table. MySQL uses one expression everywhere:

```sql
ROUND(UNIX_TIMESTAMP(CURRENT_TIMESTAMP(6)) * 10000)   -- in triggers AND GetLocalTimestamp
```

Row-count for applied rows is read back from the `sync_row_count` **output** parameter
(`Orchestrators/ApplyChanges/BaseOrchestrator.ApplyRow.cs:74-80,155-161`) when
`SupportsOutputParameters == true`; otherwise the `ExecuteNonQuery` return value is used.

---

## 3. Gap analysis

References are `file:line` within `Projects/Dotmim.Sync.Oracle` unless noted.

### 3.1 Critical blockers — sync cannot run at all

| # | Issue | Location | Why it breaks |
|---|-------|----------|---------------|
| C1 | **Stored procedures never generated** — placeholder `SELECT 1 FROM DUAL`. | `Builders/OracleTableBuilder.cs:494-505` | Adapter calls these procedures for upsert/delete/select; they don't exist. |
| C2 | **Selects routed to stored procedures.** `SelectChanges`, `SelectInitializedChanges`, `UpdateRow`, `DeleteRow`, `Reset` use `CommandType.StoredProcedure`. | `OracleSyncAdapter.cs:55-103,142-146` | The orchestrator supplies no `SYS_REFCURSOR`; Oracle SPs can't stream a result set to the reader this way. Must be inline `CommandType.Text`. |
| C3 | **No `SelectChanges` / `SelectInitializedChanges` / `UpdateRow` / `DeleteRow` SQL exists anywhere.** `GetCommandName` only covers SelectRow, En/DisableConstraints, DeleteMetadata, UpdateUntrackedRows, Reset. | `Builders/OracleObjectNames.cs:143-162` | The heart of the engine (join base⨝tracking filtered by timestamp/scope) is absent. |
| C4 | **`ParameterPrefix` not overridden** (inherits `"@"`). | `OracleSyncAdapter.cs` (missing) / base `DbSyncAdapter.cs:33` | Framework emits `@name` params; Oracle needs `:name`. |
| C5 | **`BindByName` never set to `true`.** | `OracleSyncAdapter.GetCommand` | Positional binding + named SQL ⇒ wrong/failed binds after `Prepare()`. |
| C6 | **No monotonic `timestamp` mechanism.** Tracking table has `update_timestamp NUMBER` populated by `(SELECT MAX(update_timestamp)+1 FROM tracking)` inside row triggers; `GetLocalTimestamp` returns an unrelated SCN (`DBMS_FLASHBACK.GET_SYSTEM_CHANGE_NUMBER`). | `Builders/OracleTableBuilder.cs:207,211,226,230,245,249`; `Builders/OracleScopeBuilder.cs:728-734` | Two different clocks ⇒ change detection is incoherent; `MAX()+1` is racy, non-monotonic across tables, and triggers a mutating-table risk. |
| C7 | **Tracking column named `update_timestamp`, not `timestamp`.** | `Builders/OracleTableBuilder.cs:166` | Select-changes / metadata-cleanup SQL across the framework expects a `timestamp` column. |
| C8 | **Guid parameters bound as `DbType.Guid`** while scope/tracking GUIDs are `VARCHAR2(36)`. | `Builders/OracleScopeBuilder.cs:207,273,335…`; orchestrator sends `sync_scope_id` as `DbType.Guid`. | ODP.NET has no native Guid type ⇒ bind/convert failure. Must map Guid→`VARCHAR2(36)` string. |

### 3.2 Major correctness bugs (block real use even once wired)

| # | Issue | Location |
|---|-------|----------|
| M1 | **`OracleDbMetadata` type mapping is non-functional.** Every method switches on `columnDefinition.GetDbType().ToString()` (an ADO `System.Data.DbType` name like `String`/`Int32`/`Guid`) but the `case` labels are Oracle native names (`VARCHAR2`, `NUMBER`, …), so **all paths fall to the default**. `GetType`→always `string`, `GetDbType`→always `String`, `GetOwnerDbType`→always `Varchar2`, `IsNumericType`→always `false`, `GetPrecisionAndScale`→`(0,0)`. | `Manager/OracleDbMetadata.cs:92-307` |
| M2 | **DDL type mapping only works SQL-Server→Oracle.** `GetOracleColumnTypeString` switches on SQL-Server type names from `OriginalTypeName`; when Oracle is the source these are Oracle names ⇒ default `VARCHAR2(4000)`, losing types. | `Builders/OracleTableBuilder.cs:257-332` |
| M3 | **`GetColumnsAsync` unfinished/buggy** — computes `isPrimaryKey` then does `syncColumn.ColumnName = columnName` "to satisfy the compiler"; never maps Oracle type back to a managed `DbType`; `IsAutoIncrement` hardcoded false. | `Builders/OracleTableBuilder.cs:640-662` |
| M4 | **`GetRelationsAsync` never attaches key columns** to the relation (placeholder comment) and self-joins `USER_CONS_COLUMNS` on `c.POSITION = r.POSITION` for the *same* constraint, so FK relations are empty/incorrect. | `Builders/OracleTableBuilder.cs:719-777` |
| M5 | **Triggers `MERGE` into the tracking table from a row-level trigger** and read `MAX(update_timestamp)` from it ⇒ ORA-04091 mutating-table risk and non-deterministic ordering. | `Builders/OracleTableBuilder.cs:179-255` |
| M6 | **Inconsistent identifier case strategy.** Objects are created **quoted** (`"Product"`, case-preserving) but existence/lookup queries compare `TABLE_NAME = :name` with `:name` forced to **UPPER**. Mixed-case names ⇒ exists checks miss ⇒ broken idempotency/provisioning. | e.g. `OracleTableBuilder.cs:375-389,70`; `OracleObjectNames.cs:40-42` |
| M7 | **30-char truncation by blunt `Substring(0,30)`** for tracking/SP/trigger names risks collisions for longer table names; trigger name not uppercased while checks are. | `OracleObjectNames.cs:80-89,133-138,167-176` |
| M8 | **`Reset` uses `TRUNCATE`** (DDL, non-transactional, auto-commits) and only clears tracking, not base rows. | `OracleObjectNames.cs:284-287` |
| M9 | **`UpdateUntrackedRows` MERGE is malformed** — reuses alias `t` for both target and inner join, sets `update_timestamp = s.sync_min_timestamp`, wrong tombstone/scope semantics. | `OracleObjectNames.cs:261-282` |
| M10 | **`AddCommandParameterValue` uses fragile length heuristics** (string>4000→CLOB, byte[]>2000→BLOB), bool→0/1, and never handles Guid→string; discards proper `OracleDbType` from metadata. | `OracleSyncAdapter.cs:164-216` |
| M11 | **`EnsureCommandParameters` is a no-op** so `BindByName`/Guid/`OracleDbType` corrections never get applied. | `OracleSyncAdapter.cs:219-224` |
| M12 | **`OracleDatabaseBuilder.EnsureTableAsync` / `GetAllTablesAsync` / `GetTableAsync` throw `NotImplementedException`.** | `Builders/OracleDatabaseBuilder.cs:298-311,360-364` |
| M13 | **`RenameTable` "schema move" uses `MOVE TABLESPACE`** (tablespace ≠ schema) — incorrect semantics. | `Builders/OracleDatabaseBuilder.cs:494` |
| M14 | **Bulk path half-present.** Adapter disables TVP/bulk but `ExecuteBatchCommandAsync` still string-matches `:`-prefixed params and would misbehave if reached. Bulk should be cleanly off (`UseBulkOperations=false`, batch types return `(null,false)`). | `OracleSyncAdapter.cs:227-305` |

### 3.3 Type-system / metadata specifics to get right

- GUID ⇒ `VARCHAR2(36)`; bind as string; compare case-insensitively to scope ids.
- `bool`/`BIT` ⇒ `NUMBER(1)`; `tombstone`/`force_write` as `0/1`.
- `timestamp` tracking column ⇒ `NUMBER(19)` (holds the scaled epoch value).
- `DateTime` ⇒ `TIMESTAMP`; `DateTimeOffset` ⇒ `TIMESTAMP WITH TIME ZONE`.
- `decimal` ⇒ `NUMBER(p,s)`; `int/bigint` ⇒ `NUMBER(10)/NUMBER(19)`.
- `string` > 4000 / `text` ⇒ `CLOB`; `byte[]` > 2000 / `image` ⇒ `BLOB`; else `RAW(n)`.
- Reading back (`GetColumnsAsync`): map Oracle native (`NUMBER(p,s)`, `VARCHAR2`, `CLOB`,
  `TIMESTAMP`, `RAW`, `BLOB`, …) to managed types using precision/scale to pick
  bool/int16/int32/int64/decimal for `NUMBER`.

### 3.4 Integration, tests, and packaging gaps

| # | Issue | Evidence |
|---|-------|----------|
| I1 | Project is in `Dotmim.Sync.sln` but **missing from `Dotmim.Sync.slnx`**. | `Dotmim.Sync.slnx` |
| I2 | **No tests.** Not in `ProviderType` enum, `HelperDatabase`, or `Setup.cs`. | `Tests/Dotmim.Sync.Tests/Core/ProviderType.cs`; `Misc/HelperDatabase.cs`; `Setup.cs` |
| I3 | **Not in CI.** No Oracle job/container in `pipelines/*` or `azure-pipelines-*.yml`; not packed in `azure-pipelines-nuget.yml`. | pipelines |
| I4 | **No sample** under `Samples/`. | — |
| I5 | Provider metadata rough edges: `Company` is `Microsoft`, `Version` `1.0.0`, `favicon.ico` packed oddly. | `Dotmim.Sync.Oracle.csproj` |

### 3.5 Minor / cleanup

- `Console.WriteLine` on drop failure (`OracleDatabaseBuilder.cs:46`).
- `throw ex;` rethrow loses stack trace (`OracleScopeBuilder.cs:111,152`).
- Dead code: `InternalExistsTableAsync` / `InternalExistsTrackingTableAsync` unused
  (`OracleTableBuilder.cs:59-121`); `CreateDeleteMetadataParameters` unused.
- `Task.Run` wrappers around sync ADO calls in `OracleDatabaseBuilder` (`GetHelloAsync`,
  `ExistsTableAsync`, …) — prefer real async APIs.

---

## 4. Target design decisions

1. **Inline-SQL architecture (no stored procedures).** Mirror SQLite/MySQL: `CommandType.Text`
   for `SelectChanges`, `SelectInitializedChanges`, `SelectRow`, `DeleteMetadata`,
   `UpdateMetadata`, `SelectMetadata`, `Reset`, `UpdateUntrackedRows`, En/DisableConstraints.
   Implement `UpdateRow`/`InsertRow`/`DeleteRow` as **anonymous PL/SQL blocks** ending with
   `:sync_row_count := SQL%ROWCOUNT;`. Make the three `*StoredProcedure*` table-builder methods
   return `null` (exactly as `SqliteTableBuilder.cs:277-286`) so provisioning with the
   `StoredProcedures` flag is a clean no-op.
2. **Keep `SupportsOutputParameters = true`** and expose `sync_row_count` as an OUT bind.
3. **`ParameterPrefix => ":"`, `UseBulkOperations` forced off,** batch command types return
   `(null,false)`; `ExecuteBatchCommandAsync` throws `NotSupportedException` (never reached).
4. **`BindByName = true`** on every `OracleCommand` (set in `GetCommand`, re-assert in
   `EnsureCommandParameters`).
5. **One timestamp clock.** Define a single expression, e.g.
   `ROUND((CAST(SYS_EXTRACT_UTC(SYSTIMESTAMP) AS DATE) - DATE '1970-01-01') * 86400000)`
   refined to sub-second using `SYSTIMESTAMP` fractional seconds, used identically in triggers
   and `GetLocalTimestampCommand`. Validate monotonicity; if insufficient resolution, back the
   clock with a global Oracle **sequence** (`<prefix>_ts_seq`) combined with epoch.
6. **Canonical tracking table** columns: the PK columns, `update_scope_id VARCHAR2(36) NULL`,
   `timestamp NUMBER(19) NULL`, `sync_row_is_tombstone NUMBER(1) NOT NULL`,
   `last_change_datetime TIMESTAMP NULL`. Triggers `MERGE … USING DUAL` but read the timestamp
   from the shared expression/sequence (never `MAX()` of the table being mutated) to avoid
   ORA-04091.
7. **Identifier strategy: uppercase, unquoted-semantics.** Normalize all sync-created object
   names to UPPER and store/look them up consistently, so quoting and `USER_*` lookups agree.
   Preserve user table/column names via the parser but ensure create-vs-exists use the same
   casing.
8. **GUID handling** centralized in `AddCommandParameterValue`/`EnsureCommandParametersValues`:
   convert `Guid`→36-char string, set `OracleDbType.Varchar2`; convert `bool`→`NUMBER(1)`.
9. **Fix `OracleDbMetadata`** to map on the managed `DbType`/CLR type for outbound and on
   `OriginalTypeName` for inbound (two clear directions), eliminating the dead switches.

---

## 5. Phased implementation plan

Each phase has concrete tasks and an acceptance check. Phases 1–6 are the functional core;
7–8 are integration/hardening.

### Phase 0 — Baseline & harness (prereq)
- Add Oracle to `Dotmim.Sync.slnx`; confirm `dotnet build` of the project succeeds.
- Stand up an Oracle test database locally (container `gvenzl/oracle-free` or
  `container-registry.oracle.com/database/free`); capture a connection string.
- **Acceptance:** project builds; can open an `OracleConnection` and run `SELECT 1 FROM DUAL`.

### Phase 1 — Adapter wiring & parameter correctness
- Override `ParameterPrefix => ":"`, `SupportsOutputParameters => true`,
  `UseBulkOperations` off.
- Set `BindByName = true` in `GetCommand`; re-assert in `EnsureCommandParameters`.
- Rewrite `AddCommandParameterValue` to handle Guid→string(36), bool→0/1, DateTime range,
  CLOB/BLOB via metadata (not length heuristics).
- Route **all** command types to `CommandType.Text` (no `StoredProcedure`).
- **Acceptance:** a hand-written unit test binds `:sync_scope_id` (Guid) + a PK and executes a
  trivial `SELECT … FROM DUAL` style command without bind errors.

### Phase 2 — `OracleDbMetadata` rewrite
- Outbound: `GetOwnerDbType`/`GetDbType`/`GetType`/`IsNumericType`/`GetPrecisionAndScale`
  switch on managed `DbType`/CLR type.
- Inbound: a dedicated method maps Oracle native names (+ precision/scale) → managed type, used
  by `GetColumnsAsync`.
- **Acceptance:** round-trip unit tests: SQL-Server schema → Oracle types → read back →
  equivalent `SyncColumn`s (PK, nullability, precision/scale, length preserved).

### Phase 3 — Tracking table, timestamp clock & triggers
- Emit the canonical tracking table (with `timestamp`).
- Implement the single timestamp expression (+ optional sequence) and use it in
  `GetLocalTimestampCommand`.
- Rewrite insert/update/delete triggers to `MERGE … USING DUAL` writing
  `update_scope_id=NULL`, `sync_row_is_tombstone=0/1`, `timestamp=<clock>`,
  `last_change_datetime=SYS_EXTRACT_UTC(SYSTIMESTAMP)`. No `MAX()` of the mutating table.
- **Acceptance:** INSERT/UPDATE/DELETE on the base table produce exactly one tracking row with a
  strictly increasing `timestamp`; `GetLocalTimestamp` ≥ all row timestamps.

### Phase 4 — `OracleObjectNames` SQL bodies (the engine)
Implement inline SQL matching the framework's parameter sets:
- `SelectChanges` / `SelectChangesWithFilters`: base `LEFT/RIGHT JOIN` tracking, `WHERE
  side.timestamp > :sync_min_timestamp AND (side.update_scope_id <> :sync_scope_id OR …IS
  NULL)`; project `sync_row_is_tombstone`, `sync_update_scope_id`.
- `SelectInitializedChanges` (+filters): all rows (+recent tombstones) `> :sync_min_timestamp`.
- `SelectRow`: PK-filtered, bind names = parsed PK normalized names.
- `UpdateRow`/`InsertRow`: anonymous PL/SQL — conflict-aware `MERGE`/`UPDATE`+`INSERT` into base
  guarded by `(ts <= :sync_min_timestamp OR ts IS NULL OR update_scope_id = :sync_scope_id OR
  :sync_force_write = 1)`, then upsert tracking with `update_scope_id = :sync_scope_id`; set
  `:sync_row_count := SQL%ROWCOUNT`.
- `DeleteRow`: guarded delete + tombstone tracking; `:sync_row_count`.
- `DeleteMetadata`: `DELETE FROM tracking WHERE timestamp <= :sync_row_timestamp`.
- `UpdateMetadata` / `SelectMetadata`: tracking upsert / select.
- `Reset`: `DELETE` base + `DELETE` tracking (transaction-safe; no TRUNCATE).
- `UpdateUntrackedRows`: insert missing tracking rows with a fresh timestamp.
- En/DisableConstraints: per-table FK enable/disable (provider is `OnTableLevel`).
- **Acceptance:** each command executes against a real Oracle table with the framework-created
  parameters and returns/affects the expected rows.

### Phase 5 — `OracleTableBuilder` / `OracleDatabaseBuilder` completion
- Stored-proc methods → `null`; finish `GetColumnsAsync` (real type mapping, PK flags via
  `GetPrimaryKeysAsync`), fix `GetRelationsAsync` (correct FK column join + attach columns).
- Implement `EnsureTableAsync`/`GetTableAsync`/`GetAllTablesAsync`; fix `RenameTable`; replace
  `Console.WriteLine`/`throw ex;`; remove dead code; consistent identifier casing.
- **Acceptance:** `ProvisionAsync`(Table|TrackingTable|Triggers) then `DeprovisionAsync`
  round-trips cleanly and idempotently on a fresh schema.

### Phase 6 — Scope builder hardening
- Bind GUID scope ids as `VARCHAR2(36)` strings; ensure CLOB columns bind as `OracleDbType.Clob`;
  align `GetLocalTimestamp` with the Phase 3 clock; replace `throw ex;`.
- **Acceptance:** scope_info / scope_info_client insert/update/select round-trip; two-client
  sync persists and reloads scopes correctly.

### Phase 7 — Test integration
- Add `Oracle = 32` to `ProviderType`; wire `HelperDatabase.GetSyncProvider`/connection-string
  and DB create/drop; add `OracleTcpTests` (+ filter/conflict/http) to `Setup.cs`.
- Provide a containerized Oracle for the suite; gate on an env-var connection string.
- **Acceptance:** the standard TCP sync test matrix passes Oracle⇄Oracle and SqlServer⇄Oracle.

### Phase 8 — CI, packaging, sample, docs
- Oracle pipeline/job with an Oracle service container; add to NuGet pack; fix csproj metadata.
- Add a `Samples/` Oracle walkthrough; document Oracle specifics (identifier casing, types,
  timestamp clock, no-stored-proc design) under `docs/`.
- **Acceptance:** CI builds+tests Oracle; package restores and runs in the sample.

---

## 6. Suggested implementation order (dependencies)

```
Phase 0 ─► Phase 1 ─► Phase 2 ─► Phase 3 ─► Phase 4 ─► Phase 5 ─► Phase 6 ─► Phase 7 ─► Phase 8
                         │                     ▲
                         └─────────────────────┘  (Phase 2 metadata underpins Phase 4 SQL typing)
```

Phases 1–4 are the critical path to a first successful one-table, one-direction sync. A good
first milestone: **SqlServer (server) → Oracle (client)** single-table TCP sync green.

## 7. Risks & open decisions

- **Timestamp resolution / monotonicity.** Sub-millisecond bursts may collide; decide early
  between a pure time expression and a sequence-backed hybrid (recommended for safety). This is
  the highest-risk design point.
- **Identifier casing policy.** Choosing "uppercase canonical" is simplest and matches Oracle
  defaults, but verify against mixed-case user tables; document the contract.
- **NCLOB/CLOB & `Prepare()`** interactions with large LOB binds; validate `PrepareAsync` works
  for anonymous PL/SQL blocks with OUT params (fallback: skip prepare via interceptor if needed).
- **Min Oracle version** for chosen functions (`SYS_EXTRACT_UTC`, identity columns) — target 19c+
  and state it.
- **`netstandard2.1`** target vs `Oracle.ManagedDataAccess.Core` async support — confirm
  `PrepareAsync`/`ExecuteReaderAsync` behavior across TFMs.

## 8. Effort estimate (rough)

| Phase | Scope | Estimate |
|------|-------|----------|
| 0–1 | Harness + adapter wiring | 2–3 days |
| 2 | Metadata rewrite | 2 days |
| 3 | Tracking + clock + triggers | 3–4 days |
| 4 | Engine SQL bodies | 5–7 days |
| 5 | Builders completion | 3–4 days |
| 6 | Scope hardening | 1–2 days |
| 7 | Test integration | 3–5 days |
| 8 | CI / packaging / sample / docs | 2–3 days |

**Total: ~3–5 focused weeks** to a tested, CI-covered, fully functional Oracle provider, with
the first green single-table sync achievable by end of Phase 4.
