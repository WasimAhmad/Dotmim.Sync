using Dotmim.Sync.Builders;
using Oracle.ManagedDataAccess.Client;
using System;
using System.Collections.Generic;
using System.Data;
using System.Data.Common;
using System.Globalization;
using System.Text;
using System.Threading.Tasks;
using Dotmim.Sync.DatabaseStringParsers;

namespace Dotmim.Sync.Oracle.Builders
{
    /// <summary>
    /// Oracle implementation for the DbScopeBuilder.
    /// Handles scope info table creation and maintenance for Oracle database.
    /// </summary>
    public class OracleScopeBuilder : DbScopeBuilder
    {
        private readonly string tableName;
        private readonly string clientTableName;
        private readonly DbTableNames scopeInfoTableNames;
        private readonly DbTableNames scopeInfoClientTableNames;
        private const string leftQuote = "\"";
        private const string rightQuote = "\"";

        /// <summary>
        /// Initializes a new instance of the <see cref="OracleScopeBuilder"/> class.
        /// </summary>
        /// <param name="scopeInfoTableName">The name of the scope info table.</param>
        public OracleScopeBuilder(string scopeInfoTableName) : base()
        {
            // Validate table names to prevent SQL injection
            if (string.IsNullOrEmpty(scopeInfoTableName) || !System.Text.RegularExpressions.Regex.IsMatch(scopeInfoTableName, @"^[A-Za-z0-9_]+$"))
                throw new ArgumentException("Invalid scope info table name format", nameof(scopeInfoTableName));
                
            this.tableName = scopeInfoTableName;
            this.clientTableName = $"{scopeInfoTableName}_client";

            // Create table names for scope_info
            var parser = new ObjectParser(this.tableName, leftQuote[0], rightQuote[0]);
            this.scopeInfoTableNames = new DbTableNames(
                leftQuote[0], rightQuote[0],
                this.tableName,
                this.tableName,
                parser.NormalizedShortName,
                $"{leftQuote}{this.tableName}{rightQuote}",
                parser.QuotedShortName,
                string.Empty);
                
            // Create table names for scope_info_client
            var clientParser = new ObjectParser(this.clientTableName, leftQuote[0], rightQuote[0]);
            this.scopeInfoClientTableNames = new DbTableNames(
                leftQuote[0], rightQuote[0],
                this.clientTableName,
                this.clientTableName,
                clientParser.NormalizedShortName,
                $"{leftQuote}{this.clientTableName}{rightQuote}",
                clientParser.QuotedShortName,
                string.Empty);
        }

        /// <summary>
        /// Gets the table names for the scope info table.
        /// </summary>
        /// <returns>The table names for the scope info table.</returns>
        public override DbTableNames GetParsedScopeInfoTableNames() => this.scopeInfoTableNames;

        /// <summary>
        /// Gets the table names for the scope info client table.
        /// </summary>
        /// <returns>The table names for the scope info client table.</returns>
        public override DbTableNames GetParsedScopeInfoClientTableNames() => this.scopeInfoClientTableNames;

        /// <summary>
        /// Gets the local timestamp from the Oracle database.
        /// </summary>
        /// <param name="connection">The database connection.</param>
        /// <param name="transaction">Optional transaction to use.</param>
        /// <returns>A tuple containing the system change number as a string and a validity flag.</returns>
        public static async Task<(string Hash, bool IsValid)> GetLocalTimestampAsync(DbConnection connection, DbTransaction transaction = null)
        {
            var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = "SELECT DBMS_FLASHBACK.GET_SYSTEM_CHANGE_NUMBER FROM DUAL";

            try
            {
                bool alreadyOpened = connection.State == ConnectionState.Open;

                if (!alreadyOpened)
                    await connection.OpenAsync().ConfigureAwait(false);

                var result = await command.ExecuteScalarAsync().ConfigureAwait(false);

                if (!alreadyOpened)
                    await connection.CloseAsync().ConfigureAwait(false);

                if (result != null && result != DBNull.Value)
                {
                    var scn = Convert.ToInt64(result);
                    return (scn.ToString(), true);
                }
                else
                {
                    return (string.Empty, false);
                }
            }
            catch (Exception ex)
            {
                throw ex;
            }
        }

