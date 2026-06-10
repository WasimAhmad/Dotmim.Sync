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

            // ODP.NET rejects DbType.Guid; map it to OracleDbType.Raw (16 bytes = RAW(16)).
            if (dbType == DbType.Guid && parameter is OracleParameter guidParam)
            {
                guidParam.OracleDbType = OracleDbType.Raw;
                guidParam.Size = 16;
            }
            else
            {
                parameter.DbType = dbType;

                if (size > 0)
                    parameter.Size = size;

                // JSON payloads (schema/setup/parameters/errors/properties) can exceed the
                // VARCHAR2 bind limit; bind them as CLOB.
                if (isClob && parameter is OracleParameter oracleParameter)
                    oracleParameter.OracleDbType = OracleDbType.Clob;
            }

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
