# Oracle Provider

`Dotmim.Sync.Oracle` brings Oracle Database support to Dotmim.Sync, usable as **client or
server** provider.

## Requirements

- **Oracle Database 19c or later**, with `COMPATIBLE >= 12.2.0.0` (the provider relies on
  128-byte identifiers and `DBMS_SQL.RETURN_RESULT` implicit result sets).
- `Oracle.ManagedDataAccess.Core` 3.x (netstandard2.1, net6.0, net8.0).

## Design (how it differs from the SQL Server provider)

- **No stored procedures.** Oracle cannot stream a result set out of a stored procedure the
  way the framework drives commands, so — like the MySQL/SQLite/PostgreSQL providers — every
  command is inline `CommandType.Text`. Row apply (update/delete) runs as an anonymous PL/SQL
  block that reports the affected row count through the `:sync_row_count` output bind.
  Provisioning with the `StoredProcedures` flag is a clean no-op.
- **Change tracking** uses per-table tracking tables (`<table>_tracking`, indexed on the
  `timestamp` column) maintained by `AFTER INSERT/UPDATE/DELETE … FOR EACH ROW` triggers,
  with a shared UTC epoch clock (~100µs resolution) used by both the triggers and
  `GetLocalTimestamp`.
- **Scope tables**: `scope_info` (keyed by `sync_scope_name`) and `scope_info_client`
  (keyed by scope id + name + hash); scope ids are stored as `RAW(16)`.
- **Parameters are pre-created by the provider** with ODP.NET-safe types (ODP.NET rejects
  `DbType.Guid`; output parameters must be typed via `DbType` to read back as .NET types).

## Type mapping highlights

| .NET / DbType | Oracle |
|---|---|
| `Guid` | `RAW(16)` (bound as `Guid.ToByteArray()`; `RAW(16)` reads back as `Guid`) |
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

## Known limitations and behavior notes

- **No bulk/TVP path**: rows are applied one by one (`UseBulkOperations` is forced off).
- **`UpdateMetadata` / `SelectMetadata` are not implemented** (same as the MySQL provider).
- **Foreign keys are re-enabled `NOVALIDATE`** after a sync (parity with SQL Server's
  `CHECK CONSTRAINT ALL`). Tools that report "not trusted" constraints will flag them; run
  `ALTER TABLE … ENABLE VALIDATE CONSTRAINT …` offline if validated state is required.
- **Join-based filters do not propagate deletes** for filtered tables (the custom INNER JOIN
  eliminates tombstone rows) — identical behavior to the MySQL and SQL Server providers.
- **`SELECT DISTINCT` is emitted for filtered tables**; Oracle raises ORA-00932 if such a
  table contains CLOB columns. Avoid CLOB columns on join-filtered tables.
- **Interceptors** that cast a *scope command*'s `args.Command` to `OracleCommand` get `null`
  (scope commands are wrapped to promote >4K text parameters to CLOB at execute time);
  sync-adapter table commands are unaffected.
- **Partial provisioning recovery**: if provisioning fails between tracking-table and index
  creation, re-provision with `overwrite: true` to recover the index.
- **`RenameTableAsync` renames within the owning schema** (Oracle `RENAME TO` is
  schema-local); the new-schema argument is ignored.
- **Cross-schema filter joins** are emitted schema-unqualified (parity with the other
  providers); use synonyms or same-schema objects.
- **EF Core on Oracle 23ai+**: pin `UseOracleSQLCompatibility` to a 19-level so `bool` maps
  to `NUMBER(1)` (ODP.NET 3.x does not support the native BOOLEAN type), map large `byte[]`
  properties to `BLOB` explicitly, and note that Oracle identity sequences do **not** advance
  past EF-seeded literal ids — restart them (`START WITH LIMIT VALUE`) after seeding.
- **Constraint toggling cost**: `DisableConstraints`/`EnableConstraints` query
  `USER_CONSTRAINTS` per table per sync, which is dictionary-heavy on Oracle. For
  FK-ordered setups consider `Options.DisableConstraintsOnApplyChanges = false`.