        /// <summary>
        /// Determines if the scope info table needs to be created.
        /// </summary>
        /// <param name="connection">The database connection.</param>
        /// <param name="transaction">Optional transaction to use.</param>
        /// <returns>True if the table needs to be created; otherwise, false.</returns>
        public async Task<bool> NeedToCreateScopeInfoTableAsync(DbConnection connection, DbTransaction transaction = null)
        {
            var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = $@"
                SELECT COUNT(*) 
                FROM USER_TABLES 
                WHERE TABLE_NAME = :tableName";

            var parameter = command.CreateParameter();
            parameter.ParameterName = ":tableName";
            parameter.Value = this.tableName.ToUpper(CultureInfo.InvariantCulture); // Oracle stores identifiers in upper case by default
            command.Parameters.Add(parameter);

            try
            {
                bool alreadyOpened = connection.State == ConnectionState.Open;

                if (!alreadyOpened)
                    await connection.OpenAsync().ConfigureAwait(false);

                var result = await command.ExecuteScalarAsync().ConfigureAwait(false);

                if (!alreadyOpened)
                    await connection.CloseAsync().ConfigureAwait(false);

                int count = Convert.ToInt32(result);
                return count == 0; // Need to create only if it doesn't exist
            }
            catch (Exception ex)
            {
                throw ex;
            }
        }

        /// <summary>
        /// Creates the SQL script for the scope info table.
        /// </summary>
        /// <param name="connection">The database connection.</param>
        /// <param name="transaction">Optional transaction to use.</param>
        /// <returns>The SQL script as a string.</returns>
        public string CreateScopeInfoTableScriptAsync(DbConnection connection, DbTransaction transaction = null)
        {
            var stringBuilder = new StringBuilder();

            // Create the scope info table
            stringBuilder.AppendLine($"CREATE TABLE \"{this.tableName}\" (");
            stringBuilder.AppendLine($"  \"sync_scope_id\" VARCHAR2(36) NOT NULL,");
            stringBuilder.AppendLine($"  \"sync_scope_name\" VARCHAR2(100) NOT NULL,");
            stringBuilder.AppendLine($"  \"sync_scope_schema\" CLOB NULL,");
            stringBuilder.AppendLine($"  \"sync_scope_setup\" CLOB NULL,");
            stringBuilder.AppendLine($"  \"sync_scope_version\" VARCHAR2(10) NULL,");
            stringBuilder.AppendLine($"  \"sync_scope_last_server_sync_timestamp\" NUMBER NULL,");
            stringBuilder.AppendLine($"  \"sync_scope_last_sync_timestamp\" NUMBER NULL,");
            stringBuilder.AppendLine($"  \"sync_scope_last_sync_duration\" NUMBER NULL,");
            stringBuilder.AppendLine($"  \"sync_scope_last_sync\" TIMESTAMP NULL,");
            stringBuilder.AppendLine($"  CONSTRAINT \"PK_{this.tableName}\" PRIMARY KEY (\"sync_scope_id\", \"sync_scope_name\")");
            stringBuilder.AppendLine($")");

            return stringBuilder.ToString();
        }

        /// <summary>
        /// Gets a command to delete a scope info client record.
        /// </summary>
        /// <param name="connection">The database connection.</param>
        /// <param name="transaction">Optional transaction to use.</param>
        /// <returns>A DbCommand object ready to be executed.</returns>
        public override DbCommand GetDeleteScopeInfoClientCommand(DbConnection connection, DbTransaction transaction)
        {
            // Validate table name to prevent SQL injection
            if (string.IsNullOrEmpty(this.clientTableName) || !System.Text.RegularExpressions.Regex.IsMatch(this.clientTableName, @"^[A-Za-z0-9_]+$"))
                throw new ArgumentException("Invalid client table name format");

            var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = $@"
                DELETE FROM ""{this.clientTableName}""
                WHERE ""sync_scope_name"" = :scopeName AND ""sync_scope_id"" = :scopeId AND ""sync_scope_client_id"" = :clientId";

            var scopeNameParam = command.CreateParameter();
            scopeNameParam.ParameterName = ":scopeName";
            scopeNameParam.DbType = DbType.String;
            command.Parameters.Add(scopeNameParam);

            var scopeIdParam = command.CreateParameter();
            scopeIdParam.ParameterName = ":scopeId";
            scopeIdParam.DbType = DbType.Guid;
            command.Parameters.Add(scopeIdParam);

            var clientIdParam = command.CreateParameter();
            clientIdParam.ParameterName = ":clientId";
            clientIdParam.DbType = DbType.Guid;
            command.Parameters.Add(clientIdParam);

            return command;
        }

