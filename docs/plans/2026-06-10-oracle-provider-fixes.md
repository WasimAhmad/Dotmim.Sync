# Oracle Provider Fixes — Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Fix every issue in [docs/Oracle-Provider-Verification.md](../Oracle-Provider-Verification.md) so the Oracle provider is functionally correct against the Dotmim.Sync v1.x framework contracts, covered by unit tests, registered in the test matrix, CI, packaging, sample and docs.

**Architecture:** The provider keeps its inline-SQL design (CommandType.Text + anonymous PL/SQL blocks). The scope builder is rewritten 1:1 against the framework's canonical scope schema, using `DBMS_SQL.RETURN_RESULT` implicit result sets for the insert/update read-back (Oracle cannot batch `INSERT; SELECT;`). All other fixes are targeted edits with SQL-text unit tests that run **without a live Oracle database**; behavior that needs a real database is collected in a final live-validation checklist.

**Tech Stack:** .NET (netstandard2.1/net6.0/net8.0), Oracle.ManagedDataAccess.Core 3.21 (ODP.NET managed), xunit (existing `Tests/Dotmim.Sync.Tests`), Azure Pipelines, `gvenzl/oracle-free` container.

---

## Required context (read before starting)

**Repo/branch:** `D:\Dev\Personal\Dotmim.Sync`, branch `claude/oracle-provider-implementation`. The working tree already contains *uncommitted* EF-Core/Oracle test wiring (see Task 12) — do not discard it.

**Commit rule:** commit messages must NOT contain any `Co-Authored-By` trailer (user requirement; overrides any default).

**Framework contracts the code must honor** (verified in this repo):

1. **Parameter binding is by canonical name.** `BaseOrchestrator` sets scope parameter values with `InternalSetParameterValue(command, "<canonical>", value)` ([BaseOrchestrator.ScopeInfos.cs:160-170](../../Projects/Dotmim.Sync.Core/Orchestrators/Scopes/BaseOrchestrator.ScopeInfos.cs)). `DbSyncAdapter.InternalGetParameter` probes `@x`, `:x`, `in_x`, `x` for canonical name `x`. So every scope parameter must be named `:sync_scope_name`, `:scope_last_sync_timestamp`, etc. The value is converted via `SyncTypeConverter.TryConvertFromDbType(value, parameter.DbType)` — so `DbType.Guid` parameters receive proper `Guid` values even when the orchestrator passes a string.
2. **Canonical scope_info** (set: ScopeInfos.cs:469-474, read: ScopeInfos.cs:490-495): columns `sync_scope_name` (PK, the ONLY key — no `sync_scope_id` in this table), `sync_scope_schema`, `sync_scope_setup`, `sync_scope_version`, `sync_scope_last_clean_timestamp` (read with `reader.GetInt64`), `sync_scope_properties`.
3. **Canonical scope_info_client** (set: ScopeInfoClients.cs:337-346, read: ScopeInfoClients.cs:354-368): `sync_scope_id` (read with `reader.GetGuid` → RAW(16) works), `sync_scope_name`, `sync_scope_hash`, `sync_scope_parameters`, `scope_last_sync_timestamp`, `scope_last_server_sync_timestamp`, `scope_last_sync_duration`, `scope_last_sync` (read with `reader.GetDateTime`), `sync_scope_errors`, `sync_scope_properties`. PK = (id, name, hash).
4. **Insert/Update scope commands MUST return the saved row as a result set.** `InternalSaveScopeInfoAsync` does `ExecuteReaderAsync` → `ReadAsync` → reads columns (ScopeInfos.cs:393-397); same for clients. MySQL appends a trailing `SELECT`; Oracle must use `DBMS_SQL.RETURN_RESULT(rc)` from an anonymous block (Oracle 12.1+; supported by ODP.NET `ExecuteReader`).
5. **The framework adds parameters our SQL may not reference** — `:sync_scope_id` + `:sync_row_count` on SelectRow, all PK params + `:sync_row_count` on DeleteMetadata (BaseOrchestrator.SetCommands.cs:61-92, 143-174). ODP.NET with `BindByName = true` raises ORA-01036 for unmatched parameters → they must be stripped in `EnsureCommandParameters` (the hook SqlServer uses for the same purpose).
6. **`DbSyncAdapter.SupportsOutputParameters` defaults to `true`** (DbSyncAdapter.cs:38) — do not override; the PL/SQL blocks rely on the framework-supplied `:sync_row_count` output parameter.
7. **Filter SQL**: reference providers emit `filter.Joins` (MySqlSyncAdapter.GetChanges.cs:28-78 `CreateFilterCustomJoins`) in both SelectChanges and SelectInitializedChanges.

**Build/test commands used throughout** (run from repo root `D:\Dev\Personal\Dotmim.Sync`):

```powershell
# Build provider only (fast):
dotnet build Projects/Dotmim.Sync.Oracle/Dotmim.Sync.Oracle.csproj -f net8.0 --nologo

# Run ONLY the new Oracle unit tests (no database needed; filter excludes OracleTcpTests etc.):
dotnet test Tests/Dotmim.Sync.Tests/Dotmim.Sync.Tests.csproj -f net8.0 --filter "FullyQualifiedName~UnitTests.Oracle" --nologo
```

**Progress tracking:** update `docs/plans/oracle-provider-fixes-progress.md` after each task (mark done, note deviations).

---

## ⚠ Execution amendment (discovered during Task 1)

