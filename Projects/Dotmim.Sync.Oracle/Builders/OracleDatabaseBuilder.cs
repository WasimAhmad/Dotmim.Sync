using Dotmim.Sync.Builders;
using Dotmim.Sync.DatabaseStringParsers;
using Oracle.ManagedDataAccess.Client;
using System;
using System.Collections.Generic;
using System.Data;
using System.Data.Common;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Dotmim.Sync.Oracle.Builders
{
    /// <summary>
    /// Oracle implementation of the DbDatabaseBuilder, providing database operations
    /// specific to Oracle database systems.
    /// </summary>
    public class OracleDatabaseBuilder : DbDatabaseBuilder
    {
        /// <summary>
        /// Create a database if it doesn't exists already
        /// </summary>
        public Task CreateDatabaseAsync(string databaseName, DbConnection connection, DbTransaction transaction = null)
        {
            // No need to create a database with Oracle
            return Task.CompletedTask;
        }

        /// <summary>
        /// Drop a table if exists
        /// </summary>
        public async Task DropTableAsync(string tableName, string schemaName, DbConnection connection, DbTransaction transaction = null)
        {
            // Just trying to execute drop command. No control
            var commandText = $"DROP TABLE {tableName}";

            var command = new OracleCommand(commandText);
            command.Connection = (OracleConnection)connection;
            command.Transaction = (OracleTransaction)transaction;

            try
            {
                await command.ExecuteNonQueryAsync();
            }
            catch (Exception)
            {
                // Ignored: the table may not exist.
            }
        }

        /// <summary>
        /// Check if a database exists
        /// </summary>
        public Task<bool> DatabaseExistsAsync(string databaseName, DbConnection connection)
        {
            // Always true since we connect to a valid database name
            return Task.FromResult(true);
        }

        /// <summary>
        /// Check a stored procedure exists
        /// </summary>
        public async Task<bool> ProcedureExistsAsync(string procedureName, string schemaName, DbConnection connection, DbTransaction transaction = null)
        {
            if (string.IsNullOrEmpty(procedureName))
                throw new ArgumentNullException("procedureName");

            var commandText = new StringBuilder();

            commandText.AppendLine("SELECT COUNT(*) FROM ALL_OBJECTS ");
            commandText.AppendLine("WHERE OWNER = :owner ");
            commandText.AppendLine("AND OBJECT_NAME = :name ");
            commandText.AppendLine("AND OBJECT_TYPE = 'PROCEDURE'");

            var command = new OracleCommand(commandText.ToString());
            command.Connection = (OracleConnection)connection;
            command.Transaction = (OracleTransaction)transaction;

            if (string.IsNullOrEmpty(schemaName))
                schemaName = connection.Database;

            var p = command.CreateParameter();
            p.ParameterName = ":owner";
            p.DbType = DbType.String;
            p.Value = schemaName;
            command.Parameters.Add(p);

            p = command.CreateParameter();
            p.ParameterName = ":name";
            p.DbType = DbType.String;
            p.Value = procedureName;
            command.Parameters.Add(p);

            var result = Convert.ToInt32(await command.ExecuteScalarAsync());

            return result > 0;
        }

        /// <summary>
        /// Check a schema exists
        /// </summary>
        public async Task<bool> SchemaExistsAsync(string schemaName, DbConnection connection, DbTransaction transaction = null)
        {
            if (string.IsNullOrEmpty(schemaName))
                throw new ArgumentNullException("schemaName");

            var commandText = new StringBuilder();

            commandText.AppendLine("SELECT COUNT(*) FROM ALL_USERS ");
            commandText.AppendLine("WHERE USERNAME = :username ");

            var command = new OracleCommand(commandText.ToString());
            command.Connection = (OracleConnection)connection;
            command.Transaction = (OracleTransaction)transaction;

            if (string.IsNullOrEmpty(schemaName))
                schemaName = connection.Database;

            var p = command.CreateParameter();
            p.ParameterName = ":username";
            p.DbType = DbType.String;
            p.Value = schemaName.ToUpperInvariant();
            command.Parameters.Add(p);

            var result = Convert.ToInt32(await command.ExecuteScalarAsync());

            return result > 0;
        }

        /// <summary>
        /// Check if a table exists
        /// </summary>
        public async Task<bool> TableExistsAsync(string tableName, string schemaName, DbConnection connection, DbTransaction transaction = null)
        {
            if (string.IsNullOrEmpty(tableName))
                throw new ArgumentNullException(nameof(tableName));

            var commandText = new StringBuilder();

            commandText.AppendLine("SELECT COUNT(*) FROM ALL_TABLES ");
            commandText.AppendLine("WHERE OWNER = :owner ");
            commandText.AppendLine("AND TABLE_NAME = :name ");

            var command = new OracleCommand(commandText.ToString());
            command.Connection = (OracleConnection)connection;
            command.Transaction = (OracleTransaction)transaction;

            if (string.IsNullOrEmpty(schemaName))
                schemaName = connection.Database;

            var p = command.CreateParameter();
            p.ParameterName = ":owner";
            p.DbType = DbType.String;
            p.Value = schemaName;
            command.Parameters.Add(p);

            p = command.CreateParameter();
            p.ParameterName = ":name";
            p.DbType = DbType.String;
            p.Value = tableName;
            command.Parameters.Add(p);

            var result = Convert.ToInt32(await command.ExecuteScalarAsync());

            return result > 0;
        }

        /// <summary>
        /// Check if a trigger exists
        /// </summary>
        public async Task<bool> TriggerExistsAsync(string triggerName, string schemaName, DbConnection connection, DbTransaction transaction = null)
        {
            if (string.IsNullOrEmpty(triggerName))
                throw new ArgumentNullException("triggerName");

            var commandText = new StringBuilder();

            commandText.AppendLine("SELECT COUNT(*) FROM ALL_TRIGGERS ");
            commandText.AppendLine("WHERE OWNER = :owner ");
            commandText.AppendLine("AND TRIGGER_NAME = :name ");

            var command = new OracleCommand(commandText.ToString());
            command.Connection = (OracleConnection)connection;
            command.Transaction = (OracleTransaction)transaction;

            if (string.IsNullOrEmpty(schemaName))
                schemaName = connection.Database;

            var p = command.CreateParameter();
            p.ParameterName = ":owner";
            p.DbType = DbType.String;
            p.Value = schemaName;
            command.Parameters.Add(p);

            p = command.CreateParameter();
            p.ParameterName = ":name";
            p.DbType = DbType.String;
            p.Value = triggerName;
            command.Parameters.Add(p);

            var result = Convert.ToInt32(await command.ExecuteScalarAsync());

            return result > 0;
        }

        /// <summary>
        /// Check if a type exists
        /// </summary>
        public async Task<bool> TypeExistsAsync(string typeName, string schemaName, DbConnection connection, DbTransaction transaction = null)
        {
            if (string.IsNullOrEmpty(typeName))
                throw new ArgumentNullException("typeName");

            var commandText = new StringBuilder();

            commandText.AppendLine("SELECT COUNT(*) FROM ALL_TYPES ");
            commandText.AppendLine("WHERE OWNER = :owner ");
            commandText.AppendLine("AND TYPE_NAME = :name ");

            var command = new OracleCommand(commandText.ToString());
            command.Connection = (OracleConnection)connection;
            command.Transaction = (OracleTransaction)transaction;

            if (string.IsNullOrEmpty(schemaName))
                schemaName = connection.Database;

            var p = command.CreateParameter();
            p.ParameterName = ":owner";
            p.DbType = DbType.String;
            p.Value = schemaName;
            command.Parameters.Add(p);

            p = command.CreateParameter();
            p.ParameterName = ":name";
            p.DbType = DbType.String;
            p.Value = typeName;
            command.Parameters.Add(p);

            var result = Convert.ToInt32(await command.ExecuteScalarAsync());

            return result > 0;
        }

        /// <summary>
        /// Drop a procedure
        /// </summary>
        public async Task DropProcedureAsync(string procedureName, string schemaName, DbConnection connection, DbTransaction transaction = null)
        {
            if (string.IsNullOrEmpty(procedureName))
                throw new ArgumentNullException("procedureName");

            // Test if exists
            if (await this.ProcedureExistsAsync(procedureName, schemaName, connection, transaction))
            {
                string commandText = $"DROP PROCEDURE {procedureName}";

                var command = new OracleCommand(commandText);
                command.Connection = (OracleConnection)connection;
                command.Transaction = (OracleTransaction)transaction;

                await command.ExecuteNonQueryAsync();
            }
        }

        /// <summary>
        /// Drop a trigger
        /// </summary>
        public async Task DropTriggerAsync(string triggerName, string schemaName, DbConnection connection, DbTransaction transaction = null)
        {
            if (string.IsNullOrEmpty(triggerName))
                throw new ArgumentNullException("triggerName");

            // Test if exists
            if (await this.TriggerExistsAsync(triggerName, schemaName, connection, transaction))
            {
                string commandText = $"DROP TRIGGER {triggerName}";

                var command = new OracleCommand(commandText);
                command.Connection = (OracleConnection)connection;
                command.Transaction = (OracleTransaction)transaction;

                await command.ExecuteNonQueryAsync();
            }
        }

        /// <summary>
        /// First step before creating schema.
        /// </summary>
        public override Task EnsureDatabaseAsync(DbConnection connection, DbTransaction transaction = null)
        {
            // No need to create a database with Oracle
            return Task.CompletedTask;
        }

        /// <summary>
        /// First step before creating schema.
        /// </summary>
        public override Task<SyncTable> EnsureTableAsync(string tableName, string schemaName, DbConnection connection, DbTransaction transaction = null)
            => Task.FromResult(string.IsNullOrEmpty(schemaName) ? new SyncTable(tableName) : new SyncTable(tableName, schemaName));

        /// <summary>
        /// Get all tables with column names from a database.
        /// </summary>
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
                    connection.Close();
            }

            return setup;
        }

        /// <summary>
        /// Make a hello test on the current database.
        /// </summary>
        public override Task<(string DatabaseName, string Version)> GetHelloAsync(DbConnection connection, DbTransaction transaction = null)
        {
            var databaseNameCommand = connection.CreateCommand();
            databaseNameCommand.Transaction = transaction;
            databaseNameCommand.CommandText = "SELECT SYS_CONTEXT('USERENV', 'DB_NAME') FROM DUAL";

            var versionCommand = connection.CreateCommand();
            versionCommand.Transaction = transaction;
            versionCommand.CommandText = "SELECT BANNER FROM V$VERSION WHERE BANNER LIKE 'Oracle Database%'";

            return Task.Run(() =>
            {
                bool alreadyOpened = connection.State == ConnectionState.Open;

                try
                {
                    if (!alreadyOpened)
                        connection.Open();

                    var databaseName = databaseNameCommand.ExecuteScalar()?.ToString() ?? "Oracle";
                    var versionText = versionCommand.ExecuteScalar()?.ToString() ?? "Unknown";

                    // Extract version number from banner text
                    string version = versionText;
                    if (versionText.Contains("Oracle Database"))
                    {
                        var parts = versionText.Split(' ');
                        if (parts.Length >= 3)
                            version = parts[2];
                    }

                    return (databaseName, version);
                }
                finally
                {
                    if (!alreadyOpened && connection.State == ConnectionState.Open)
                        connection.Close();
                }
            });
        }

        /// <summary>
        /// Get a table with all rows from a table.
        /// </summary>
        public override async Task<SyncTable> GetTableAsync(string tableName, string schemaName, DbConnection connection, DbTransaction transaction = null)
        {
            var tableParser = new TableParser(tableName, '"', '"');
            var parsedName = tableParser.TableName;
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
                        var precision = reader.IsDBNull(3) ? (byte)0 : Convert.ToByte(reader.GetValue(3));
                        var scale = reader.IsDBNull(4) ? (byte)0 : Convert.ToByte(reader.GetValue(4));
                        var column = new SyncColumn(reader.GetString(0))
                        {
                            OriginalTypeName = dataType,
                            AllowDBNull = reader.GetString(5) == "Y",
                            MaxLength = dataType.IndexOf("CHAR", StringComparison.OrdinalIgnoreCase) >= 0
                                ? (reader.IsDBNull(6) ? 0 : Convert.ToInt32(reader.GetValue(6)))
                                : (reader.IsDBNull(2) ? 0 : Convert.ToInt32(reader.GetValue(2))),
                            Precision = precision,
                            Scale = scale,
                        };
                        column.SetType(OracleTableBuilder.GetManagedType(dataType, precision, scale));
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
                    connection.Close();
            }

            return syncTable;
        }

        /// <summary>
        /// Check if a table exists.
        /// </summary>
        public override Task<bool> ExistsTableAsync(string tableName, string schemaName, DbConnection connection, DbTransaction transaction = null)
        {
            if (string.IsNullOrEmpty(tableName))
                throw new ArgumentNullException(nameof(tableName));

            var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = @"
                SELECT COUNT(*) 
                FROM ALL_TABLES 
                WHERE OWNER = :owner 
                AND TABLE_NAME = :name";

            var ownerParam = command.CreateParameter();
            ownerParam.ParameterName = ":owner";
            ownerParam.Value = string.IsNullOrEmpty(schemaName) ? 
                connection.Database : schemaName.ToUpperInvariant();
            command.Parameters.Add(ownerParam);

            var nameParam = command.CreateParameter();
            nameParam.ParameterName = ":name";
            nameParam.Value = tableName.ToUpperInvariant();
            command.Parameters.Add(nameParam);

            return Task.Run(() =>
            {
                bool alreadyOpened = connection.State == ConnectionState.Open;

                try
                {
                    if (!alreadyOpened)
                        connection.Open();

                    var result = command.ExecuteScalar();
                    int count = Convert.ToInt32(result);
                    return count > 0;
                }
                finally
                {
                    if (!alreadyOpened && connection.State == ConnectionState.Open)
                        connection.Close();
                }
            });
        }

        /// <summary>
        /// Drops a table if exists.
        /// </summary>
        public override Task DropsTableIfExistsAsync(string tableName, string schemaName, DbConnection connection, DbTransaction transaction = null)
        {
            if (string.IsNullOrEmpty(tableName))
                throw new ArgumentNullException(nameof(tableName));

            // First check if the table exists
            return ExistsTableAsync(tableName, schemaName, connection, transaction)
                .ContinueWith(async existsTask =>
                {
                    if (existsTask.Result)
                    {
                        // Table exists, drop it
                        string qualifiedTableName;
                        if (string.IsNullOrEmpty(schemaName))
                            qualifiedTableName = $"\"{tableName.ToUpperInvariant()}\"";
                        else
                            qualifiedTableName = $"\"{schemaName.ToUpperInvariant()}\".\"{tableName.ToUpperInvariant()}\"";

                        var command = connection.CreateCommand();
                        command.Transaction = transaction;
                        command.CommandText = $"DROP TABLE {qualifiedTableName}";

                        bool alreadyOpened = connection.State == ConnectionState.Open;

                        try
                        {
                            if (!alreadyOpened)
                                connection.Open();

                            await command.ExecuteNonQueryAsync();
                        }
                        finally
                        {
                            if (!alreadyOpened && connection.State == ConnectionState.Open)
                                connection.Close();
                        }
                    }
                }).Unwrap();
        }

        /// <summary>
        /// Rename a table.
        /// </summary>
        public override Task RenameTableAsync(string tableName, string schemaName, string newTableName, string newSchemaName, DbConnection connection, DbTransaction transaction = null)
        {
            if (string.IsNullOrEmpty(tableName))
                throw new ArgumentNullException(nameof(tableName));
            if (string.IsNullOrEmpty(newTableName))
                throw new ArgumentNullException(nameof(newTableName));

            string qualifiedTableName;
            if (string.IsNullOrEmpty(schemaName))
                qualifiedTableName = $"\"{tableName.ToUpperInvariant()}\"";
            else
                qualifiedTableName = $"\"{schemaName.ToUpperInvariant()}\".\"{tableName.ToUpperInvariant()}\"";

            var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = $"ALTER TABLE {qualifiedTableName} RENAME TO \"{newTableName.ToUpperInvariant()}\"";

            return Task.Run(async () =>
            {
                bool alreadyOpened = connection.State == ConnectionState.Open;

                try
                {
                    if (!alreadyOpened)
                        connection.Open();

                    // Oracle's ALTER TABLE ... RENAME TO renames within the current schema.
                    // Moving a table across schemas is not a simple rename and is not supported here.
                    await command.ExecuteNonQueryAsync();
                }
                finally
                {
                    if (!alreadyOpened && connection.State == ConnectionState.Open)
                        connection.Close();
                }
            });
        }
    }
} 