        /// <summary>
        /// Gets a command to insert a scope info record.
        /// </summary>
        /// <param name="connection">The database connection.</param>
        /// <param name="transaction">Optional transaction to use.</param>
        /// <returns>A DbCommand object ready to be executed.</returns>
        public override DbCommand GetInsertScopeInfoCommand(DbConnection connection, DbTransaction transaction)
        {
            var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = $@"
                INSERT INTO ""{this.tableName}"" 
                (""sync_scope_id"", ""sync_scope_name"", ""sync_scope_schema"", ""sync_scope_setup"", ""sync_scope_version"", 
                 ""sync_scope_last_server_sync_timestamp"", ""sync_scope_last_sync_timestamp"", ""sync_scope_last_sync_duration"", ""sync_scope_last_sync"")
                VALUES 
                (:scopeId, :scopeName, :schema, :setup, :version, 
                 :lastServerSyncTimestamp, :lastSyncTimestamp, :lastSyncDuration, :lastSync)";

            AddScopeInfoParameters(command);

            return command;
        }

        /// <summary>
        /// Gets a command to insert a scope info client record.
        /// </summary>
        /// <param name="connection">The database connection.</param>
        /// <param name="transaction">Optional transaction to use.</param>
        /// <returns>A DbCommand object ready to be executed.</returns>
        public override DbCommand GetInsertScopeInfoClientCommand(DbConnection connection, DbTransaction transaction)
        {
            var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = $@"
                INSERT INTO ""{this.clientTableName}"" 
                (""sync_scope_id"", ""sync_scope_name"", ""sync_scope_client_id"", ""sync_scope_client_name"", ""sync_scope_parameters"", 
                 ""sync_scope_filters"", ""sync_scope_properties"", ""sync_scope_last_client_sync_timestamp"", ""sync_scope_last_server_sync_timestamp"", 
                 ""sync_scope_last_sync_timestamp"", ""sync_scope_last_sync_duration"", ""sync_scope_last_sync"")
                VALUES 
                (:scopeId, :scopeName, :clientId, :clientName, :parameters, 
                 :filters, :properties, :lastClientSyncTimestamp, :lastServerSyncTimestamp, 
                 :lastSyncTimestamp, :lastSyncDuration, :lastSync)";

            AddScopeInfoClientParameters(command);

            return command;
        }

