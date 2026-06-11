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

            var command = connection.CreateCommand().EnsureBindByName();
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
            // "database" in this provider's model is the user/schema
            var databaseNameCommand = connection.CreateCommand().EnsureBindByName();
            databaseNameCommand.Transaction = transaction;
            databaseNameCommand.CommandText = "SELECT SYS_CONTEXT('USERENV', 'CURRENT_SCHEMA') FROM DUAL";

            // PRODUCT_COMPONENT_VERSION is granted to PUBLIC, unlike V$VERSION
            var versionCommand = connection.CreateCommand().EnsureBindByName();
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
                var columnsCommand = connection.CreateCommand().EnsureBindByName();
                columnsCommand.Transaction = transaction;

                // USER_TAB_COLS (not USER_TAB_COLUMNS) exposes VIRTUAL_COLUMN; virtual (computed)
                // columns must be flagged so they are excluded from sync DML on every side.
                columnsCommand.CommandText = @"
                    SELECT COLUMN_NAME, DATA_TYPE, DATA_LENGTH, DATA_PRECISION, DATA_SCALE, NULLABLE, CHAR_LENGTH, VIRTUAL_COLUMN
                    FROM USER_TAB_COLS WHERE TABLE_NAME = :tableName AND HIDDEN_COLUMN = 'NO' ORDER BY COLUMN_ID";
                var p = columnsCommand.CreateParameter();
                p.ParameterName = ":tableName";
                p.Value = parsedName;
                columnsCommand.Parameters.Add(p);

                using (var reader = await columnsCommand.ExecuteReaderAsync().ConfigureAwait(false))
                {
                    while (await reader.ReadAsync().ConfigureAwait(false))
                    {
                        var dataType = reader.GetString(1);
                        var dataLength = await reader.IsDBNullAsync(2).ConfigureAwait(false) ? 0 : Convert.ToInt32(reader.GetValue(2));
                        var precision = await reader.IsDBNullAsync(3).ConfigureAwait(false) ? (byte)0 : Convert.ToByte(reader.GetValue(3));
                        var scale = await reader.IsDBNullAsync(4).ConfigureAwait(false) ? (byte)0 : Convert.ToByte(reader.GetValue(4));
                        var charLength = await reader.IsDBNullAsync(6).ConfigureAwait(false) ? 0 : Convert.ToInt32(reader.GetValue(6));
                        var isVirtual = !await reader.IsDBNullAsync(7).ConfigureAwait(false) && reader.GetString(7) == "YES";
                        var column = new SyncColumn(reader.GetString(0))
                        {
                            OriginalTypeName = dataType,
                            AllowDBNull = reader.GetString(5) == "Y",
                            MaxLength = dataType.Contains("CHAR", StringComparison.OrdinalIgnoreCase) ? charLength : dataLength,
                            Precision = precision,
                            Scale = scale,
                            IsCompute = isVirtual,
                        };
                        column.SetType(OracleTableBuilder.GetManagedType(dataType, precision, scale, dataLength));
                        syncTable.Columns.Add(column);
                    }
                }

                // Primary keys
                var pkCommand = connection.CreateCommand().EnsureBindByName();
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

            var command = connection.CreateCommand().EnsureBindByName();
            command.Transaction = transaction;

            if (string.IsNullOrEmpty(schemaName))
            {
                // current user's tables; never rely on connection.Database (empty in ODP.NET)
                command.CommandText = "SELECT COUNT(*) FROM USER_TABLES WHERE TABLE_NAME = :name";
            }
            else
            {
                command.CommandText = "SELECT COUNT(*) FROM ALL_TABLES WHERE OWNER = :owner AND TABLE_NAME = :name";
                var ownerParameter = command.CreateParameter();
                ownerParameter.ParameterName = ":owner";
                ownerParameter.Value = schemaName;
                command.Parameters.Add(ownerParameter);
            }

            var nameParameter = command.CreateParameter();
            nameParameter.ParameterName = ":name";
            nameParameter.Value = parsedName;
            command.Parameters.Add(nameParameter);

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

            var command = connection.CreateCommand().EnsureBindByName();
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

            var command = connection.CreateCommand().EnsureBindByName();
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
