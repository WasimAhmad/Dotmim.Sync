using Dotmim.Sync.Builders;
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
            catch (Exception ex)
            {
                Console.WriteLine($"Error in DropTableAsync: {ex.Message}");
                // Just a bad error if table not exists
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
        {
            // Implementation needed
            throw new NotImplementedException();
        }

        /// <summary>
        /// Get all tables with column names from a database.
        /// </summary>
        public override Task<SyncSetup> GetAllTablesAsync(DbConnection connection, DbTransaction transaction = null)
        {
            // Implementation needed
            throw new NotImplementedException();
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
        public override Task<SyncTable> GetTableAsync(string tableName, string schemaName, DbConnection connection, DbTransaction transaction = null)
        {
            // Implementation needed
            throw new NotImplementedException();
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

                    // If we're also changing the schema, we need to move the table after renaming it
                    if (!string.IsNullOrEmpty(newSchemaName) && 
                        (!string.IsNullOrEmpty(schemaName) && !schemaName.Equals(newSchemaName, StringComparison.OrdinalIgnoreCase)))
                    {
                        // First rename the table
                        await command.ExecuteNonQueryAsync();

                        // Now move it to the new schema
                        command.CommandText = $"ALTER TABLE \"{newTableName.ToUpperInvariant()}\" MOVE TABLESPACE \"{newSchemaName.ToUpperInvariant()}\"";
                        await command.ExecuteNonQueryAsync();
                    }
                    else
                    {
                        // Just rename the table
                        await command.ExecuteNonQueryAsync();
                    }
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