        /// <summary>
        /// Helper method to create parameters for the ScopeInfo commands
        /// </summary>
        private static void AddScopeInfoParameters(DbCommand command)
        {
            var scopeIdParam = command.CreateParameter();
            scopeIdParam.ParameterName = ":scopeId";
            scopeIdParam.DbType = DbType.Guid;
            command.Parameters.Add(scopeIdParam);

            var scopeNameParam = command.CreateParameter();
            scopeNameParam.ParameterName = ":scopeName";
            scopeNameParam.DbType = DbType.String;
            command.Parameters.Add(scopeNameParam);

            var schemaParam = command.CreateParameter();
            schemaParam.ParameterName = ":schema";
            schemaParam.DbType = DbType.String;
            command.Parameters.Add(schemaParam);

            var setupParam = command.CreateParameter();
            setupParam.ParameterName = ":setup";
            setupParam.DbType = DbType.String;
            command.Parameters.Add(setupParam);

            var versionParam = command.CreateParameter();
            versionParam.ParameterName = ":version";
            versionParam.DbType = DbType.String;
            command.Parameters.Add(versionParam);

            var lastServerSyncTimestampParam = command.CreateParameter();
            lastServerSyncTimestampParam.ParameterName = ":lastServerSyncTimestamp";
            lastServerSyncTimestampParam.DbType = DbType.Int64;
            command.Parameters.Add(lastServerSyncTimestampParam);

            var lastSyncTimestampParam = command.CreateParameter();
            lastSyncTimestampParam.ParameterName = ":lastSyncTimestamp";
            lastSyncTimestampParam.DbType = DbType.Int64;
            command.Parameters.Add(lastSyncTimestampParam);

            var lastSyncDurationParam = command.CreateParameter();
            lastSyncDurationParam.ParameterName = ":lastSyncDuration";
            lastSyncDurationParam.DbType = DbType.Int64;
            command.Parameters.Add(lastSyncDurationParam);

            var lastSyncParam = command.CreateParameter();
            lastSyncParam.ParameterName = ":lastSync";
            lastSyncParam.DbType = DbType.DateTime;
            command.Parameters.Add(lastSyncParam);
        }

        /// <summary>
        /// Helper method to create parameters for the ScopeInfoClient commands
        /// </summary>
        private static void AddScopeInfoClientParameters(DbCommand command)
        {
            var scopeIdParam = command.CreateParameter();
            scopeIdParam.ParameterName = ":scopeId";
            scopeIdParam.DbType = DbType.Guid;
            command.Parameters.Add(scopeIdParam);

            var scopeNameParam = command.CreateParameter();
            scopeNameParam.ParameterName = ":scopeName";
            scopeNameParam.DbType = DbType.String;
            command.Parameters.Add(scopeNameParam);

            var clientIdParam = command.CreateParameter();
            clientIdParam.ParameterName = ":clientId";
            clientIdParam.DbType = DbType.Guid;
            command.Parameters.Add(clientIdParam);

            var clientNameParam = command.CreateParameter();
            clientNameParam.ParameterName = ":clientName";
            clientNameParam.DbType = DbType.String;
            command.Parameters.Add(clientNameParam);

            var parametersParam = command.CreateParameter();
            parametersParam.ParameterName = ":parameters";
            parametersParam.DbType = DbType.String;
            command.Parameters.Add(parametersParam);

            var filtersParam = command.CreateParameter();
            filtersParam.ParameterName = ":filters";
            filtersParam.DbType = DbType.String;
            command.Parameters.Add(filtersParam);

            var propertiesParam = command.CreateParameter();
            propertiesParam.ParameterName = ":properties";
            propertiesParam.DbType = DbType.String;
            command.Parameters.Add(propertiesParam);

            var lastClientSyncTimestampParam = command.CreateParameter();
            lastClientSyncTimestampParam.ParameterName = ":lastClientSyncTimestamp";
            lastClientSyncTimestampParam.DbType = DbType.Int64;
            command.Parameters.Add(lastClientSyncTimestampParam);

            var lastServerSyncTimestampParam = command.CreateParameter();
            lastServerSyncTimestampParam.ParameterName = ":lastServerSyncTimestamp";
            lastServerSyncTimestampParam.DbType = DbType.Int64;
            command.Parameters.Add(lastServerSyncTimestampParam);

            var lastSyncTimestampParam = command.CreateParameter();
            lastSyncTimestampParam.ParameterName = ":lastSyncTimestamp";
            lastSyncTimestampParam.DbType = DbType.Int64;
            command.Parameters.Add(lastSyncTimestampParam);

            var lastSyncDurationParam = command.CreateParameter();
            lastSyncDurationParam.ParameterName = ":lastSyncDuration";
            lastSyncDurationParam.DbType = DbType.Int64;
            command.Parameters.Add(lastSyncDurationParam);

            var lastSyncParam = command.CreateParameter();
            lastSyncParam.ParameterName = ":lastSync";
            lastSyncParam.DbType = DbType.DateTime;
            command.Parameters.Add(lastSyncParam);
        }