**ODP.NET fact (verified by direct test against Oracle.ManagedDataAccess in this repo's test bin):** `OracleParameter.DbType = DbType.Guid` **throws** `ArgumentException` ("Value does not fall within the expected range"), and `OracleDbType.Raw` reports back as `DbType.Binary`. Two framework consequences:

1. **Scope commands:** values are set by `InternalSetParameterValue`, which converts via `SyncTypeConverter.TryConvertFromDbType(value, parameter.DbType)` with **no provider hook**. A Raw parameter (reporting `DbType.Binary`) routes Guid/string values into `TryConvertTo<byte[]>` → `Convert.FromBase64String(guidString)` / `BitConverter.GetBytes(Guid)` → **runtime crash**.
   **Resolution (Task 1 fix-up):** scope-id parameters are declared `DbType.String, Size = 36` (converter yields the canonical guid string), the `sync_scope_id` column stays `RAW(16)`, and every scope-SQL reference converts the bind with a `GuidToRaw(...)` SQL expression that reorders the hex string into the **`Guid.ToByteArray()` byte layout** — so `reader.GetGuid()` (used by the orchestrator, `ScopeInfoClients.cs:358`) round-trips the identical Guid, and values stay comparable with the table-command path (which binds `guid.ToByteArray()` directly):
   `h = REPLACE(:sync_scope_id, '-', '')` →
   `HEXTORAW(SUBSTR(h,7,2)||SUBSTR(h,5,2)||SUBSTR(h,3,2)||SUBSTR(h,1,2) || SUBSTR(h,11,2)||SUBSTR(h,9,2) || SUBSTR(h,15,2)||SUBSTR(h,13,2) || SUBSTR(h,17,16))`
   (Byte-order check: guid `00112233-4455-6677-8899-aabbccddeeff` → ToByteArray `33 22 11 00 / 55 44 / 77 66 / 88 99 aa bb cc dd ee ff`.)

2. **Table commands:** the framework's generic parameter creation sets `p.DbType = column.GetDbType()` — `DbType.Guid` for Guid columns → same throw. But `BaseOrchestrator.InternalGetCommandAsync` **skips generic parameter creation when `GetCommand` returns a command that already has parameters** (`Commands.cs:81`), and all subsequent value-setting goes through `syncAdapter.GetParameter` (null-checked, `Commands.cs:219-242`) + `syncAdapter.AddCommandParameterValue` (our hook) with column values matched by `parameter.SourceColumn` (`Commands.cs:187-193`).
   **Resolution (Task 2 redesigned):** `OracleSyncAdapter.GetCommand` pre-creates the full parameter set per command type using `OracleDbType` (via `OracleDbMetadata.GetOwnerDbType`), creating **only the parameters each statement references** (this also solves ORA-01036/C3 structurally — the regex strip remains as a defensive net). `AddCommandParameterValue` converts Guid values (and guid-strings for Guid-typed columns, identified via `parameter.SourceColumn`) to `Guid.ToByteArray()` for Raw(16) binds. Parameter sets:
   - SelectChanges(+filters): `:sync_min_timestamp` Int64, `:sync_scope_id` Raw(16) [+ filter params]
   - SelectInitializedChanges(+filters): `:sync_min_timestamp` Int64 [+ filter params]
   - SelectRow: PK column params only
   - UpdateRow/InsertRow(+Rows): all `!IsReadOnly` columns, `:sync_scope_id` Raw(16), `:sync_force_write` Int64, `:sync_min_timestamp` Int64, `:sync_row_count` Int32 Output
   - DeleteRow(+Rows): PK columns, `:sync_scope_id` Raw(16), `:sync_force_write` Int64, `:sync_min_timestamp` Int64, `:sync_row_count` Int32 Output
   - DeleteMetadata: `:sync_row_timestamp` Int64
   - Reset: `:sync_row_count` Int32 Output
   - UpdateUntrackedRows / Disable-EnableConstraints: none

   Task 2's original unit test (manually adding a `DbType.Guid` parameter) is replaced — tests now assert the parameter sets `GetCommand` itself creates.

3. **CLOB parameters (found by Task 1 quality review):** `OracleDbType.Clob` makes the parameter report `DbType.Object`, which `TryConvertFromDbType` routes to the same byte[]/base64 crash path (`SyncTypeConverter.cs:280-281`). The Task 1 spec's `isClob` helper was a plan defect.
   **Resolution (landed in `c78b8066`):** the five JSON parameters are declared plain `DbType.String`; a thin internal `OracleScopeCommand : DbCommand` wrapper (returned by `CreateCommand`) promotes Varchar2 parameters whose string value exceeds 4000 chars to `OracleDbType.Clob` at execute time — after the framework's conversion has already run. VARCHAR2 bind limits therefore never cap schema JSON. `InternalsVisibility.cs` was added in Task 1 (Task 10 must NOT re-create it). Regression tests: `SaveCommands_ParameterDbTypes_SurviveFrameworkConversion` (locks every save-command DbType to converter-safe values), `GuidToRaw_SqlSubstrPositions_ReproduceGuidToByteArray` (locks the byte swap against the generated SQL), `ScopeCommands_PromoteLargeStringValuesToClobAtExecuteTime`.
   Task 16 additions: verify >32KB schema JSON saves (CLOB promotion inside the PL/SQL block) and `DBMS_SQL.RETURN_RESULT` consumption via `ExecuteReaderAsync`. Known note for docs (Task 15): interceptors casting scope-command `args.Command` to `OracleCommand` get null (sync-adapter commands are unaffected).

---

## File map

| File | Action | Responsibility |
|---|---|---|
| `Projects/Dotmim.Sync.Oracle/Builders/OracleScopeBuilder.cs` | rewrite | v1.x scope schema + canonical params + RETURN_RESULT read-back (C1, C2) |
| `Projects/Dotmim.Sync.Oracle/OracleSyncAdapter.cs` | modify | strip unreferenced binds (C3) |
| `Projects/Dotmim.Sync.Oracle/Builders/OracleObjectNames.cs` | modify | filter joins (C4), NOVALIDATE (M2), identifier guard (M3), tracking index (M6), single-eval clock (minor) |
| `Projects/Dotmim.Sync.Oracle/Builders/OracleDatabaseBuilder.cs` | rewrite | USER_* views, case-preserved names, async cleanup, dead-code removal (M1) |
| `Projects/Dotmim.Sync.Oracle/OracleTransientExceptionDetector.cs` | modify | correct transient set, testable core (M4) |
| `Projects/Dotmim.Sync.Oracle/OracleSyncProvider.cs` | modify | database name = user/schema (M5) |
| `Projects/Dotmim.Sync.Oracle/Builders/OracleTableBuilder.cs` | modify | RAW(16)→Guid inbound mapping (minor) |
| `Projects/Dotmim.Sync.Oracle/InternalsVisibility.cs` | create | test access to internals (mirrors SqlServer project) |
| `Projects/Dotmim.Sync.Oracle/Dotmim.Sync.Oracle.csproj` | rewrite | parity with MySQL csproj, central versioning (packaging) |
| `Tests/Dotmim.Sync.Tests/UnitTests/Oracle/OracleScopeBuilderTests.cs` | create | scope builder unit tests |
| `Tests/Dotmim.Sync.Tests/UnitTests/Oracle/OracleSyncAdapterTests.cs` | create | adapter/param-strip unit tests |
| `Tests/Dotmim.Sync.Tests/UnitTests/Oracle/OracleObjectNamesTests.cs` | create | SQL-text unit tests (joins, constraints, clock, guard, index) |
| `Tests/Dotmim.Sync.Tests/UnitTests/Oracle/OracleMiscTests.cs` | create | detector, provider identity, type mapping |
| `Tests/Dotmim.Sync.Tests/Setup.cs` | modify | add `OracleTcpFilterTests`, `OracleHttpTests` (I2) |
| `pipelines/azure-pipelines-template.yml` | modify | Oracle docker step (I4) |
| `azure-pipelines-oracle.yml` | create | Oracle CI jobs (I4) |
| `azure-pipelines-nuget.yml` | modify | pack Dotmim.Sync.Oracle (I4) |
| `Samples/HelloOracleSync/*` | create | minimal sample (I5) |
| `docs/Oracle.md` | create | provider docs (I6) |

Shared test helper note: each unit-test file builds its own small `SyncTable`/`ScopeInfo` fixtures (kept local per file — they are 10 lines and the files test different surfaces).

---

### Task 0: Baseline

**Files:** none (verification only)

- [ ] **Step 0.1: Confirm clean baseline build**

Run: `dotnet build Projects/Dotmim.Sync.Oracle/Dotmim.Sync.Oracle.csproj -f net8.0 --nologo`
Expected: `0 Error(s)` (warnings are OK at this point; they are addressed in Task 11).

- [ ] **Step 0.2: Confirm the unit-test filter currently selects nothing**

Run: `dotnet test Tests/Dotmim.Sync.Tests/Dotmim.Sync.Tests.csproj -f net8.0 --filter "FullyQualifiedName~UnitTests.Oracle" --nologo`
Expected: "No test matches the given testcase filter" (exit code may be non-zero — that's fine).

---

### Task 1: Rewrite `OracleScopeBuilder` (fixes C1 + C2)

**Files:**
- Create: `Tests/Dotmim.Sync.Tests/UnitTests/Oracle/OracleScopeBuilderTests.cs`
- Rewrite: `Projects/Dotmim.Sync.Oracle/Builders/OracleScopeBuilder.cs`

- [ ] **Step 1.1: Write the failing tests**

Create `Tests/Dotmim.Sync.Tests/UnitTests/Oracle/OracleScopeBuilderTests.cs`:

```csharp
using Dotmim.Sync.Oracle.Builders;
using Oracle.ManagedDataAccess.Client;
using System.Data.Common;
using System.Linq;
using Xunit;

namespace Dotmim.Sync.Tests.UnitTests.Oracle
{
    public class OracleScopeBuilderTests
    {
        private static string[] ParameterNames(DbCommand command)
            => command.Parameters.Cast<DbParameter>().Select(p => p.ParameterName).ToArray();

        [Fact]
        public void CreateScopeInfoTable_UsesCanonicalColumnsAndNameKey()
        {
            using var connection = new OracleConnection();
            var builder = new OracleScopeBuilder("scope_info");

            var text = builder.GetCreateScopeInfoTableCommand(connection, null).CommandText;

            Assert.Contains("\"sync_scope_name\"", text);
            Assert.Contains("\"sync_scope_schema\"", text);
            Assert.Contains("\"sync_scope_setup\"", text);
            Assert.Contains("\"sync_scope_version\"", text);
            Assert.Contains("\"sync_scope_last_clean_timestamp\"", text);
            Assert.Contains("\"sync_scope_properties\"", text);
            // scope_info is keyed by name only — it must NOT contain a scope id column
            Assert.DoesNotContain("sync_scope_id", text);
            Assert.Contains("PRIMARY KEY (\"sync_scope_name\")", text);
        }

        [Fact]
        public void CreateScopeInfoClientTable_UsesCanonicalColumnsAndCompositeKey()
        {
            using var connection = new OracleConnection();
            var builder = new OracleScopeBuilder("scope_info");

            var text = builder.GetCreateScopeInfoClientTableCommand(connection, null).CommandText;

            foreach (var col in new[]
            {
                "\"sync_scope_id\"", "\"sync_scope_name\"", "\"sync_scope_hash\"",
                "\"sync_scope_parameters\"", "\"scope_last_sync_timestamp\"",
                "\"scope_last_server_sync_timestamp\"", "\"scope_last_sync_duration\"",
                "\"scope_last_sync\"", "\"sync_scope_errors\"", "\"sync_scope_properties\"",
            })
            {
                Assert.Contains(col, text);
            }

            Assert.Contains("PRIMARY KEY (\"sync_scope_id\", \"sync_scope_name\", \"sync_scope_hash\")", text);
            // invented legacy columns must be gone
            Assert.DoesNotContain("sync_scope_client_id", text);
            Assert.DoesNotContain("sync_scope_filters", text);
        }

        [Fact]
        public void InsertScopeInfo_UsesCanonicalParameterNames_AndReturnsRow()
        {
            using var connection = new OracleConnection();
            var builder = new OracleScopeBuilder("scope_info");

            var command = builder.GetInsertScopeInfoCommand(connection, null);
            var names = ParameterNames(command);

            Assert.Contains(":sync_scope_name", names);
            Assert.Contains(":sync_scope_schema", names);
            Assert.Contains(":sync_scope_setup", names);
            Assert.Contains(":sync_scope_version", names);
            Assert.Contains(":sync_scope_last_clean_timestamp", names);
            Assert.Contains(":sync_scope_properties", names);
            // the orchestrator reads the saved row back from a result set
            Assert.Contains("DBMS_SQL.RETURN_RESULT", command.CommandText);
        }

        [Fact]
        public void UpdateScopeInfoClient_UsesCanonicalParameterNames_AndReturnsRow()
        {
            using var connection = new OracleConnection();
            var builder = new OracleScopeBuilder("scope_info");

            var command = builder.GetUpdateScopeInfoClientCommand(connection, null);
            var names = ParameterNames(command);

            Assert.Contains(":sync_scope_id", names);
            Assert.Contains(":sync_scope_name", names);
            Assert.Contains(":sync_scope_hash", names);
            Assert.Contains(":sync_scope_parameters", names);
            Assert.Contains(":scope_last_sync_timestamp", names);
            Assert.Contains(":scope_last_server_sync_timestamp", names);
            Assert.Contains(":scope_last_sync_duration", names);
            Assert.Contains(":scope_last_sync", names);
            Assert.Contains(":sync_scope_errors", names);
            Assert.Contains(":sync_scope_properties", names);
            Assert.Contains("DBMS_SQL.RETURN_RESULT", command.CommandText);
        }

        [Fact]
        public void ExistsScopeInfoTable_ComparesCasePreservedName()
        {
            using var connection = new OracleConnection();
            var builder = new OracleScopeBuilder("scope_info");

            var command = builder.GetExistsScopeInfoTableCommand(connection, null);
            var parameter = command.Parameters.Cast<DbParameter>().Single();

            // C2: creation quotes "scope_info" (case-preserved) so the exists
            // check must compare the same string, NOT 'SCOPE_INFO'.
            Assert.Equal("scope_info", parameter.Value);
        }

        [Fact]
        public void GetScopeInfo_FiltersByNameOnly()
        {
            using var connection = new OracleConnection();
            var builder = new OracleScopeBuilder("scope_info");

            var command = builder.GetScopeInfoCommand(connection, null);

            Assert.Contains(":sync_scope_name", command.CommandText);
            Assert.DoesNotContain(":sync_scope_id", command.CommandText);
        }

        [Fact]
        public void ExistsScopeInfoClient_FiltersByNameIdAndHash()
        {
            using var connection = new OracleConnection();
            var builder = new OracleScopeBuilder("scope_info");

            var text = builder.GetExistsScopeInfoClientCommand(connection, null).CommandText;

            Assert.Contains(":sync_scope_name", text);
            Assert.Contains(":sync_scope_id", text);
            Assert.Contains(":sync_scope_hash", text);
        }
    }
}
```

- [ ] **Step 1.2: Run the tests to verify they fail**

Run: `dotnet test Tests/Dotmim.Sync.Tests/Dotmim.Sync.Tests.csproj -f net8.0 --filter "FullyQualifiedName~OracleScopeBuilderTests" --nologo`
Expected: FAIL (current code creates the legacy schema and `:scopeName`-style parameters).

- [ ] **Step 1.3: Replace `OracleScopeBuilder.cs` entirely**

Replace the full content of `Projects/Dotmim.Sync.Oracle/Builders/OracleScopeBuilder.cs` with:

```csharp
using Dotmim.Sync.Builders;
using Dotmim.Sync.DatabaseStringParsers;
using Oracle.ManagedDataAccess.Client;
using System;
using System.Data;
using System.Data.Common;

namespace Dotmim.Sync.Oracle.Builders
{
    /// <summary>
    /// Oracle implementation of the DbScopeBuilder, aligned with the framework's v1.x
    /// scope model:
    /// <para>
    /// - <c>scope_info</c> is keyed by <c>sync_scope_name</c> only.
    /// - <c>scope_info_client</c> is keyed by (<c>sync_scope_id</c>, <c>sync_scope_name</c>,
    ///   <c>sync_scope_hash</c>).
    /// - Every parameter uses the canonical name expected by
    ///   <c>BaseOrchestrator.InternalSetParameterValue</c> (prefixed with <c>:</c>).
    /// - Insert/Update commands return the saved row as a result set through
    ///   <c>DBMS_SQL.RETURN_RESULT</c> (Oracle 12.1+), because the orchestrator reads the
    ///   row back with ExecuteReader and Oracle cannot batch a trailing SELECT.
    /// </para>
    /// </summary>
    public class OracleScopeBuilder : DbScopeBuilder
    {
        private const char QuoteChar = '"';

        private const string ScopeInfoColumns =
            "\"sync_scope_name\", \"sync_scope_schema\", \"sync_scope_setup\", \"sync_scope_version\", " +
            "\"sync_scope_last_clean_timestamp\", \"sync_scope_properties\"";

        private const string ScopeInfoClientColumns =
            "\"sync_scope_id\", \"sync_scope_name\", \"sync_scope_hash\", \"sync_scope_parameters\", " +
            "\"scope_last_sync_timestamp\", \"scope_last_server_sync_timestamp\", \"scope_last_sync_duration\", " +
            "\"scope_last_sync\", \"sync_scope_errors\", \"sync_scope_properties\"";

        private readonly string tableName;
        private readonly string clientTableName;
        private readonly DbTableNames scopeInfoTableNames;
        private readonly DbTableNames scopeInfoClientTableNames;

        /// <summary>
        /// Initializes a new instance of the <see cref="OracleScopeBuilder"/> class.
        /// </summary>
        public OracleScopeBuilder(string scopeInfoTableName)
            : base()
        {
            if (string.IsNullOrEmpty(scopeInfoTableName) || !System.Text.RegularExpressions.Regex.IsMatch(scopeInfoTableName, @"^[A-Za-z0-9_]+$"))
                throw new ArgumentException("Invalid scope info table name format", nameof(scopeInfoTableName));

            this.tableName = scopeInfoTableName;
            this.clientTableName = $"{scopeInfoTableName}_client";

            var parser = new ObjectParser(this.tableName, QuoteChar, QuoteChar);
            this.scopeInfoTableNames = new DbTableNames(
                QuoteChar, QuoteChar, this.tableName, this.tableName,
                parser.NormalizedShortName, $"\"{this.tableName}\"", parser.QuotedShortName, string.Empty);

            var clientParser = new ObjectParser(this.clientTableName, QuoteChar, QuoteChar);
            this.scopeInfoClientTableNames = new DbTableNames(
                QuoteChar, QuoteChar, this.clientTableName, this.clientTableName,
                clientParser.NormalizedShortName, $"\"{this.clientTableName}\"", clientParser.QuotedShortName, string.Empty);
        }

        /// <inheritdoc/>
        public override DbTableNames GetParsedScopeInfoTableNames() => this.scopeInfoTableNames;

        /// <inheritdoc/>
        public override DbTableNames GetParsedScopeInfoClientTableNames() => this.scopeInfoClientTableNames;

        // ----------------------------------------------------------------------------------------
        // Helpers
        // ----------------------------------------------------------------------------------------
        private static DbCommand CreateCommand(DbConnection connection, DbTransaction transaction, string commandText)
        {
            var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = commandText;

            // Named binds everywhere; ODP.NET binds by position unless this is set.
            if (command is OracleCommand oracleCommand)
                oracleCommand.BindByName = true;

            return command;
        }

        private static void AddParameter(DbCommand command, string name, DbType dbType, bool isClob = false, int size = 0)
        {
            var parameter = command.CreateParameter();
            parameter.ParameterName = $":{name}";
            parameter.DbType = dbType;

            if (size > 0)
                parameter.Size = size;

            // JSON payloads (schema/setup/parameters/errors/properties) can exceed the
            // VARCHAR2 bind limit; bind them as CLOB.
            if (isClob && parameter is OracleParameter oracleParameter)
                oracleParameter.OracleDbType = OracleDbType.Clob;

            command.Parameters.Add(parameter);
        }

        private static void AddScopeInfoSaveParameters(DbCommand command)
        {
            AddParameter(command, "sync_scope_name", DbType.String, size: 100);
            AddParameter(command, "sync_scope_schema", DbType.String, isClob: true);
            AddParameter(command, "sync_scope_setup", DbType.String, isClob: true);
            AddParameter(command, "sync_scope_version", DbType.String, size: 10);
            AddParameter(command, "sync_scope_last_clean_timestamp", DbType.Int64);
            AddParameter(command, "sync_scope_properties", DbType.String, isClob: true);
        }

        private static void AddScopeInfoClientSaveParameters(DbCommand command)
        {
            AddParameter(command, "sync_scope_id", DbType.Guid);
            AddParameter(command, "sync_scope_name", DbType.String, size: 100);
            AddParameter(command, "sync_scope_hash", DbType.String, size: 100);
            AddParameter(command, "sync_scope_parameters", DbType.String, isClob: true);
            AddParameter(command, "scope_last_sync_timestamp", DbType.Int64);
            AddParameter(command, "scope_last_server_sync_timestamp", DbType.Int64);
            AddParameter(command, "scope_last_sync_duration", DbType.Int64);
            AddParameter(command, "scope_last_sync", DbType.DateTime);
            AddParameter(command, "sync_scope_errors", DbType.String, isClob: true);
            AddParameter(command, "sync_scope_properties", DbType.String, isClob: true);
        }

        private static void AddScopeInfoClientKeyParameters(DbCommand command)
        {
            AddParameter(command, "sync_scope_name", DbType.String, size: 100);
            AddParameter(command, "sync_scope_id", DbType.Guid);
            AddParameter(command, "sync_scope_hash", DbType.String, size: 100);
        }

        // ----------------------------------------------------------------------------------------
        // Tables : exists / create / drop
        // ----------------------------------------------------------------------------------------
        private static DbCommand CreateExistsTableCommand(DbConnection connection, DbTransaction transaction, string unquotedTableName)
        {
            // Tables are created quoted (case-preserved), so USER_TABLES stores the exact
            // string; compare it case-preserved as well (never upper-cased).
            var command = CreateCommand(connection, transaction,
                "SELECT COUNT(*) FROM USER_TABLES WHERE TABLE_NAME = :tableName");

            var parameter = command.CreateParameter();
            parameter.ParameterName = ":tableName";
            parameter.Value = unquotedTableName;
            command.Parameters.Add(parameter);

            return command;
        }

        /// <inheritdoc/>
        public override DbCommand GetExistsScopeInfoTableCommand(DbConnection connection, DbTransaction transaction)
            => CreateExistsTableCommand(connection, transaction, this.tableName);

        /// <inheritdoc/>
        public override DbCommand GetExistsScopeInfoClientTableCommand(DbConnection connection, DbTransaction transaction)
            => CreateExistsTableCommand(connection, transaction, this.clientTableName);

        /// <inheritdoc/>
        public override DbCommand GetCreateScopeInfoTableCommand(DbConnection connection, DbTransaction transaction)
        {
            var commandText =
$@"CREATE TABLE ""{this.tableName}"" (
  ""sync_scope_name"" VARCHAR2(100) NOT NULL,
  ""sync_scope_schema"" CLOB NULL,
  ""sync_scope_setup"" CLOB NULL,
  ""sync_scope_version"" VARCHAR2(10) NULL,
  ""sync_scope_last_clean_timestamp"" NUMBER(19) NULL,
  ""sync_scope_properties"" CLOB NULL,
  CONSTRAINT ""PK_{this.tableName}"" PRIMARY KEY (""sync_scope_name"")
)";
            return CreateCommand(connection, transaction, commandText);
        }

        /// <inheritdoc/>
        public override DbCommand GetCreateScopeInfoClientTableCommand(DbConnection connection, DbTransaction transaction)
        {
            var commandText =
$@"CREATE TABLE ""{this.clientTableName}"" (
  ""sync_scope_id"" RAW(16) NOT NULL,
  ""sync_scope_name"" VARCHAR2(100) NOT NULL,
  ""sync_scope_hash"" VARCHAR2(100) NOT NULL,
  ""sync_scope_parameters"" CLOB NULL,
  ""scope_last_sync_timestamp"" NUMBER(19) NULL,
  ""scope_last_server_sync_timestamp"" NUMBER(19) NULL,
  ""scope_last_sync_duration"" NUMBER(19) NULL,
  ""scope_last_sync"" TIMESTAMP NULL,
  ""sync_scope_errors"" CLOB NULL,
  ""sync_scope_properties"" CLOB NULL,
  CONSTRAINT ""PK_{this.clientTableName}"" PRIMARY KEY (""sync_scope_id"", ""sync_scope_name"", ""sync_scope_hash"")
)";
            return CreateCommand(connection, transaction, commandText);
        }

        /// <inheritdoc/>
        public override DbCommand GetDropScopeInfoTableCommand(DbConnection connection, DbTransaction transaction)
            => CreateCommand(connection, transaction, $"DROP TABLE \"{this.tableName}\"");

        /// <inheritdoc/>
        public override DbCommand GetDropScopeInfoClientTableCommand(DbConnection connection, DbTransaction transaction)
            => CreateCommand(connection, transaction, $"DROP TABLE \"{this.clientTableName}\"");

        // ----------------------------------------------------------------------------------------
        // scope_info : exists / get / get all / insert / update / delete
        // ----------------------------------------------------------------------------------------

        /// <inheritdoc/>
        public override DbCommand GetExistsScopeInfoCommand(DbConnection connection, DbTransaction transaction)
        {
            var command = CreateCommand(connection, transaction,
                $"SELECT COUNT(*) FROM \"{this.tableName}\" WHERE \"sync_scope_name\" = :sync_scope_name");
            AddParameter(command, "sync_scope_name", DbType.String, size: 100);
            return command;
        }

        /// <inheritdoc/>
        public override DbCommand GetScopeInfoCommand(DbConnection connection, DbTransaction transaction)
        {
            var command = CreateCommand(connection, transaction,
                $"SELECT {ScopeInfoColumns} FROM \"{this.tableName}\" WHERE \"sync_scope_name\" = :sync_scope_name");
            AddParameter(command, "sync_scope_name", DbType.String, size: 100);
            return command;
        }

        /// <inheritdoc/>
        public override DbCommand GetAllScopeInfosCommand(DbConnection connection, DbTransaction transaction)
            => CreateCommand(connection, transaction, $"SELECT {ScopeInfoColumns} FROM \"{this.tableName}\"");

        /// <inheritdoc/>
        public override DbCommand GetInsertScopeInfoCommand(DbConnection connection, DbTransaction transaction)
        {
            var commandText =
$@"DECLARE
  rc SYS_REFCURSOR;
BEGIN
  INSERT INTO ""{this.tableName}""
    ({ScopeInfoColumns})
  VALUES
    (:sync_scope_name, :sync_scope_schema, :sync_scope_setup, :sync_scope_version, :sync_scope_last_clean_timestamp, :sync_scope_properties);
  OPEN rc FOR SELECT {ScopeInfoColumns} FROM ""{this.tableName}"" WHERE ""sync_scope_name"" = :sync_scope_name;
  DBMS_SQL.RETURN_RESULT(rc);
END;";
            var command = CreateCommand(connection, transaction, commandText);
            AddScopeInfoSaveParameters(command);
            return command;
        }

        /// <inheritdoc/>
        public override DbCommand GetUpdateScopeInfoCommand(DbConnection connection, DbTransaction transaction)
        {
            var commandText =
$@"DECLARE
  rc SYS_REFCURSOR;
BEGIN
  UPDATE ""{this.tableName}"" SET
    ""sync_scope_schema"" = :sync_scope_schema,
    ""sync_scope_setup"" = :sync_scope_setup,
    ""sync_scope_version"" = :sync_scope_version,
    ""sync_scope_last_clean_timestamp"" = :sync_scope_last_clean_timestamp,
    ""sync_scope_properties"" = :sync_scope_properties
  WHERE ""sync_scope_name"" = :sync_scope_name;
  OPEN rc FOR SELECT {ScopeInfoColumns} FROM ""{this.tableName}"" WHERE ""sync_scope_name"" = :sync_scope_name;
  DBMS_SQL.RETURN_RESULT(rc);
END;";
            var command = CreateCommand(connection, transaction, commandText);
            AddScopeInfoSaveParameters(command);
            return command;
        }

        /// <inheritdoc/>
        public override DbCommand GetDeleteScopeInfoCommand(DbConnection connection, DbTransaction transaction)
        {
            var command = CreateCommand(connection, transaction,
                $"DELETE FROM \"{this.tableName}\" WHERE \"sync_scope_name\" = :sync_scope_name");
            AddParameter(command, "sync_scope_name", DbType.String, size: 100);
            return command;
        }

        // ----------------------------------------------------------------------------------------
        // scope_info_client : exists / get / get all / insert / update / delete
        // ----------------------------------------------------------------------------------------

        /// <inheritdoc/>
        public override DbCommand GetExistsScopeInfoClientCommand(DbConnection connection, DbTransaction transaction)
        {
            var command = CreateCommand(connection, transaction,
                $"SELECT COUNT(*) FROM \"{this.clientTableName}\" " +
                "WHERE \"sync_scope_name\" = :sync_scope_name AND \"sync_scope_id\" = :sync_scope_id AND \"sync_scope_hash\" = :sync_scope_hash");
            AddScopeInfoClientKeyParameters(command);
            return command;
        }

        /// <inheritdoc/>
        public override DbCommand GetScopeInfoClientCommand(DbConnection connection, DbTransaction transaction)
        {
            var command = CreateCommand(connection, transaction,
                $"SELECT {ScopeInfoClientColumns} FROM \"{this.clientTableName}\" " +
                "WHERE \"sync_scope_name\" = :sync_scope_name AND \"sync_scope_id\" = :sync_scope_id AND \"sync_scope_hash\" = :sync_scope_hash");
            AddScopeInfoClientKeyParameters(command);
            return command;
        }

        /// <inheritdoc/>
        public override DbCommand GetAllScopeInfoClientsCommand(DbConnection connection, DbTransaction transaction)
            => CreateCommand(connection, transaction, $"SELECT {ScopeInfoClientColumns} FROM \"{this.clientTableName}\"");

        /// <inheritdoc/>
        public override DbCommand GetInsertScopeInfoClientCommand(DbConnection connection, DbTransaction transaction)
        {
            var commandText =
$@"DECLARE
  rc SYS_REFCURSOR;
BEGIN
  INSERT INTO ""{this.clientTableName}""
    ({ScopeInfoClientColumns})
  VALUES
    (:sync_scope_id, :sync_scope_name, :sync_scope_hash, :sync_scope_parameters, :scope_last_sync_timestamp, :scope_last_server_sync_timestamp, :scope_last_sync_duration, :scope_last_sync, :sync_scope_errors, :sync_scope_properties);
  OPEN rc FOR SELECT {ScopeInfoClientColumns} FROM ""{this.clientTableName}""
    WHERE ""sync_scope_name"" = :sync_scope_name AND ""sync_scope_id"" = :sync_scope_id AND ""sync_scope_hash"" = :sync_scope_hash;
  DBMS_SQL.RETURN_RESULT(rc);
END;";
            var command = CreateCommand(connection, transaction, commandText);
            AddScopeInfoClientSaveParameters(command);
            return command;
        }

        /// <inheritdoc/>
        public override DbCommand GetUpdateScopeInfoClientCommand(DbConnection connection, DbTransaction transaction)
        {
            var commandText =
$@"DECLARE
  rc SYS_REFCURSOR;
BEGIN
  UPDATE ""{this.clientTableName}"" SET
    ""sync_scope_parameters"" = :sync_scope_parameters,
    ""scope_last_sync_timestamp"" = :scope_last_sync_timestamp,
    ""scope_last_server_sync_timestamp"" = :scope_last_server_sync_timestamp,
    ""scope_last_sync_duration"" = :scope_last_sync_duration,
    ""scope_last_sync"" = :scope_last_sync,
    ""sync_scope_errors"" = :sync_scope_errors,
    ""sync_scope_properties"" = :sync_scope_properties
  WHERE ""sync_scope_name"" = :sync_scope_name AND ""sync_scope_id"" = :sync_scope_id AND ""sync_scope_hash"" = :sync_scope_hash;
  OPEN rc FOR SELECT {ScopeInfoClientColumns} FROM ""{this.clientTableName}""
    WHERE ""sync_scope_name"" = :sync_scope_name AND ""sync_scope_id"" = :sync_scope_id AND ""sync_scope_hash"" = :sync_scope_hash;
  DBMS_SQL.RETURN_RESULT(rc);
END;";
            var command = CreateCommand(connection, transaction, commandText);
            AddScopeInfoClientSaveParameters(command);
            return command;
        }

        /// <inheritdoc/>
        public override DbCommand GetDeleteScopeInfoClientCommand(DbConnection connection, DbTransaction transaction)
        {
            var command = CreateCommand(connection, transaction,
                $"DELETE FROM \"{this.clientTableName}\" " +
                "WHERE \"sync_scope_name\" = :sync_scope_name AND \"sync_scope_id\" = :sync_scope_id AND \"sync_scope_hash\" = :sync_scope_hash");
            AddScopeInfoClientKeyParameters(command);
            return command;
        }

        // ----------------------------------------------------------------------------------------
        // Local timestamp
        // ----------------------------------------------------------------------------------------

        /// <inheritdoc/>
        public override DbCommand GetLocalTimestampCommand(DbConnection connection, DbTransaction transaction)
            => CreateCommand(connection, transaction, $"SELECT {OracleObjectNames.TimestampValue} FROM DUAL");
    }
}
```

Notes:
- The legacy members `GetLocalTimestampAsync` (static), `NeedToCreateScopeInfoTableAsync` and `CreateScopeInfoTableScriptAsync` are intentionally **deleted** (dead code; `catch { throw; }` anti-pattern).
- `DBMS_SQL.RETURN_RESULT` requires Oracle ≥ 12.1; the provider's documented floor is 19c (Task 15).

- [ ] **Step 1.4: Run the tests to verify they pass**

Run: `dotnet test Tests/Dotmim.Sync.Tests/Dotmim.Sync.Tests.csproj -f net8.0 --filter "FullyQualifiedName~OracleScopeBuilderTests" --nologo`
Expected: 7 PASSED.

- [ ] **Step 1.5: Build the full solution projects that reference the scope builder**

Run: `dotnet build Projects/Dotmim.Sync.Oracle/Dotmim.Sync.Oracle.csproj -f net8.0 --nologo`
Expected: 0 errors. (If `GetLocalTimestampAsync`/`NeedToCreateScopeInfoTableAsync` were referenced anywhere, the compiler will say so — search with `grep -r "GetLocalTimestampAsync\|NeedToCreateScopeInfoTable" Projects/ Tests/` and remove those usages; as of the audit there are none.)

- [ ] **Step 1.6: Commit**

```powershell
git add Projects/Dotmim.Sync.Oracle/Builders/OracleScopeBuilder.cs Tests/Dotmim.Sync.Tests/UnitTests/Oracle/OracleScopeBuilderTests.cs
git commit -m "fix(oracle): rewrite scope builder to v1.x scope schema with canonical parameter names (C1, C2)"
```

---

### Task 2: Strip unreferenced bind variables in `EnsureCommandParameters` (fixes C3)

**Files:**
- Create: `Tests/Dotmim.Sync.Tests/UnitTests/Oracle/OracleSyncAdapterTests.cs`
- Modify: `Projects/Dotmim.Sync.Oracle/OracleSyncAdapter.cs`

- [ ] **Step 2.1: Write the failing tests**

Create `Tests/Dotmim.Sync.Tests/UnitTests/Oracle/OracleSyncAdapterTests.cs`:

```csharp
using Dotmim.Sync.Builders;
using Dotmim.Sync.Oracle;
using Oracle.ManagedDataAccess.Client;
using System;
using System.Data;
using System.Data.Common;
using System.Linq;
using Xunit;

namespace Dotmim.Sync.Tests.UnitTests.Oracle
{
    public class OracleSyncAdapterTests
    {
        private static (OracleSyncAdapter Adapter, SyncContext Context) BuildAdapter()
        {
            var table = new SyncTable("Product");
            table.Columns.Add(new SyncColumn("ProductId", typeof(Guid)));
            table.Columns.Add(new SyncColumn("Name", typeof(string)));
            table.PrimaryKeys.Add("ProductId");

            var schema = new SyncSet();
            schema.Tables.Add(table);

            var scopeInfo = new ScopeInfo { Name = "DefaultScope", Setup = new SyncSetup("Product") };
            var adapter = new OracleSyncAdapter(table, scopeInfo, useBulkOperations: false);
            var context = new SyncContext(Guid.NewGuid(), "DefaultScope");
            return (adapter, context);
        }

        private static DbParameter AddParameter(DbCommand command, string name, DbType dbType)
        {
            var p = command.CreateParameter();
            p.ParameterName = name;
            p.DbType = dbType;
            command.Parameters.Add(p);
            return p;
        }

        [Fact]
        public void EnsureCommandParameters_RemovesParametersNotReferencedInSql()
        {
            var (adapter, context) = BuildAdapter();
            var (command, _) = adapter.GetCommand(context, DbCommandType.SelectRow, null);

            // simulate what BaseOrchestrator.InternalSetSelectRowParameters adds
            AddParameter(command, ":ProductId", DbType.Guid);
            AddParameter(command, ":sync_scope_id", DbType.Guid);          // NOT in the SelectRow SQL
            var rowCount = AddParameter(command, ":sync_row_count", DbType.Int32); // NOT in the SelectRow SQL
            rowCount.Direction = ParameterDirection.Output;

            using var connection = new OracleConnection();
            adapter.EnsureCommandParameters(context, command, DbCommandType.SelectRow, connection, null);

            var names = command.Parameters.Cast<DbParameter>().Select(p => p.ParameterName).ToArray();
            Assert.Contains(":ProductId", names);
            Assert.DoesNotContain(":sync_scope_id", names);
            Assert.DoesNotContain(":sync_row_count", names);
        }

        [Fact]
        public void EnsureCommandParameters_KeepsOutputRowCountForPlSqlBlocks()
        {
            var (adapter, context) = BuildAdapter();
            var (command, _) = adapter.GetCommand(context, DbCommandType.UpdateRow, null);

            AddParameter(command, ":ProductId", DbType.Guid);
            AddParameter(command, ":Name", DbType.String);
            AddParameter(command, ":sync_scope_id", DbType.Guid);
            AddParameter(command, ":sync_force_write", DbType.Int64);
            AddParameter(command, ":sync_min_timestamp", DbType.Int64);
            var rowCount = AddParameter(command, ":sync_row_count", DbType.Int32);
            rowCount.Direction = ParameterDirection.Output;

            using var connection = new OracleConnection();
            adapter.EnsureCommandParameters(context, command, DbCommandType.UpdateRow, connection, null);

            // every one of these IS referenced in the PL/SQL block — none may be stripped
            Assert.Equal(6, command.Parameters.Count);
        }

        [Fact]
        public void EnsureCommandParameters_DoesNotMatchOnNamePrefix()
        {
            var (adapter, context) = BuildAdapter();

            using var connection = new OracleConnection();
            var command = connection.CreateCommand();
            command.CommandText = "SELECT * FROM \"Product\" WHERE \"ProductId\" = :ProductId";

            // ":Product" is a strict prefix of ":ProductId" — it must still be stripped
            AddParameter(command, ":Product", DbType.String);
            AddParameter(command, ":ProductId", DbType.Guid);

            adapter.EnsureCommandParameters(context, command, DbCommandType.SelectRow, connection, null);

            var names = command.Parameters.Cast<DbParameter>().Select(p => p.ParameterName).ToArray();
            Assert.Contains(":ProductId", names);
            Assert.DoesNotContain(":Product", names);
        }
    }
}
```

- [ ] **Step 2.2: Run the tests to verify they fail**

Run: `dotnet test Tests/Dotmim.Sync.Tests/Dotmim.Sync.Tests.csproj -f net8.0 --filter "FullyQualifiedName~OracleSyncAdapterTests" --nologo`
Expected: FAIL — `EnsureCommandParameters` currently keeps all parameters.

- [ ] **Step 2.3: Implement the stripping**

In `Projects/Dotmim.Sync.Oracle/OracleSyncAdapter.cs`, add `using System.Text.RegularExpressions;` to the usings and replace the `EnsureCommandParameters` method with:

```csharp
        /// <inheritdoc/>
        public override DbCommand EnsureCommandParameters(SyncContext context, DbCommand command, DbCommandType commandType, DbConnection connection, DbTransaction transaction, SyncFilter filter = null)
        {
            if (command is OracleCommand oracleCommand)
                oracleCommand.BindByName = true;

            // The orchestrator adds parameters some of our statements never reference
            // (e.g. :sync_scope_id / :sync_row_count on SelectRow, the PK parameters on
            // DeleteMetadata). ODP.NET raises ORA-01036 for binds with no matching
            // placeholder, so remove any parameter that does not appear in the SQL.
            for (var i = command.Parameters.Count - 1; i >= 0; i--)
            {
                var name = command.Parameters[i].ParameterName.TrimStart(':', '@');
                if (!Regex.IsMatch(command.CommandText, $@":{Regex.Escape(name)}\b", RegexOptions.IgnoreCase))
                    command.Parameters.RemoveAt(i);
            }

            // Coerce boolean parameters to NUMBER(1) before the command is prepared.
            // (DbType.Guid is handled natively by ODP.NET as RAW(16).)
            foreach (DbParameter parameter in command.Parameters)
            {
                if (parameter is OracleParameter oracleParameter && oracleParameter.DbType == DbType.Boolean)
                    oracleParameter.OracleDbType = OracleDbType.Int32;
            }

            return command;
        }
```

- [ ] **Step 2.4: Run the tests to verify they pass**

Run: `dotnet test Tests/Dotmim.Sync.Tests/Dotmim.Sync.Tests.csproj -f net8.0 --filter "FullyQualifiedName~OracleSyncAdapterTests" --nologo`
Expected: 3 PASSED.

- [ ] **Step 2.5: Commit**

```powershell
git add Projects/Dotmim.Sync.Oracle/OracleSyncAdapter.cs Tests/Dotmim.Sync.Tests/UnitTests/Oracle/OracleSyncAdapterTests.cs
git commit -m "fix(oracle): strip framework-added parameters with no matching bind to avoid ORA-01036 (C3)"
```

---

### Task 3: Emit filter custom joins (fixes C4)

**Files:**
- Create: `Tests/Dotmim.Sync.Tests/UnitTests/Oracle/OracleObjectNamesTests.cs`
- Modify: `Projects/Dotmim.Sync.Oracle/Builders/OracleObjectNames.cs`

- [ ] **Step 3.1: Write the failing tests**

Create `Tests/Dotmim.Sync.Tests/UnitTests/Oracle/OracleObjectNamesTests.cs`:

```csharp
using Dotmim.Sync.Builders;
using Dotmim.Sync.Oracle.Builders;
using System;
using Xunit;

namespace Dotmim.Sync.Tests.UnitTests.Oracle
{
    public class OracleObjectNamesTests
    {
        internal static OracleObjectNames BuildObjectNames()
        {
            var table = new SyncTable("Product");
            table.Columns.Add(new SyncColumn("ProductId", typeof(Guid)));
            table.Columns.Add(new SyncColumn("ProductCategoryId", typeof(Guid)));
            table.Columns.Add(new SyncColumn("Name", typeof(string)));
            table.PrimaryKeys.Add("ProductId");

            var schema = new SyncSet();
            schema.Tables.Add(table);

            var scopeInfo = new ScopeInfo { Name = "DefaultScope", Setup = new SyncSetup("Product") };
            return new OracleObjectNames(table, scopeInfo);
        }

        private static SyncFilter BuildJoinFilter()
        {
            var filter = new SyncFilter("Product");
            filter.Joins.Add(new SyncFilterJoin
            {
                JoinEnum = Join.Inner,
                TableName = "ProductCategory",
                LeftTableName = "Product",
                LeftColumnName = "ProductCategoryId",
                RightTableName = "ProductCategory",
                RightColumnName = "ProductCategoryId",
            });
            return filter;
        }

        [Fact]
        public void CreateFilterCustomJoins_EmitsJoinClause_AliasingFilterTableAsBase()
        {
            var objectNames = BuildObjectNames();

            var sql = objectNames.CreateFilterCustomJoins(BuildJoinFilter());

            Assert.Contains("INNER JOIN \"ProductCategory\"", sql);
            Assert.Contains("base.\"ProductCategoryId\" = \"ProductCategory\".\"ProductCategoryId\"", sql);
        }

        [Fact]
        public void SelectChangesWithFilters_ContainsCustomJoins()
        {
            var objectNames = BuildObjectNames();

            var sql = objectNames.GetCommandText(DbCommandType.SelectChangesWithFilters, BuildJoinFilter());

            Assert.Contains("INNER JOIN \"ProductCategory\"", sql);
        }

        [Fact]
        public void SelectInitializedChangesWithFilters_ContainsCustomJoins()
        {
            var objectNames = BuildObjectNames();

            var sql = objectNames.GetCommandText(DbCommandType.SelectInitializedChangesWithFilters, BuildJoinFilter());

            Assert.Contains("INNER JOIN \"ProductCategory\"", sql);
        }
    }
}
```

> If `SyncFilterJoin` has no parameterless constructor with settable properties, check `Projects/Dotmim.Sync.Core/Setup/SetupFilterJoin.cs` / `SyncFilterJoin` for the actual shape (the MySQL provider reads `JoinEnum`, `TableName`, `LeftTableName`, `LeftColumnName`, `RightTableName`, `RightColumnName` — `MySqlSyncAdapter.GetChanges.cs:38-74`) and construct it the way `Tests/Dotmim.Sync.Tests/UnitTests/SyncSetup/SetupFilterJoinTests.cs` does.

- [ ] **Step 3.2: Run the tests to verify they fail**

Run: `dotnet test Tests/Dotmim.Sync.Tests/Dotmim.Sync.Tests.csproj -f net8.0 --filter "FullyQualifiedName~OracleObjectNamesTests" --nologo`
Expected: FAIL — `CreateFilterCustomJoins` does not exist yet (compile error in test project is the expected failure mode; that is fine).

- [ ] **Step 3.3: Implement `CreateFilterCustomJoins` and wire it into both selects**

In `Projects/Dotmim.Sync.Oracle/Builders/OracleObjectNames.cs`, add this method to the `Filters` region (after `CreateFilterCustomWheres`):

```csharp
        /// <summary>
        /// Builds the custom JOIN clauses declared on the filter (ported from the MySQL provider,
        /// MySqlSyncAdapter.GetChanges.cs). The filter table itself is aliased as <c>base</c>.
        /// </summary>
        public string CreateFilterCustomJoins(SyncFilter filter)
        {
            var customJoins = filter.Joins;

            if (customJoins.Count == 0)
                return string.Empty;

            var stringBuilder = new StringBuilder();
            stringBuilder.AppendLine();

            foreach (var customJoin in customJoins)
            {
                switch (customJoin.JoinEnum)
                {
                    case Join.Left:
                        stringBuilder.Append("LEFT JOIN ");
                        break;
                    case Join.Right:
                        stringBuilder.Append("RIGHT JOIN ");
                        break;
                    case Join.Outer:
                        stringBuilder.Append("FULL OUTER JOIN ");
                        break;
                    case Join.Inner:
                    default:
                        stringBuilder.Append("INNER JOIN ");
                        break;
                }

                var filterTableParser = new TableParser(filter.TableName, LeftQuoteChar, RightQuoteChar);
                var joinTableParser = new TableParser(customJoin.TableName, LeftQuoteChar, RightQuoteChar);

                var leftTableParser = new TableParser(customJoin.LeftTableName, LeftQuoteChar, RightQuoteChar);
                var leftTableName = leftTableParser.QuotedShortName;
                if (string.Equals(filterTableParser.QuotedShortName, leftTableName, SyncGlobalization.DataSourceStringComparison))
                    leftTableName = "base";

                var rightTableParser = new TableParser(customJoin.RightTableName, LeftQuoteChar, RightQuoteChar);
                var rightTableName = rightTableParser.QuotedShortName;
                if (string.Equals(filterTableParser.QuotedShortName, rightTableName, SyncGlobalization.DataSourceStringComparison))
                    rightTableName = "base";

                var leftColumnParser = new ObjectParser(customJoin.LeftColumnName, LeftQuoteChar, RightQuoteChar);
                var rightColumnParser = new ObjectParser(customJoin.RightColumnName, LeftQuoteChar, RightQuoteChar);

                stringBuilder.AppendLine($"{joinTableParser.QuotedShortName} ON {leftTableName}.{leftColumnParser.QuotedShortName} = {rightTableName}.{rightColumnParser.QuotedShortName}");
            }

            return stringBuilder.ToString();
        }
```

Then in `CreateSelectIncrementalChangesCommand`, immediately after the tracking-table join line

```csharp
            stringBuilder.AppendLine(this.PrimaryKeyJoin("base", "side"));
```

insert:

```csharp
            if (filter != null)
                stringBuilder.Append(this.CreateFilterCustomJoins(filter));
```

And in `CreateSelectInitializedChangesCommand`, after the **first** (LEFT JOIN) `PrimaryKeyJoin` line, insert the same two lines. (Do not add joins to the tombstone UNION branch — MySQL does not either.)

- [ ] **Step 3.4: Run the tests to verify they pass**

Run: `dotnet test Tests/Dotmim.Sync.Tests/Dotmim.Sync.Tests.csproj -f net8.0 --filter "FullyQualifiedName~OracleObjectNamesTests" --nologo`
Expected: 3 PASSED.

- [ ] **Step 3.5: Commit**

```powershell
git add Projects/Dotmim.Sync.Oracle/Builders/OracleObjectNames.cs Tests/Dotmim.Sync.Tests/UnitTests/Oracle/OracleObjectNamesTests.cs
git commit -m "fix(oracle): emit filter custom joins in select changes commands (C4)"
```

---

### Task 4: Fix `OracleDatabaseBuilder` — owner resolution, casing, async (fixes M1)

**Files:**
- Rewrite: `Projects/Dotmim.Sync.Oracle/Builders/OracleDatabaseBuilder.cs`

No unit test is possible without a live database (every method executes SQL); correctness is asserted by the live checklist (Task 16). The fix is mechanical and reviewed by build.

- [ ] **Step 4.1: Verify the legacy helpers are dead code**

Run: `Select-String -Path Projects\**\*.cs,Tests\**\*.cs,Samples\**\*.cs -Pattern "ProcedureExistsAsync|SchemaExistsAsync|TriggerExistsAsync|TypeExistsAsync|DropProcedureAsync|DropTriggerAsync|DatabaseExistsAsync|CreateDatabaseAsync\(" | Where-Object Path -match "Oracle"`
Expected: matches only inside `OracleDatabaseBuilder.cs` itself (definitions + the internal `DropProcedureAsync`→`ProcedureExistsAsync` calls). If any external caller exists, keep that helper and fix it the same way as `ExistsTableAsync` below.

- [ ] **Step 4.2: Replace the file content**

Replace the full content of `Projects/Dotmim.Sync.Oracle/Builders/OracleDatabaseBuilder.cs` with:

```csharp
using Dotmim.Sync.Builders;
using Dotmim.Sync.DatabaseStringParsers;
using System;
using System.Data;
using System.Data.Common;
using System.Threading.Tasks;

namespace Dotmim.Sync.Oracle.Builders
{
    /// <summary>
    /// Oracle implementation of the DbDatabaseBuilder.
    /// <para>
    /// Identifier policy: sync objects are created quoted (case-preserved), so every
    /// dictionary lookup compares the exact string. When no schema is provided the
    /// current user's views (USER_*) are used — ODP.NET's
    /// <see cref="DbConnection.Database"/> returns an empty string, so it must never be
    /// used as an OWNER filter.
    /// </para>
    /// </summary>
    public class OracleDatabaseBuilder : DbDatabaseBuilder
    {
        /// <summary>
        /// First step before creating schema. Oracle has no "database" to create
        /// (a database is a user/schema, created by an administrator).
        /// </summary>
        public override Task EnsureDatabaseAsync(DbConnection connection, DbTransaction transaction = null)
            => Task.CompletedTask;

        /// <inheritdoc/>
        public override Task<SyncTable> EnsureTableAsync(string tableName, string schemaName, DbConnection connection, DbTransaction transaction = null)
            => Task.FromResult(string.IsNullOrEmpty(schemaName) ? new SyncTable(tableName) : new SyncTable(tableName, schemaName));

        /// <inheritdoc/>
        public override async Task<SyncSetup> GetAllTablesAsync(DbConnection connection, DbTransaction transaction = null)
        {
            var setup = new SyncSetup();

            var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = "SELECT TABLE_NAME FROM USER_TABLES ORDER BY TABLE_NAME";

            var alreadyOpened = connection.State == ConnectionState.Open;
            try
            {
                if (!alreadyOpened)
                    await connection.OpenAsync().ConfigureAwait(false);

                using var reader = await command.ExecuteReaderAsync().ConfigureAwait(false);
                while (await reader.ReadAsync().ConfigureAwait(false))
                    setup.Tables.Add(new SetupTable(reader.GetString(0)));
            }
            finally
            {
                if (!alreadyOpened && connection.State == ConnectionState.Open)
                    await connection.CloseAsync().ConfigureAwait(false);
            }

            return setup;
        }

        /// <inheritdoc/>
        public override async Task<(string DatabaseName, string Version)> GetHelloAsync(DbConnection connection, DbTransaction transaction = null)
        {
            var databaseNameCommand = connection.CreateCommand();
            databaseNameCommand.Transaction = transaction;
            databaseNameCommand.CommandText = "SELECT SYS_CONTEXT('USERENV', 'CURRENT_SCHEMA') FROM DUAL";

            // PRODUCT_COMPONENT_VERSION is granted to PUBLIC, unlike V$VERSION.
            var versionCommand = connection.CreateCommand();
            versionCommand.Transaction = transaction;
            versionCommand.CommandText = "SELECT VERSION FROM PRODUCT_COMPONENT_VERSION WHERE PRODUCT LIKE 'Oracle%' AND ROWNUM = 1";

            var alreadyOpened = connection.State == ConnectionState.Open;
            try
            {
                if (!alreadyOpened)
                    await connection.OpenAsync().ConfigureAwait(false);

                var databaseName = (await databaseNameCommand.ExecuteScalarAsync().ConfigureAwait(false))?.ToString() ?? "Oracle";
                var version = (await versionCommand.ExecuteScalarAsync().ConfigureAwait(false))?.ToString() ?? "Unknown";

                return (databaseName, version);
            }
            finally
            {
                if (!alreadyOpened && connection.State == ConnectionState.Open)
                    await connection.CloseAsync().ConfigureAwait(false);
            }
        }

        /// <inheritdoc/>
        public override async Task<SyncTable> GetTableAsync(string tableName, string schemaName, DbConnection connection, DbTransaction transaction = null)
        {
            string parsedName = GetParsedTableName(tableName);
            var syncTable = string.IsNullOrEmpty(schemaName) ? new SyncTable(parsedName) : new SyncTable(parsedName, schemaName);

            var alreadyOpened = connection.State == ConnectionState.Open;
            try
            {
                if (!alreadyOpened)
                    await connection.OpenAsync().ConfigureAwait(false);

                // Columns
                var columnsCommand = connection.CreateCommand();
                columnsCommand.Transaction = transaction;
                columnsCommand.CommandText = @"
                    SELECT COLUMN_NAME, DATA_TYPE, DATA_LENGTH, DATA_PRECISION, DATA_SCALE, NULLABLE, CHAR_LENGTH
                    FROM USER_TAB_COLUMNS WHERE TABLE_NAME = :tableName ORDER BY COLUMN_ID";
                var p = columnsCommand.CreateParameter();
                p.ParameterName = ":tableName";
                p.Value = parsedName;
                columnsCommand.Parameters.Add(p);

                using (var reader = await columnsCommand.ExecuteReaderAsync().ConfigureAwait(false))
                {
                    while (await reader.ReadAsync().ConfigureAwait(false))
                    {
                        var dataType = reader.GetString(1);
                        var dataLength = reader.IsDBNull(2) ? 0 : Convert.ToInt32(reader.GetValue(2));
                        var precision = reader.IsDBNull(3) ? (byte)0 : Convert.ToByte(reader.GetValue(3));
                        var scale = reader.IsDBNull(4) ? (byte)0 : Convert.ToByte(reader.GetValue(4));
                        var column = new SyncColumn(reader.GetString(0))
                        {
                            OriginalTypeName = dataType,
                            AllowDBNull = reader.GetString(5) == "Y",
                            MaxLength = dataType.Contains("CHAR", StringComparison.OrdinalIgnoreCase)
                                ? (reader.IsDBNull(6) ? 0 : Convert.ToInt32(reader.GetValue(6)))
                                : dataLength,
                            Precision = precision,
                            Scale = scale,
                        };
                        column.SetType(OracleTableBuilder.GetManagedType(dataType, precision, scale, dataLength));
                        syncTable.Columns.Add(column);
                    }
                }

                // Primary keys
                var pkCommand = connection.CreateCommand();
                pkCommand.Transaction = transaction;
                pkCommand.CommandText = @"
                    SELECT cols.COLUMN_NAME FROM USER_CONSTRAINTS cons
                    JOIN USER_CONS_COLUMNS cols ON cons.CONSTRAINT_NAME = cols.CONSTRAINT_NAME
                    WHERE cons.TABLE_NAME = :tableName AND cons.CONSTRAINT_TYPE = 'P' ORDER BY cols.POSITION";
                var pkp = pkCommand.CreateParameter();
                pkp.ParameterName = ":tableName";
                pkp.Value = parsedName;
                pkCommand.Parameters.Add(pkp);

                using (var reader = await pkCommand.ExecuteReaderAsync().ConfigureAwait(false))
                {
                    while (await reader.ReadAsync().ConfigureAwait(false))
                        syncTable.PrimaryKeys.Add(reader.GetString(0));
                }
            }
            finally
            {
                if (!alreadyOpened && connection.State == ConnectionState.Open)
                    await connection.CloseAsync().ConfigureAwait(false);
            }

            return syncTable;
        }

        /// <inheritdoc/>
        public override async Task<bool> ExistsTableAsync(string tableName, string schemaName, DbConnection connection, DbTransaction transaction = null)
        {
            if (string.IsNullOrEmpty(tableName))
                throw new ArgumentNullException(nameof(tableName));

            var parsedName = GetParsedTableName(tableName);

            var command = connection.CreateCommand();
            command.Transaction = transaction;

            if (string.IsNullOrEmpty(schemaName))
            {
                // current user's tables; never rely on connection.Database (empty in ODP.NET)
                command.CommandText = "SELECT COUNT(*) FROM USER_TABLES WHERE TABLE_NAME = :name";
            }
            else
            {
                command.CommandText = "SELECT COUNT(*) FROM ALL_TABLES WHERE OWNER = :owner AND TABLE_NAME = :name";
                var ownerParam = command.CreateParameter();
                ownerParam.ParameterName = ":owner";
                ownerParam.Value = schemaName;
                command.Parameters.Add(ownerParam);
            }

            var nameParam = command.CreateParameter();
            nameParam.ParameterName = ":name";
            nameParam.Value = parsedName;
            command.Parameters.Add(nameParam);

            var alreadyOpened = connection.State == ConnectionState.Open;
            try
            {
                if (!alreadyOpened)
                    await connection.OpenAsync().ConfigureAwait(false);

                return Convert.ToInt32(await command.ExecuteScalarAsync().ConfigureAwait(false)) > 0;
            }
            finally
            {
                if (!alreadyOpened && connection.State == ConnectionState.Open)
                    await connection.CloseAsync().ConfigureAwait(false);
            }
        }

        /// <inheritdoc/>
        public override async Task DropsTableIfExistsAsync(string tableName, string schemaName, DbConnection connection, DbTransaction transaction = null)
        {
            if (string.IsNullOrEmpty(tableName))
                throw new ArgumentNullException(nameof(tableName));

            var exists = await this.ExistsTableAsync(tableName, schemaName, connection, transaction).ConfigureAwait(false);
            if (!exists)
                return;

            var parsedName = GetParsedTableName(tableName);
            var qualifiedTableName = string.IsNullOrEmpty(schemaName)
                ? $"\"{parsedName}\""
                : $"\"{schemaName}\".\"{parsedName}\"";

            var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = $"DROP TABLE {qualifiedTableName}";

            var alreadyOpened = connection.State == ConnectionState.Open;
            try
            {
                if (!alreadyOpened)
                    await connection.OpenAsync().ConfigureAwait(false);

                await command.ExecuteNonQueryAsync().ConfigureAwait(false);
            }
            finally
            {
                if (!alreadyOpened && connection.State == ConnectionState.Open)
                    await connection.CloseAsync().ConfigureAwait(false);
            }
        }

        /// <inheritdoc/>
        public override async Task RenameTableAsync(string tableName, string schemaName, string newTableName, string newSchemaName, DbConnection connection, DbTransaction transaction = null)
        {
            if (string.IsNullOrEmpty(tableName))
                throw new ArgumentNullException(nameof(tableName));
            if (string.IsNullOrEmpty(newTableName))
                throw new ArgumentNullException(nameof(newTableName));

            var parsedName = GetParsedTableName(tableName);
            var parsedNewName = GetParsedTableName(newTableName);

            var qualifiedTableName = string.IsNullOrEmpty(schemaName)
                ? $"\"{parsedName}\""
                : $"\"{schemaName}\".\"{parsedName}\"";

            var command = connection.CreateCommand();
            command.Transaction = transaction;

            // Oracle's RENAME TO renames within the owning schema; moving a table across
            // schemas is not a rename and is not supported here.
            command.CommandText = $"ALTER TABLE {qualifiedTableName} RENAME TO \"{parsedNewName}\"";

            var alreadyOpened = connection.State == ConnectionState.Open;
            try
            {
                if (!alreadyOpened)
                    await connection.OpenAsync().ConfigureAwait(false);

                await command.ExecuteNonQueryAsync().ConfigureAwait(false);
            }
            finally
            {
                if (!alreadyOpened && connection.State == ConnectionState.Open)
                    await connection.CloseAsync().ConfigureAwait(false);
            }
        }

        private static string GetParsedTableName(string tableName)
        {
            var tableParser = new TableParser(tableName, '"', '"');
            return tableParser.TableName;
        }
    }
}
```

Notes: all `Task.Run` / `ContinueWith().Unwrap()` wrappers, `ToUpperInvariant()` calls, the `connection.Database` OWNER filter, and the ten dead legacy helpers are gone. `GetManagedType` gains a `dataLength` argument in Task 10 — **if executing tasks out of order**, keep the call as `GetManagedType(dataType, precision, scale)` until Task 10 lands.

- [ ] **Step 4.3: Build**

Run: `dotnet build Projects/Dotmim.Sync.Oracle/Dotmim.Sync.Oracle.csproj -f net8.0 --nologo`
Expected: 0 errors. (A CS1501 on `GetManagedType` means Task 10 hasn't landed — use the 3-argument call for now, see note above.)

- [ ] **Step 4.4: Commit**

```powershell
git add Projects/Dotmim.Sync.Oracle/Builders/OracleDatabaseBuilder.cs
git commit -m "fix(oracle): database builder uses USER_* views and case-preserved identifiers; remove Task.Run wrappers and dead helpers (M1)"
```

---

### Task 5: `ENABLE NOVALIDATE` + quote-escaped table names in constraint commands (fixes M2)

**Files:**
- Modify: `Projects/Dotmim.Sync.Oracle/Builders/OracleObjectNames.cs`
- Modify: `Tests/Dotmim.Sync.Tests/UnitTests/Oracle/OracleObjectNamesTests.cs`

- [ ] **Step 5.1: Write the failing tests** — add to `OracleObjectNamesTests`:

```csharp
        [Fact]
        public void EnableConstraints_UsesNovalidate()
        {
            var sql = BuildObjectNames().GetCommandText(DbCommandType.EnableConstraints);
            Assert.Contains("ENABLE NOVALIDATE CONSTRAINT", sql);
            Assert.DoesNotContain("' ENABLE CONSTRAINT", sql);
        }

        [Fact]
        public void DisableConstraints_EscapesSingleQuotesInTableName()
        {
            var sql = BuildObjectNames().GetCommandText(DbCommandType.DisableConstraints);
            // the table name is embedded as a SQL string literal — sanity: literal present
            Assert.Contains("= 'Product'", sql);
        }
```

- [ ] **Step 5.2: Run to verify the first test fails**

Run: `dotnet test Tests/Dotmim.Sync.Tests/Dotmim.Sync.Tests.csproj -f net8.0 --filter "FullyQualifiedName~OracleObjectNamesTests" --nologo`
Expected: `EnableConstraints_UsesNovalidate` FAILS.

- [ ] **Step 5.3: Implement**

In `OracleObjectNames.CreateDisableConstraintsCommand` and `CreateEnableConstraintsCommand`:
1. Change the first line of both methods to escape quotes: `var tableName = this.TableName.Replace("'", "''");`
2. In `CreateEnableConstraintsCommand`, change both `ENABLE CONSTRAINT` occurrences to `ENABLE NOVALIDATE CONSTRAINT` (Oracle's plain `ENABLE` means ENABLE VALIDATE, which rescans all rows and fails on orphans mid-sync; SqlServer's `CHECK CONSTRAINT ALL` re-enables without validating — NOVALIDATE is the parity behavior).

The two changed `EXECUTE IMMEDIATE` lines become:

```csharp
            sb.AppendLine($"    EXECUTE IMMEDIATE 'ALTER TABLE {this.TableQuotedFullName} ENABLE NOVALIDATE CONSTRAINT \"' || c.constraint_name || '\"';");
```
and
```csharp
            sb.AppendLine("    EXECUTE IMMEDIATE 'ALTER TABLE \"' || c.table_name || '\" ENABLE NOVALIDATE CONSTRAINT \"' || c.constraint_name || '\"';");
```

- [ ] **Step 5.4: Run the tests to verify they pass**

Run: `dotnet test Tests/Dotmim.Sync.Tests/Dotmim.Sync.Tests.csproj -f net8.0 --filter "FullyQualifiedName~OracleObjectNamesTests" --nologo`
Expected: all PASS.

- [ ] **Step 5.5: Commit**

```powershell
git add Projects/Dotmim.Sync.Oracle/Builders/OracleObjectNames.cs Tests/Dotmim.Sync.Tests/UnitTests/Oracle/OracleObjectNamesTests.cs
git commit -m "fix(oracle): re-enable constraints with NOVALIDATE and escape table-name literals (M2)"
```

---

### Task 6: Correct the transient-error set (fixes M4)

**Files:**
- Modify: `Projects/Dotmim.Sync.Oracle/OracleTransientExceptionDetector.cs`
- Create: `Tests/Dotmim.Sync.Tests/UnitTests/Oracle/OracleMiscTests.cs`

- [ ] **Step 6.1: Write the failing tests**

Create `Tests/Dotmim.Sync.Tests/UnitTests/Oracle/OracleMiscTests.cs`:

```csharp
using Dotmim.Sync.Oracle;
using Xunit;

namespace Dotmim.Sync.Tests.UnitTests.Oracle
{
    public class OracleMiscTests
    {
        [Theory]
        [InlineData(60, true)]     // deadlock detected — transient
        [InlineData(2049, true)]   // distributed lock timeout — transient
        [InlineData(3113, true)]   // end-of-file on communication channel — transient
        [InlineData(942, false)]   // table or view does not exist — permanent
        [InlineData(1542, false)]  // tablespace offline — permanent (was wrongly retried)
        [InlineData(12154, false)] // cannot resolve connect identifier — config error, permanent
        public void IsTransient_ClassifiesOracleErrorNumbers(int number, bool expected)
            => Assert.Equal(expected, OracleTransientExceptionDetector.IsTransient(number));
    }
}
```

(`OracleException` has no public constructor, so the testable surface is the error-number classifier.)

- [ ] **Step 6.2: Run to verify it fails**

Run: `dotnet test Tests/Dotmim.Sync.Tests/Dotmim.Sync.Tests.csproj -f net8.0 --filter "FullyQualifiedName~OracleMiscTests" --nologo`
Expected: FAIL (no `IsTransient` method; 1542/12154 currently retried).

- [ ] **Step 6.3: Implement**

In `OracleTransientExceptionDetector.cs`:
1. Remove `12154` and `1542` from the set (and their comments).
2. Add to the concurrency section:
```csharp
            // Concurrency errors
            60,    // deadlock detected while waiting for resource
            2049,  // distributed lock timeout
```
3. Add the classifier and route `ShouldRetryOn` through it:
```csharp
        /// <summary>
        /// Returns true when the Oracle error number is considered transient.
        /// </summary>
        public static bool IsTransient(int oracleErrorNumber) => transientErrorNumbers.Contains(oracleErrorNumber);

        /// <summary>
        /// Determines whether the specified exception should be retried.
        /// </summary>
        public static bool ShouldRetryOn(Exception ex)
        {
            if (ex is OracleException oracleException)
                return IsTransient(oracleException.Number);

            return false;
        }
```

- [ ] **Step 6.4: Run the tests to verify they pass**

Run: `dotnet test Tests/Dotmim.Sync.Tests/Dotmim.Sync.Tests.csproj -f net8.0 --filter "FullyQualifiedName~OracleMiscTests" --nologo`
Expected: 6 PASSED.

- [ ] **Step 6.5: Commit**

```powershell
git add Projects/Dotmim.Sync.Oracle/OracleTransientExceptionDetector.cs Tests/Dotmim.Sync.Tests/UnitTests/Oracle/OracleMiscTests.cs
git commit -m "fix(oracle): stop retrying permanent errors (942-family, 12154); retry deadlocks (M4)"
```

---

### Task 7: Database name = user/schema (fixes M5)

**Files:**
- Modify: `Projects/Dotmim.Sync.Oracle/OracleSyncProvider.cs`
- Modify: `Tests/Dotmim.Sync.Tests/UnitTests/Oracle/OracleMiscTests.cs`

- [ ] **Step 7.1: Write the failing test** — add to `OracleMiscTests`:

```csharp
        [Fact]
        public void GetDatabaseName_ReturnsUserSchema_NotDataSource()
        {
            var provider = new Dotmim.Sync.Oracle.OracleSyncProvider(
                "Data Source=localhost:1521/FREEPDB1;User Id=SCOTT;Password=x;");

            // In Oracle the "database" is the user/schema (DataSource is the host/TNS alias)
            Assert.Equal("SCOTT", provider.GetDatabaseName());
        }
```

- [ ] **Step 7.2: Run to verify it fails**

Run: `dotnet test Tests/Dotmim.Sync.Tests/Dotmim.Sync.Tests.csproj -f net8.0 --filter "FullyQualifiedName~OracleMiscTests" --nologo`
Expected: the new test FAILS (returns `localhost:1521/FREEPDB1`).

- [ ] **Step 7.3: Implement**

In `OracleSyncProvider.cs`:

```csharp
        /// <inheritdoc/>
        public override string GetDatabaseName() => this.builder?.UserID ?? string.Empty;
```

and extend `EnsureSyncException` so the catalog is populated like SqlServer does:

```csharp
        /// <inheritdoc/>
        public override void EnsureSyncException(SyncException syncException)
        {
            if (this.builder != null && !string.IsNullOrEmpty(this.builder.ConnectionString))
            {
                syncException.DataSource = this.builder.DataSource;
                syncException.InitialCatalog = this.builder.UserID;
            }

            if (syncException.InnerException is not OracleException oracleException)
                return;

            syncException.Number = oracleException.Number;
        }
```

- [ ] **Step 7.4: Run tests, build, commit**

Run: `dotnet test Tests/Dotmim.Sync.Tests/Dotmim.Sync.Tests.csproj -f net8.0 --filter "FullyQualifiedName~OracleMiscTests" --nologo` → all PASS.

```powershell
git add Projects/Dotmim.Sync.Oracle/OracleSyncProvider.cs Tests/Dotmim.Sync.Tests/UnitTests/Oracle/OracleMiscTests.cs
git commit -m "fix(oracle): database name is the user/schema; enrich SyncException with catalog (M5)"
```

---

### Task 8: Tracking-table index + identifier-length guard (fixes M6 + M3)

**Files:**
- Modify: `Projects/Dotmim.Sync.Oracle/Builders/OracleObjectNames.cs`
- Modify: `Tests/Dotmim.Sync.Tests/UnitTests/Oracle/OracleObjectNamesTests.cs`

- [ ] **Step 8.1: Write the failing tests** — add to `OracleObjectNamesTests`:

```csharp
        [Fact]
        public void CreateTrackingTableScript_CreatesTableAndTimestampIndex()
        {
            var objectNames = BuildObjectNames();

            var script = objectNames.CreateTrackingTableScript(_ => "RAW(16)");

            // two DDL statements wrapped in one PL/SQL block
            Assert.Contains("EXECUTE IMMEDIATE 'CREATE TABLE", script);
            Assert.Contains("EXECUTE IMMEDIATE 'CREATE INDEX", script);
            Assert.Contains("(\"timestamp\")", script);
        }

        [Fact]
        public void GetTriggerCommandName_ThrowsWhenIdentifierExceeds128Bytes()
        {
            var longName = new string('X', 125); // + "_insert_trigger" => > 128
            var table = new SyncTable(longName);
            table.Columns.Add(new SyncColumn("Id", typeof(int)));
            table.PrimaryKeys.Add("Id");
            var scopeInfo = new ScopeInfo { Name = "DefaultScope", Setup = new SyncSetup(longName) };
            var objectNames = new OracleObjectNames(table, scopeInfo);

            Assert.Throws<ArgumentException>(() => objectNames.GetTriggerCommandName(Dotmim.Sync.Builders.DbTriggerType.Insert));
        }
```

- [ ] **Step 8.2: Run to verify they fail**

Run: `dotnet test Tests/Dotmim.Sync.Tests/Dotmim.Sync.Tests.csproj -f net8.0 --filter "FullyQualifiedName~OracleObjectNamesTests" --nologo`
Expected: both new tests FAIL.

- [ ] **Step 8.3: Implement**

In `OracleObjectNames.cs`:

1. Add the guard helper (near the other identifier helpers):

```csharp
        /// <summary>
        /// Oracle 12.2+ limits identifiers to 128 bytes. Generated names (tracking table,
        /// triggers, index, constraints) must fit, otherwise fail fast with a clear message.
        /// </summary>
        internal static string EnsureIdentifierLength(string identifier)
        {
            if (System.Text.Encoding.UTF8.GetByteCount(identifier) > 128)
                throw new ArgumentException($"Generated Oracle identifier '{identifier}' exceeds the 128-byte limit. Use shorter table names or shorter tracking/trigger prefixes-suffixes.");

            return identifier;
        }
```

2. In `GetTriggerCommandName`, wrap the return: `return EnsureIdentifierLength(name);`
3. In the constructor, wrap the tracking name assignment: `this.TrackingTableName = EnsureIdentifierLength(trackingTableParser.TableName);`
4. Replace `CreateTrackingTableScript` with a PL/SQL block that creates the table **and** a `timestamp` index (the DeleteMetadata / SelectChanges scans need it — SqlServer creates an equivalent index):

```csharp
        /// <summary>
        /// Builds a PL/SQL block creating the tracking table (primary keys + sync metadata
        /// columns) and its timestamp index. Two EXECUTE IMMEDIATE calls because Oracle cannot
        /// batch two DDL statements in a single command.
        /// </summary>
        public string CreateTrackingTableScript(Func<SyncColumn, string> columnTypeResolver)
        {
            var createTable = new StringBuilder();
            createTable.AppendLine($"CREATE TABLE {this.TrackingTableQuotedFullName} (");

            foreach (var pk in this.tableDescription.GetPrimaryKeysColumns())
                createTable.AppendLine($"  {Quoted(pk.ColumnName)} {columnTypeResolver(pk)} NOT NULL,");

            createTable.AppendLine("  \"update_scope_id\" RAW(16) NULL,");
            createTable.AppendLine("  \"timestamp\" NUMBER(19) NULL,");
            createTable.AppendLine("  \"sync_row_is_tombstone\" NUMBER(1) DEFAULT 0 NOT NULL,");
            createTable.AppendLine("  \"last_change_datetime\" TIMESTAMP NULL,");

            var pkList = string.Join(", ", this.tableDescription.GetPrimaryKeysColumns().Select(c => Quoted(c.ColumnName)));
            createTable.AppendLine($"  CONSTRAINT {Quoted(EnsureIdentifierLength($"PK_{this.TrackingTableName}"))} PRIMARY KEY ({pkList})");
            createTable.AppendLine(")");

            var indexName = EnsureIdentifierLength($"{this.TrackingTableName}_ts_idx");
            var createIndex = $"CREATE INDEX {Quoted(indexName)} ON {this.TrackingTableQuotedFullName} (\"timestamp\")";

            var sb = new StringBuilder();
            sb.AppendLine("BEGIN");
            sb.AppendLine($"  EXECUTE IMMEDIATE '{createTable.ToString().Replace("'", "''")}';");
            sb.AppendLine($"  EXECUTE IMMEDIATE '{createIndex.Replace("'", "''")}';");
            sb.AppendLine("END;");
            return sb.ToString();
        }
```

- [ ] **Step 8.4: Run the tests to verify they pass**

Run: `dotnet test Tests/Dotmim.Sync.Tests/Dotmim.Sync.Tests.csproj -f net8.0 --filter "FullyQualifiedName~OracleObjectNamesTests" --nologo`
Expected: all PASS.

- [ ] **Step 8.5: Commit**

```powershell
git add Projects/Dotmim.Sync.Oracle/Builders/OracleObjectNames.cs Tests/Dotmim.Sync.Tests/UnitTests/Oracle/OracleObjectNamesTests.cs
git commit -m "feat(oracle): index tracking timestamp column; guard 128-byte identifier limit (M6, M3)"
```

---

### Task 9: Single-evaluation timestamp clock (minor)

**Files:**
- Modify: `Projects/Dotmim.Sync.Oracle/Builders/OracleObjectNames.cs`
- Modify: `Tests/Dotmim.Sync.Tests/UnitTests/Oracle/OracleObjectNamesTests.cs`

The current `TimestampValue` calls `SYS_EXTRACT_UTC(SYSTIMESTAMP)` twice (seconds part and `FF6` fraction part); a second-boundary between the two evaluations can produce a reading up to ~1s off. Compute both parts from one evaluation via an inline view (scalar subquery — legal in SELECT lists, MERGE SET/VALUES, and INSERT … SELECT, which are the only contexts the constant is used in).

- [ ] **Step 9.1: Write the failing test** — add to `OracleObjectNamesTests`:

```csharp
        [Fact]
        public void TimestampValue_EvaluatesSystimestampExactlyOnce()
        {
            var occurrences = System.Text.RegularExpressions.Regex
                .Matches(OracleObjectNames.TimestampValue, "SYSTIMESTAMP").Count;
            Assert.Equal(1, occurrences);
        }
```

- [ ] **Step 9.2: Run to verify it fails** (current constant contains SYSTIMESTAMP twice).

- [ ] **Step 9.3: Implement** — replace the `TimestampValue` constant:

```csharp
        /// <summary>
        /// Monotonic, UTC, epoch-based version expression with ~100µs resolution (mirrors the
        /// MySQL provider's <c>ROUND(UNIX_TIMESTAMP(CURRENT_TIMESTAMP(6)) * 10000)</c>).
        /// The same clock is used by the triggers, the apply-merge, UpdateUntrackedRows and
        /// <c>GetLocalTimestamp</c>. A scalar subquery is used so SYSTIMESTAMP is evaluated
        /// exactly once per reading (seconds and fractional parts cannot straddle a second
        /// boundary).
        /// </summary>
        public const string TimestampValue =
            "(SELECT ROUND(((CAST(u AS DATE) - DATE '1970-01-01') * 86400 " +
            "+ TO_NUMBER(TO_CHAR(u, 'FF6')) / 1000000) * 10000) " +
            "FROM (SELECT SYS_EXTRACT_UTC(SYSTIMESTAMP) u FROM DUAL))";
```

- [ ] **Step 9.4: Run all Oracle unit tests to verify nothing else regressed**

Run: `dotnet test Tests/Dotmim.Sync.Tests/Dotmim.Sync.Tests.csproj -f net8.0 --filter "FullyQualifiedName~UnitTests.Oracle" --nologo`
Expected: all PASS.

- [ ] **Step 9.5: Commit**

```powershell
git add Projects/Dotmim.Sync.Oracle/Builders/OracleObjectNames.cs Tests/Dotmim.Sync.Tests/UnitTests/Oracle/OracleObjectNamesTests.cs
git commit -m "fix(oracle): evaluate the timestamp clock from a single SYSTIMESTAMP reading"
```

---

### Task 10: RAW(16) → Guid inbound mapping + InternalsVisibleTo (minor)

**Files:**
- Create: `Projects/Dotmim.Sync.Oracle/InternalsVisibility.cs`
- Modify: `Projects/Dotmim.Sync.Oracle/Builders/OracleTableBuilder.cs`
- Modify: `Projects/Dotmim.Sync.Oracle/Builders/OracleDatabaseBuilder.cs` (call-site)
- Modify: `Tests/Dotmim.Sync.Tests/UnitTests/Oracle/OracleMiscTests.cs`

- [ ] **Step 10.1: Create `Projects/Dotmim.Sync.Oracle/InternalsVisibility.cs`** (mirrors the SqlServer project):

```csharp
using System.Runtime.CompilerServices;

[assembly: InternalsVisibleTo("Dotmim.Sync.Tests")]
```

- [ ] **Step 10.2: Write the failing tests** — add to `OracleMiscTests`:

```csharp
        [Theory]
        [InlineData("RAW", 16, typeof(System.Guid))]    // RAW(16) is how this provider stores GUIDs
        [InlineData("RAW", 32, typeof(byte[]))]
        [InlineData("BLOB", 0, typeof(byte[]))]
        [InlineData("VARCHAR2", 0, typeof(string))]
        public void GetManagedType_MapsRaw16ToGuid(string oracleType, int length, System.Type expected)
            => Assert.Equal(expected, Dotmim.Sync.Oracle.Builders.OracleTableBuilder.GetManagedType(oracleType, 0, 0, length));
```

- [ ] **Step 10.3: Run to verify it fails** (signature has no length parameter yet → compile failure of the test project is the expected failure mode).

- [ ] **Step 10.4: Implement**

In `OracleTableBuilder.cs`, change `GetManagedType` to take the data length and map RAW(16) to `Guid`:

```csharp
        /// <summary>
        /// Maps an Oracle native type name to a managed CLR type (used when reading an existing schema).
        /// RAW(16) is mapped to <see cref="Guid"/> because that is how this provider stores GUIDs
        /// (same heuristic as MySQL's char(36) → Guid).
        /// </summary>
        internal static Type GetManagedType(string oracleType, byte precision, byte scale, int dataLength = 0)
        {
            var type = oracleType.ToUpperInvariant();

            if (type.StartsWith("TIMESTAMP", StringComparison.Ordinal))
                return type.Contains("TIME ZONE") ? typeof(DateTimeOffset) : typeof(DateTime);

            return type switch
            {
                "NUMBER" => scale > 0
                    ? typeof(decimal)
                    : precision == 0 ? typeof(decimal)
                    : precision <= 4 ? typeof(short)
                    : precision <= 9 ? typeof(int)
                    : precision <= 18 ? typeof(long)
                    : typeof(decimal),
                "FLOAT" => typeof(decimal),
                "BINARY_FLOAT" => typeof(float),
                "BINARY_DOUBLE" => typeof(double),
                "DATE" => typeof(DateTime),
                "RAW" when dataLength == 16 => typeof(Guid),
                "RAW" or "LONG RAW" or "BLOB" or "BFILE" => typeof(byte[]),
                _ => typeof(string),
            };
        }
```

Update the call-site in `OracleTableBuilder.GetColumnsAsync` to pass the length:

```csharp
                    column.SetType(GetManagedType(dataType, precision, scale, dataLength));
```

and in `OracleDatabaseBuilder.GetTableAsync` (already shown with the 4-argument call in Task 4).

- [ ] **Step 10.5: Run the tests to verify they pass**

Run: `dotnet test Tests/Dotmim.Sync.Tests/Dotmim.Sync.Tests.csproj -f net8.0 --filter "FullyQualifiedName~OracleMiscTests" --nologo`
Expected: all PASS.

- [ ] **Step 10.6: Commit**

```powershell
git add Projects/Dotmim.Sync.Oracle/InternalsVisibility.cs Projects/Dotmim.Sync.Oracle/Builders/OracleTableBuilder.cs Projects/Dotmim.Sync.Oracle/Builders/OracleDatabaseBuilder.cs Tests/Dotmim.Sync.Tests/UnitTests/Oracle/OracleMiscTests.cs
git commit -m "feat(oracle): map RAW(16) to Guid when reading existing schemas"
```

---

### Task 11: csproj parity + warning-clean build (packaging / minors)

**Files:**
- Rewrite: `Projects/Dotmim.Sync.Oracle/Dotmim.Sync.Oracle.csproj`

- [ ] **Step 11.1: Replace the csproj content** (modeled on `Dotmim.Sync.MySql.csproj`; version, tags, license, repo URL all inherit from `Directory.Build.props` — the current hardcoded `Version 1.0.0` would fight the repo's 1.3.0):

```xml
<Project Sdk="Microsoft.NET.Sdk">

	<PropertyGroup>
		<!-- netstandard2.1 is the floor of Oracle.ManagedDataAccess.Core 3.x (not 2.0) -->
		<TargetFrameworks>netstandard2.1;$(TargetFrameworkNet6);$(TargetFrameworkNet8)</TargetFrameworks>

		<Authors>Sébastien Pertus, Waseem Ahmad Mughal</Authors>
		<Company>Microsoft</Company>
		<Title>Dotmim.Sync.Oracle</Title>
		<Summary>Oracle Sync Provider. Client or Server provider .Net Standard 2.1</Summary>
		<Description>Oracle Sync Provider. Manage a sync process beetween two relational databases provider. This provider works with SQL Server and can be used as Client or Server provider .Net Standard 2.1</Description>
		<RepositoryType>git</RepositoryType>
		<PackageIcon>packageIcon.png</PackageIcon>
	</PropertyGroup>
	<PropertyGroup>
		<NoWarn>$(NoWarn)SA0001;SA1202;CA1308;CA1305;CA1822;CA1834;SA1600;IDE0017;CA2249;CA1866;CA2100;CA1307;CA1310;</NoWarn>
	</PropertyGroup>

	<ItemGroup>
		<None Include="..\..\docs\assets\packageIcon.png">
			<Pack>True</Pack>
			<PackagePath></PackagePath>
		</None>
	</ItemGroup>

	<ItemGroup>
		<ProjectReference Include="..\Dotmim.Sync.Core\Dotmim.Sync.Core.csproj" />
		<PackageReference Include="Microsoft.SourceLink.GitHub" Version="8.0.0" PrivateAssets="All" />
	</ItemGroup>

	<ItemGroup>
		<PackageReference Include="Oracle.ManagedDataAccess.Core" Version="3.21.130" />
	</ItemGroup>

</Project>
```

Notes: drops `net7.0` (no other provider targets it), drops `Version`, `LangVersion 10` (inherits 12), `Nullable disable` (default), `favicon.ico`/`database.png` packing.

- [ ] **Step 11.2: Regenerate the lock file**

Run: `dotnet restore Projects/Dotmim.Sync.Oracle/Dotmim.Sync.Oracle.csproj --force-evaluate`
Expected: `packages.lock.json` updated (net7.0 section removed, SourceLink added).

- [ ] **Step 11.3: Build all TFMs and check warnings**

Run: `dotnet build Projects/Dotmim.Sync.Oracle/Dotmim.Sync.Oracle.csproj --nologo`
Expected: 0 errors. Then count warnings: if any remain (the Task 4 rewrite removes the CA1849 cluster; the NoWarn list matches MySQL's), fix them in code — do **not** widen the NoWarn list beyond MySQL's.

- [ ] **Step 11.4: Run the full Oracle unit test suite**

Run: `dotnet test Tests/Dotmim.Sync.Tests/Dotmim.Sync.Tests.csproj -f net8.0 --filter "FullyQualifiedName~UnitTests.Oracle" --nologo`
Expected: all PASS.

- [ ] **Step 11.5: Commit**

```powershell
git add Projects/Dotmim.Sync.Oracle/Dotmim.Sync.Oracle.csproj Projects/Dotmim.Sync.Oracle/packages.lock.json
git commit -m "chore(oracle): align csproj with provider conventions (central version, SourceLink, packageIcon)"
```

---

### Task 12: Register filter/HTTP test classes + commit the EF wiring (fixes I2, I3)

**Files:**
- Modify: `Tests/Dotmim.Sync.Tests/Setup.cs`
- Commit as-is (already modified in working tree): `Tests/Dotmim.Sync.Tests/Dotmim.Sync.Tests.csproj`, `Tests/Dotmim.Sync.Tests/Models/AdventureWorksContext.cs`, `Tests/Dotmim.Sync.Tests/Misc/DatabaseTest.cs`, `Samples/Dotmim.Sync.SampleConsole/Dotmim.Sync.SampleConsole.csproj`, lock files

The working tree already contains the EF wiring (Oracle.EntityFrameworkCore 6.21.130/8.23.26200, `UseOracle` branch in `AdventureWorksContext.OnConfiguring`, admin pre-creation of the Oracle user in `DatabaseTest.cs`). This task registers the two missing test classes and commits everything together.

- [ ] **Step 12.1: Add the two test classes to `Tests/Dotmim.Sync.Tests/Setup.cs`**, directly after the existing `OracleConflictTests` class (~line 590), following the existing Oracle classes' single-client pattern:

```csharp
    public class OracleTcpFilterTests : TcpFilterTests
    {
        public OracleTcpFilterTests(ITestOutputHelper output, DatabaseServerFixture fixture)
            : base(output, fixture)
        {
        }

        public override ProviderType ServerProviderType => ProviderType.Oracle;

        private string oracleClientRandomDatabaseName = HelperDatabase.GetRandomName("tcpf_ora_");

        public override IEnumerable<CoreProvider> GetClientProviders()
        {
            yield return HelperDatabase.GetSyncProvider(ProviderType.Oracle, this.oracleClientRandomDatabaseName, false);
        }
    }

    public class OracleHttpTests : HttpTests
    {
        public OracleHttpTests(ITestOutputHelper output, DatabaseServerFixture fixture)
            : base(output, fixture)
        {
        }

        public override ProviderType ServerProviderType => ProviderType.Oracle;

        private string oracleClientRandomDatabaseName = HelperDatabase.GetRandomName("http_ora_");

        public override IEnumerable<CoreProvider> GetClientProviders()
        {
            yield return HelperDatabase.GetSyncProvider(ProviderType.Oracle, this.oracleClientRandomDatabaseName, false);
        }
    }
```

- [ ] **Step 12.2: Build the test project (compile check only — running these classes needs a live Oracle)**

Run: `dotnet build Tests/Dotmim.Sync.Tests/Dotmim.Sync.Tests.csproj -f net8.0 --nologo`
Expected: 0 errors.

- [ ] **Step 12.3: Commit (includes the pre-existing uncommitted EF wiring)**

```powershell
git add Tests/Dotmim.Sync.Tests Samples/Dotmim.Sync.SampleConsole/Dotmim.Sync.SampleConsole.csproj Samples/Dotmim.Sync.SampleConsole/packages.lock.json
git commit -m "test(oracle): EF Core Oracle test model wiring; register filter and http test classes (I2, I3)"
```

---

### Task 13: CI + packaging pipelines (fixes I4)

**Files:**
- Modify: `pipelines/azure-pipelines-template.yml`
- Create: `azure-pipelines-oracle.yml`
- Modify: `azure-pipelines-nuget.yml`

- [ ] **Step 13.1: Add the Oracle container step to the template**, after the existing postgres step (`pipelines/azure-pipelines-template.yml:70-72`):

```yaml
      - script: |
          docker run --name oracle -e ORACLE_PASSWORD=Password12! -p 1521:1521 -d gvenzl/oracle-free:23-slim
          timeout 600 bash -c 'until docker logs oracle 2>&1 | grep -q "DATABASE IS READY TO USE"; do sleep 5; done'
        displayName: "Run Oracle Database Free on Linux container"
        condition: eq('${{ parameters.docker }}', 'oracle')
```

(`gvenzl/oracle-free` exposes service `FREEPDB1` with the `SYSTEM` password from `ORACLE_PASSWORD` — exactly what `Tests/Dotmim.Sync.Tests/appsettings.json:8-9` expects: `localhost:1521/FREEPDB1`, `SYSTEM/Password12!`. The readiness wait is required: Oracle takes 1–3 minutes to open.)

- [ ] **Step 13.2: Create `azure-pipelines-oracle.yml`** (root, next to `azure-pipelines-mysql.yml`):

```yaml
jobs:

  - template: pipelines/azure-pipelines-template.yml
    parameters:
      displayName: "Oracle Tcp .net 8.0"
      dotnetfx: "net8.0"
      filter: "Dotmim.Sync.Tests.OracleTcpTests"
      docker: "oracle"

  - template: pipelines/azure-pipelines-template.yml
    parameters:
      displayName: "Oracle Conflicts .net 8.0"
      dotnetfx: "net8.0"
      filter: "Dotmim.Sync.Tests.OracleConflictTests"
      docker: "oracle"

  - template: pipelines/azure-pipelines-template.yml
    parameters:
      displayName: "Oracle TCP Filter Tests .net 8.0"
      dotnetfx: "net8.0"
      filter: "Dotmim.Sync.Tests.OracleTcpFilterTests"
      docker: "oracle"

  - template: pipelines/azure-pipelines-template.yml
    parameters:
      displayName: "Oracle Http Tests .net 8.0"
      dotnetfx: "net8.0"
      filter: "Dotmim.Sync.Tests.OracleHttpTests"
      docker: "oracle"
```

(Note: `azure-pipelines-mysql.yml` references the template as `azure-pipelines-template.yml` because Azure DevOps resolves template paths relative to the *referencing* file's repo path — check how that pipeline is registered; if the mysql yml at repo root uses the bare name and works, use the same relative form `azure-pipelines-template.yml` here instead of `pipelines/...`. Mirror whatever `azure-pipelines-mysql.yml` line 3 does.)

- [ ] **Step 13.3: Add the Oracle pack block to `azure-pipelines-nuget.yml`**, after the MariaDB block (line 78). The file has one job (`Beta`) in the first 80 lines — check below line 80 for a `Release` job and add the same block (without `--version-suffix` if the Release job omits it) there too:

```yaml
      - script: |
          dotnet build Projects/Dotmim.Sync.Oracle/Dotmim.Sync.Oracle.csproj -c $(buildConfiguration) --version-suffix $(buildBetaId) --no-restore
          dotnet pack Projects/Dotmim.Sync.Oracle/Dotmim.Sync.Oracle.csproj -c $(buildConfiguration) -o $(Build.ArtifactStagingDirectory)/Dotmim.Sync.Oracle --version-suffix $(buildBetaId)
        displayName: "beta nuget Dotmim.Sync.Oracle"
```

- [ ] **Step 13.4: Validate YAML locally** (syntax only): `Get-Content azure-pipelines-oracle.yml | ConvertFrom-Yaml` requires the `powershell-yaml` module — if unavailable, eyeball indentation against `azure-pipelines-mysql.yml` (2-space, template list items).

- [ ] **Step 13.5: Commit**

```powershell
git add pipelines/azure-pipelines-template.yml azure-pipelines-oracle.yml azure-pipelines-nuget.yml
git commit -m "ci(oracle): add Oracle Free container job, test matrix pipeline, and nuget pack (I4)"
```

---

### Task 14: Sample (fixes I5)

**Files:**
- Create: `Samples/HelloOracleSync/HelloOracleSync.csproj`
- Create: `Samples/HelloOracleSync/Program.cs`

- [ ] **Step 14.1: Create `Samples/HelloOracleSync/HelloOracleSync.csproj`:**

```xml
<Project Sdk="Microsoft.NET.Sdk">

	<PropertyGroup>
		<OutputType>Exe</OutputType>
		<TargetFramework>net8.0</TargetFramework>
		<RestorePackagesWithLockFile>false</RestorePackagesWithLockFile>
	</PropertyGroup>

	<ItemGroup>
		<ProjectReference Include="..\..\Projects\Dotmim.Sync.Core\Dotmim.Sync.Core.csproj" />
		<ProjectReference Include="..\..\Projects\Dotmim.Sync.SqlServer\Dotmim.Sync.SqlServer.csproj" />
		<ProjectReference Include="..\..\Projects\Dotmim.Sync.Oracle\Dotmim.Sync.Oracle.csproj" />
	</ItemGroup>

</Project>
```

- [ ] **Step 14.2: Create `Samples/HelloOracleSync/Program.cs`:**

```csharp
using Dotmim.Sync;
using Dotmim.Sync.Oracle;
using Dotmim.Sync.SqlServer;
using System;
using System.Threading.Tasks;

namespace HelloOracleSync
{
    internal class Program
    {
        // Server: SQL Server AdventureWorks. Client: an Oracle user/schema.
        // Oracle note: a "database" is a user/schema — create it first, e.g.:
        //   CREATE USER DMS_SAMPLE IDENTIFIED BY "Password12!";
        //   GRANT CONNECT, RESOURCE TO DMS_SAMPLE;
        //   ALTER USER DMS_SAMPLE QUOTA UNLIMITED ON USERS;
        private const string ServerConnectionString =
            "Data Source=(localdb)\\mssqllocaldb;Initial Catalog=AdventureWorks;Integrated Security=true;";

        private const string ClientConnectionString =
            "Data Source=localhost:1521/FREEPDB1;User Id=DMS_SAMPLE;Password=Password12!;";

        private static async Task Main()
        {
            var serverProvider = new SqlSyncProvider(ServerConnectionString);
            var clientProvider = new OracleSyncProvider(ClientConnectionString);

            var setup = new SyncSetup("ProductCategory", "ProductModel", "Product");
            var agent = new SyncAgent(clientProvider, serverProvider);

            do
            {
                var result = await agent.SynchronizeAsync(setup);
                Console.WriteLine(result);
                Console.WriteLine("Sync ended. Press a key to sync again, or Escape to exit.");
            }
            while (Console.ReadKey().Key != ConsoleKey.Escape);
        }
    }
}
```

- [ ] **Step 14.3: Build the sample**

Run: `dotnet build Samples/HelloOracleSync/HelloOracleSync.csproj --nologo`
Expected: 0 errors. (If the repo's `Directory.Build.props` forces `RestorePackagesWithLockFile`, run `dotnet restore Samples/HelloOracleSync` once and commit the generated lock file too.)

- [ ] **Step 14.4: Commit**

```powershell
git add Samples/HelloOracleSync
git commit -m "docs(oracle): add HelloOracleSync sample (I5)"
```

---

### Task 15: Provider documentation + close out the verification report (fixes I6)

**Files:**
- Create: `docs/Oracle.md`
- Modify: `docs/Oracle-Provider-Verification.md`

- [ ] **Step 15.1: Create `docs/Oracle.md`:**

```markdown
# Oracle Provider

`Dotmim.Sync.Oracle` brings Oracle Database support to Dotmim.Sync, usable as **client or
server** provider.

## Requirements

- **Oracle Database 19c or later** (the provider relies on 128-byte identifiers (12.2+) and
  `DBMS_SQL.RETURN_RESULT` implicit result sets (12.1+); 19c is the tested floor).
- `Oracle.ManagedDataAccess.Core` 3.x (netstandard2.1, net6.0, net8.0).

## Design (how it differs from the SQL Server provider)

- **No stored procedures.** Oracle cannot stream a result set out of a stored procedure the
  way the framework drives commands, so — like the MySQL/SQLite/PostgreSQL providers — every
  command is inline `CommandType.Text`. Row apply (update/delete) runs as an anonymous PL/SQL
  block that reports the affected row count through the `:sync_row_count` output bind.
  Provisioning with the `StoredProcedures` flag is a clean no-op.
- **Change tracking** uses per-table tracking tables (`<table>_tracking`) maintained by
  `AFTER INSERT/UPDATE/DELETE … FOR EACH ROW` triggers, with a shared UTC epoch clock
  (~100µs resolution) used by both the triggers and `GetLocalTimestamp`.
- **Scope tables**: `scope_info` (keyed by `sync_scope_name`) and `scope_info_client`
  (keyed by scope id + name + hash); scope ids are stored as `RAW(16)`.

## Type mapping highlights

| .NET / DbType | Oracle |
|---|---|
| `Guid` | `RAW(16)` (and `RAW(16)` reads back as `Guid`) |
| `bool` | `NUMBER(1)` (0/1) |
| `byte/short/int/long` | `NUMBER(3/5/10/19)` |
| `decimal` | `NUMBER(p,s)` |
| `float`/`double` | `BINARY_FLOAT` / `BINARY_DOUBLE` |
| `DateTime` | `TIMESTAMP` (`DATE` for `DbType.Date`) |
| `DateTimeOffset` | `TIMESTAMP WITH TIME ZONE` |
| `string` | `VARCHAR2(n)` (≤4000) else `CLOB` |
| `byte[]` | `RAW(n)` (≤2000) else `BLOB` |

## Identifier casing contract

The provider creates and looks up all objects **quoted, case-preserved**. If your existing
Oracle tables were created unquoted (i.e. stored uppercase), reference them with their
uppercase names in `SyncSetup` (e.g. `new SyncSetup("PRODUCT")`).

Generated identifiers (tracking table, triggers, index, PK constraints) must fit Oracle's
128-byte limit; the provider fails fast with a clear message otherwise.

## "Database" = user/schema

Oracle has no `CREATE DATABASE` equivalent at the provider level: a *database* is a
user/schema. Create the user up front (an admin operation), then point the connection string
at it. See `Samples/HelloOracleSync`.

## Local test database

```bash
docker run --name oracle -e ORACLE_PASSWORD=Password12! -p 1521:1521 -d gvenzl/oracle-free:23-slim
```

Connection strings used by the test suite live in `Tests/Dotmim.Sync.Tests/appsettings.json`
(`OracleConnection`, `OracleAdminConnection`).

## Known limitations

- No bulk/TVP path: rows are applied one by one (`UseBulkOperations` is forced off).
- `UpdateMetadata` / `SelectMetadata` are not implemented (same as the MySQL provider).
- Snapshot/initialization performance depends on the tracking-table `timestamp` index that
  the provider creates with the tracking table.
```

- [ ] **Step 15.2: Update `docs/Oracle-Provider-Verification.md`** — add a status line under the title:

```markdown
> **Status update (fix pass):** all items below were addressed by the plan in
> `docs/plans/2026-06-10-oracle-provider-fixes.md` — C1–C4, M1–M6, minors, I1–I6 fixed;
> live-database validation tracked in that plan's Task 16.
```

- [ ] **Step 15.3: Commit**

```powershell
git add docs/Oracle.md docs/Oracle-Provider-Verification.md
git commit -m "docs(oracle): provider documentation; mark verification report as addressed (I6)"
```

---

### Task 16: Live-database validation (requires Oracle; gates "done")

**Files:** none (execution + recorded results in the progress tracker)

These behaviors cannot be proven by unit tests. Run once a local Oracle is available:

- [ ] **Step 16.1: Start Oracle locally**

```powershell
docker run --name oracle -e ORACLE_PASSWORD=Password12! -p 1521:1521 -d gvenzl/oracle-free:23-slim
# wait for "DATABASE IS READY TO USE" in: docker logs -f oracle
```

- [ ] **Step 16.2: Run the Oracle TCP matrix**

```powershell
dotnet test Tests/Dotmim.Sync.Tests/Dotmim.Sync.Tests.csproj -f net8.0 --filter "Dotmim.Sync.Tests.OracleTcpTests" --logger "console;verbosity=normal"
```

Watch specifically for (in failure triage order):
1. **`DBMS_SQL.RETURN_RESULT` read-back** — scope save returning the row through `ExecuteReader` (ODP.NET implicit result sets). Fallback if broken: split save into ExecuteNonQuery + the framework re-reads via `GetScopeInfoCommand`? No — the contract requires the result set; instead bind an explicit `SYS_REFCURSOR` output parameter named outside the framework set and remove `DBMS_SQL.RETURN_RESULT`. (Only pursue if implicit result sets fail; they are supported in ODP.NET Core 3.x against 12.2+.)
2. **`PrepareAsync` on anonymous PL/SQL blocks** — `BaseOrchestrator` prepares commands; ODP.NET documents `Prepare()` as a no-op, but confirm no exception path.
3. **ORA-01036 absence** — confirms Task 2's stripping covers every command type the orchestrator drives (SelectRow and DeleteMetadata are the known offenders).
4. **Guid ↔ RAW(16) round-trip** on PKs, scope ids, and `sync_update_scope_id` projection.
5. **Clock sanity** — `GetLocalTimestamp` ≥ all tracking timestamps; no row "from the future" after rapid consecutive syncs.
6. **`ENABLE NOVALIDATE`** after applying FK-ordered deletes (conflict tests cover this).

- [ ] **Step 16.3: Run conflicts, filters, http**

```powershell
dotnet test Tests/Dotmim.Sync.Tests/Dotmim.Sync.Tests.csproj -f net8.0 --filter "Dotmim.Sync.Tests.OracleConflictTests"
dotnet test Tests/Dotmim.Sync.Tests/Dotmim.Sync.Tests.csproj -f net8.0 --filter "Dotmim.Sync.Tests.OracleTcpFilterTests"
dotnet test Tests/Dotmim.Sync.Tests/Dotmim.Sync.Tests.csproj -f net8.0 --filter "Dotmim.Sync.Tests.OracleHttpTests"
```

- [ ] **Step 16.4: Record outcomes in `docs/plans/oracle-provider-fixes-progress.md`** (pass/fail per class, defects found, fixes applied).

- [ ] **Step 16.5: Commit any resulting fixes** with messages referencing the failing test, e.g. `fix(oracle): <symptom> found by OracleTcpTests.<test>`.

---

## Self-review checklist (done at authoring time)

- **Spec coverage:** C1→Task 1, C2→Task 1, C3→Task 2, C4→Task 3, M1→Task 4, M2→Task 5, M3→Task 8, M4→Task 6, M5→Task 7, M6→Task 8, minors (clock→Task 9, RAW/Guid→Task 10, Task.Run/dead code/`GetLocalTimestampAsync`→Tasks 1+4, csproj/warnings→Task 11), I1 (scope rewrite)→Task 1, I2→Task 12, I3→Task 12, I4→Task 13, I5→Task 14, I6→Task 15, live validation→Task 16. The optional I7 (UpdateMetadata/SelectMetadata) is intentionally **not** implemented — MySQL parity (documented in Task 15's doc).
- **Type consistency:** `GetManagedType(string, byte, byte, int)` introduced in Task 10 and consumed in Task 4 (cross-referenced both directions with an out-of-order note); `CreateFilterCustomJoins` defined Task 3, tested Task 3; `EnsureIdentifierLength` defined and used only in Task 8.
- **API assumptions to verify at execution time** (flagged inline): `SyncFilterJoin` construction (Task 3 note), Azure template relative path (Task 13 note), lock-file behavior for the new sample (Task 14 note).