        /// <summary>
        /// Gets a command to delete a scope info record.
        /// </summary>
        /// <param name="connection">The database connection.</param>
        /// <param name="transaction">Optional transaction to use.</param>
        /// <returns>A DbCommand object ready to be executed.</returns>
        public override DbCommand GetDeleteScopeInfoCommand(DbConnection connection, DbTransaction transaction)
        {
            var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = $@"
                DELETE FROM ""{this.tableName}""
                WHERE ""sync_scope_name"" = :scopeName AND ""sync_scope_id"" = :scopeId";

            var scopeNameParam = command.CreateParameter();
            scopeNameParam.ParameterName = ":scopeName";
            scopeNameParam.DbType = DbType.String;
            command.Parameters.Add(scopeNameParam);

            var scopeIdParam = command.CreateParameter();
            scopeIdParam.ParameterName = ":scopeId";
            scopeIdParam.DbType = DbType.Guid;
            command.Parameters.Add(scopeIdParam);

            return command;
        }

        /// <summary>
        /// Gets a command to check if a scope info exists.
        /// </summary>
        /// <param name="connection">The database connection.</param>
        /// <param name="transaction">Optional transaction to use.</param>
        /// <returns>A DbCommand object ready to be executed.</returns>
        public override DbCommand GetExistsScopeInfoCommand(DbConnection connection, DbTransaction transaction)
        {
            var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = $@"
                SELECT COUNT(*) 
                FROM ""{this.tableName}""
                WHERE ""sync_scope_name"" = :scopeName AND ""sync_scope_id"" = :scopeId";

            var scopeNameParam = command.CreateParameter();
            scopeNameParam.ParameterName = ":scopeName";
            scopeNameParam.DbType = DbType.String;
            command.Parameters.Add(scopeNameParam);

            var scopeIdParam = command.CreateParameter();
            scopeIdParam.ParameterName = ":scopeId";
            scopeIdParam.DbType = DbType.Guid;
            command.Parameters.Add(scopeIdParam);

            return command;
        }

        /// <summary>
        /// Gets a command to check if a scope info client exists.
        /// </summary>
        /// <param name="connection">The database connection.</param>
        /// <param name="transaction">Optional transaction to use.</param>
        /// <returns>A DbCommand object ready to be executed.</returns>
        public override DbCommand GetExistsScopeInfoClientCommand(DbConnection connection, DbTransaction transaction)
        {
            var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = $@"
                SELECT COUNT(*) 
                FROM ""{this.clientTableName}""
                WHERE ""sync_scope_name"" = :scopeName AND ""sync_scope_id"" = :scopeId AND ""sync_scope_client_id"" = :clientId";

            var scopeNameParam = command.CreateParameter();
            scopeNameParam.ParameterName = ":scopeName";
            scopeNameParam.DbType = DbType.String;
            command.Parameters.Add(scopeNameParam);

            var scopeIdParam = command.CreateParameter();
            scopeIdParam.ParameterName = ":scopeId";
            scopeIdParam.DbType = DbType.Guid;
            command.Parameters.Add(scopeIdParam);

            var clientIdParam = command.CreateParameter();
            clientIdParam.ParameterName = ":clientId";
            clientIdParam.DbType = DbType.Guid;
            command.Parameters.Add(clientIdParam);

            return command;
        }

        /// <summary>
        /// Gets a command to check if the scope info table exists.
        /// </summary>
        /// <param name="connection">The database connection.</param>
        /// <param name="transaction">Optional transaction to use.</param>
        /// <returns>A DbCommand object ready to be executed.</returns>
        public override DbCommand GetExistsScopeInfoTableCommand(DbConnection connection, DbTransaction transaction)
        {
            var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = $@"
                SELECT COUNT(*) 
                FROM USER_TABLES 
                WHERE TABLE_NAME = :tableName";

            var parameter = command.CreateParameter();
            parameter.ParameterName = ":tableName";
            parameter.Value = this.tableName.ToUpper(CultureInfo.InvariantCulture); // Oracle stores identifiers in upper case by default
            command.Parameters.Add(parameter);

            return command;
        }

        /// <summary>
        /// Gets a command to check if the scope info client table exists.
        /// </summary>
        /// <param name="connection">The database connection.</param>
        /// <param name="transaction">Optional transaction to use.</param>
        /// <returns>A DbCommand object ready to be executed.</returns>
        public override DbCommand GetExistsScopeInfoClientTableCommand(DbConnection connection, DbTransaction transaction)
        {
            var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = $@"
                SELECT COUNT(*) 
                FROM USER_TABLES 
                WHERE TABLE_NAME = :tableName";

            var parameter = command.CreateParameter();
            parameter.ParameterName = ":tableName";
            parameter.Value = this.clientTableName.ToUpper(CultureInfo.InvariantCulture); // Oracle stores identifiers in upper case by default
            command.Parameters.Add(parameter);

            return command;
        }

        /// <summary>
        /// Gets a command to create the scope info table.
        /// </summary>
        /// <param name="connection">The database connection.</param>
        /// <param name="transaction">Optional transaction to use.</param>
        /// <returns>A DbCommand object ready to be executed.</returns>
        public override DbCommand GetCreateScopeInfoTableCommand(DbConnection connection, DbTransaction transaction)
        {
            var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = CreateScopeInfoTableScriptAsync(connection, transaction);
            return command;
        }

        /// <summary>
        /// Gets a command to create the scope info client table.
        /// </summary>
        /// <param name="connection">The database connection.</param>
        /// <param name="transaction">Optional transaction to use.</param>
        /// <returns>A DbCommand object ready to be executed.</returns>
        public override DbCommand GetCreateScopeInfoClientTableCommand(DbConnection connection, DbTransaction transaction)
        {
            var stringBuilder = new StringBuilder();

            // Create the scope info client table
            stringBuilder.AppendLine($"CREATE TABLE \"{this.clientTableName}\" (");
            stringBuilder.AppendLine($"  \"sync_scope_id\" VARCHAR2(36) NOT NULL,");
            stringBuilder.AppendLine($"  \"sync_scope_name\" VARCHAR2(100) NOT NULL,");
            stringBuilder.AppendLine($"  \"sync_scope_client_id\" VARCHAR2(36) NOT NULL,");
            stringBuilder.AppendLine($"  \"sync_scope_client_name\" VARCHAR2(100) NULL,");
            stringBuilder.AppendLine($"  \"sync_scope_parameters\" CLOB NULL,");
            stringBuilder.AppendLine($"  \"sync_scope_filters\" CLOB NULL,");
            stringBuilder.AppendLine($"  \"sync_scope_properties\" CLOB NULL,");
            stringBuilder.AppendLine($"  \"sync_scope_last_client_sync_timestamp\" NUMBER NULL,");
            stringBuilder.AppendLine($"  \"sync_scope_last_server_sync_timestamp\" NUMBER NULL,");
            stringBuilder.AppendLine($"  \"sync_scope_last_sync_timestamp\" NUMBER NULL,");
            stringBuilder.AppendLine($"  \"sync_scope_last_sync_duration\" NUMBER NULL,");
            stringBuilder.AppendLine($"  \"sync_scope_last_sync\" TIMESTAMP NULL,");
            stringBuilder.AppendLine($"  CONSTRAINT \"PK_{this.clientTableName}\" PRIMARY KEY (\"sync_scope_id\", \"sync_scope_name\", \"sync_scope_client_id\")");
            stringBuilder.AppendLine($")");

            var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = stringBuilder.ToString();
            return command;
        }

        /// <summary>
        /// Gets a command to retrieve all scope info records.
        /// </summary>
        /// <param name="connection">The database connection.</param>
        /// <param name="transaction">Optional transaction to use.</param>
        /// <returns>A DbCommand object ready to be executed.</returns>
        public override DbCommand GetAllScopeInfosCommand(DbConnection connection, DbTransaction transaction)
        {
            var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = $@"
                SELECT ""sync_scope_id"", ""sync_scope_name"", ""sync_scope_schema"", ""sync_scope_setup"", ""sync_scope_version"", 
                       ""sync_scope_last_server_sync_timestamp"", ""sync_scope_last_sync_timestamp"", ""sync_scope_last_sync_duration"", ""sync_scope_last_sync""
                FROM ""{this.tableName}""";

            return command;
        }

        /// <summary>
        /// Gets a command to retrieve all scope info client records.
        /// </summary>
        /// <param name="connection">The database connection.</param>
        /// <param name="transaction">Optional transaction to use.</param>
        /// <returns>A DbCommand object ready to be executed.</returns>
        public override DbCommand GetAllScopeInfoClientsCommand(DbConnection connection, DbTransaction transaction)
        {
            var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = $@"
                SELECT ""sync_scope_id"", ""sync_scope_name"", ""sync_scope_client_id"", ""sync_scope_client_name"", 
                       ""sync_scope_parameters"", ""sync_scope_filters"", ""sync_scope_properties"", 
                       ""sync_scope_last_client_sync_timestamp"", ""sync_scope_last_server_sync_timestamp"", 
                       ""sync_scope_last_sync_timestamp"", ""sync_scope_last_sync_duration"", ""sync_scope_last_sync""
                FROM ""{this.clientTableName}""";

            return command;
        }

        /// <summary>
        /// Gets a command to retrieve a specific scope info record.
        /// </summary>
        /// <param name="connection">The database connection.</param>
        /// <param name="transaction">Optional transaction to use.</param>
        /// <returns>A DbCommand object ready to be executed.</returns>
        public override DbCommand GetScopeInfoCommand(DbConnection connection, DbTransaction transaction)
        {
            var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = $@"
                SELECT ""sync_scope_id"", ""sync_scope_name"", ""sync_scope_schema"", ""sync_scope_setup"", ""sync_scope_version"", 
                       ""sync_scope_last_server_sync_timestamp"", ""sync_scope_last_sync_timestamp"", ""sync_scope_last_sync_duration"", ""sync_scope_last_sync""
                FROM ""{this.tableName}""
                WHERE ""sync_scope_name"" = :scopeName AND ""sync_scope_id"" = :scopeId";

            var scopeNameParam = command.CreateParameter();
            scopeNameParam.ParameterName = ":scopeName";
            scopeNameParam.DbType = DbType.String;
            command.Parameters.Add(scopeNameParam);

            var scopeIdParam = command.CreateParameter();
            scopeIdParam.ParameterName = ":scopeId";
            scopeIdParam.DbType = DbType.Guid;
            command.Parameters.Add(scopeIdParam);

            return command;
        }

        /// <summary>
        /// Gets a command to retrieve a specific scope info client record.
        /// </summary>
        /// <param name="connection">The database connection.</param>
        /// <param name="transaction">Optional transaction to use.</param>
        /// <returns>A DbCommand object ready to be executed.</returns>
        public override DbCommand GetScopeInfoClientCommand(DbConnection connection, DbTransaction transaction)
        {
            var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = $@"
                SELECT ""sync_scope_id"", ""sync_scope_name"", ""sync_scope_client_id"", ""sync_scope_client_name"", 
                       ""sync_scope_parameters"", ""sync_scope_filters"", ""sync_scope_properties"", 
                       ""sync_scope_last_client_sync_timestamp"", ""sync_scope_last_server_sync_timestamp"", 
                       ""sync_scope_last_sync_timestamp"", ""sync_scope_last_sync_duration"", ""sync_scope_last_sync""
                FROM ""{this.clientTableName}""
                WHERE ""sync_scope_name"" = :scopeName AND ""sync_scope_id"" = :scopeId AND ""sync_scope_client_id"" = :clientId";

            var scopeNameParam = command.CreateParameter();
            scopeNameParam.ParameterName = ":scopeName";
            scopeNameParam.DbType = DbType.String;
            command.Parameters.Add(scopeNameParam);

            var scopeIdParam = command.CreateParameter();
            scopeIdParam.ParameterName = ":scopeId";
            scopeIdParam.DbType = DbType.Guid;
            command.Parameters.Add(scopeIdParam);

            var clientIdParam = command.CreateParameter();
            clientIdParam.ParameterName = ":clientId";
            clientIdParam.DbType = DbType.Guid;
            command.Parameters.Add(clientIdParam);

            return command;
        }

        /// <summary>
        /// Gets a command to update a scope info record.
        /// </summary>
        /// <param name="connection">The database connection.</param>
        /// <param name="transaction">Optional transaction to use.</param>
        /// <returns>A DbCommand object ready to be executed.</returns>
        public override DbCommand GetUpdateScopeInfoCommand(DbConnection connection, DbTransaction transaction)
        {
            var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = $@"
                UPDATE ""{this.tableName}"" SET
                ""sync_scope_schema"" = :schema,
                ""sync_scope_setup"" = :setup,
                ""sync_scope_version"" = :version,
                ""sync_scope_last_server_sync_timestamp"" = :lastServerSyncTimestamp,
                ""sync_scope_last_sync_timestamp"" = :lastSyncTimestamp,
                ""sync_scope_last_sync_duration"" = :lastSyncDuration,
                ""sync_scope_last_sync"" = :lastSync
                WHERE ""sync_scope_name"" = :scopeName AND ""sync_scope_id"" = :scopeId";

            AddScopeInfoParameters(command);

            return command;
        }

        /// <summary>
        /// Gets a command to update a scope info client record.
        /// </summary>
        /// <param name="connection">The database connection.</param>
        /// <param name="transaction">Optional transaction to use.</param>
        /// <returns>A DbCommand object ready to be executed.</returns>
        public override DbCommand GetUpdateScopeInfoClientCommand(DbConnection connection, DbTransaction transaction)
        {
            var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = $@"
                UPDATE ""{this.clientTableName}"" SET
                ""sync_scope_client_name"" = :clientName,
                ""sync_scope_parameters"" = :parameters,
                ""sync_scope_filters"" = :filters,
                ""sync_scope_properties"" = :properties,
                ""sync_scope_last_client_sync_timestamp"" = :lastClientSyncTimestamp,
                ""sync_scope_last_server_sync_timestamp"" = :lastServerSyncTimestamp,
                ""sync_scope_last_sync_timestamp"" = :lastSyncTimestamp,
                ""sync_scope_last_sync_duration"" = :lastSyncDuration,
                ""sync_scope_last_sync"" = :lastSync
                WHERE ""sync_scope_name"" = :scopeName AND ""sync_scope_id"" = :scopeId AND ""sync_scope_client_id"" = :clientId";

            AddScopeInfoClientParameters(command);

            return command;
        }

        /// <summary>
        /// Gets a command to retrieve the local timestamp from the database.
        /// </summary>
        /// <param name="connection">The database connection.</param>
        /// <param name="transaction">Optional transaction to use.</param>
        /// <returns>A DbCommand object ready to be executed.</returns>
        public override DbCommand GetLocalTimestampCommand(DbConnection connection, DbTransaction transaction)
        {
            var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = "SELECT DBMS_FLASHBACK.GET_SYSTEM_CHANGE_NUMBER FROM DUAL";
            return command;
        }

        /// <summary>
        /// Gets a command to drop the scope info table.
        /// </summary>
        /// <param name="connection">The database connection.</param>
        /// <param name="transaction">Optional transaction to use.</param>
        /// <returns>A DbCommand object ready to be executed.</returns>
        public override DbCommand GetDropScopeInfoTableCommand(DbConnection connection, DbTransaction transaction)
        {
            var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = $"DROP TABLE \"{this.tableName}\"";
            return command;
        }

        /// <summary>
        /// Gets a command to drop the scope info client table.
        /// </summary>
        /// <param name="connection">The database connection.</param>
        /// <param name="transaction">Optional transaction to use.</param>
        /// <returns>A DbCommand object ready to be executed.</returns>
        public override DbCommand GetDropScopeInfoClientTableCommand(DbConnection connection, DbTransaction transaction)
        {
            var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = $"DROP TABLE \"{this.clientTableName}\"";
            return command;
        }
    }
} 