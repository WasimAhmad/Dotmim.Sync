using Dotmim.Sync.Builders;
using Dotmim.Sync.Enumerations;
using Dotmim.Sync.Manager;
using Dotmim.Sync.Oracle.Builders;
using Dotmim.Sync.Oracle.Manager;
using Oracle.ManagedDataAccess.Client;
using System;
using System.Data.Common;

namespace Dotmim.Sync.Oracle
{
    /// <summary>
    /// Oracle provider for Dotmim.Sync
    /// </summary>
    public class OracleSyncProvider : CoreProvider
    {
        private static string shortProviderType;
        private static string providerType;
        private DbMetadata dbMetadata;
        private OracleConnectionStringBuilder builder;

        /// <inheritdoc cref="OracleSyncProvider" />
        public OracleSyncProvider()
            : base() { }

        /// <summary>
        /// Gets the provider type.
        /// </summary>
        public static string ProviderType
        {
            get
            {
                if (!string.IsNullOrEmpty(providerType))
                    return providerType;

                var type = typeof(OracleSyncProvider);
                providerType = $"{type.Name}, {type}";

                return providerType;
            }
        }

        /// <summary>
        /// Gets the short provider type.
        /// </summary>
        public static string ShortProviderType
        {
            get
            {
                if (!string.IsNullOrEmpty(shortProviderType))
                    return shortProviderType;

                var type = typeof(OracleSyncProvider);
                shortProviderType = type.Name;

                return shortProviderType;
            }
        }

        /// <summary>
        /// Gets or sets the Oracle connection string.
        /// </summary>
        public override string ConnectionString
        {
            get => this.builder == null || string.IsNullOrEmpty(this.builder.ConnectionString) ? null : this.builder.ConnectionString;
            set
            {
                this.builder = string.IsNullOrEmpty(value) ? null : new OracleConnectionStringBuilder(value);
            }
        }

        /// <inheritdoc cref="OracleSyncProvider"/>
        public OracleSyncProvider(string connectionString)
            : base() => this.ConnectionString = connectionString;

        /// <inheritdoc cref="OracleSyncProvider"/>
        public OracleSyncProvider(OracleConnectionStringBuilder builder)
            : base()
        {
            if (builder == null || string.IsNullOrEmpty(builder.ConnectionString))
                throw new Exception("You have to provide parameters to the Oracle builder to be able to construct a valid connection string.");

            this.builder = builder;
        }

        /// <inheritdoc/>
        public override DbConnection CreateConnection() => new OracleConnection(this.ConnectionString);

        /// <inheritdoc/>
        public override string GetProviderTypeName() => ProviderType;

        /// <inheritdoc/>
        public override string DefaultSchemaName => null; // Oracle uses user/schema interchangeably

        /// <inheritdoc/>
        public override ConstraintsLevelAction ConstraintsLevelAction => ConstraintsLevelAction.OnTableLevel;

        /// <inheritdoc/>
        public override string GetShortProviderTypeName() => ShortProviderType;

        /// <inheritdoc/>
        public override string GetDatabaseName() => this.builder?.DataSource ?? string.Empty;

        /// <summary>
        /// Gets or sets the Metadata object which parses Oracle types.
        /// </summary>
        public override DbMetadata GetMetadata()
        {
            this.dbMetadata ??= new OracleDbMetadata();

            return this.dbMetadata;
        }

        /// <summary>
        /// Gets a chance to make a retry connection.
        /// </summary>
        public override bool ShouldRetryOn(Exception exception)
        {
            Exception ex = exception;
            while (ex != null)
            {
                if (ex is OracleException)
                    return OracleTransientExceptionDetector.ShouldRetryOn(ex);
                else
                    ex = ex.InnerException;
            }

            return false;
        }

        /// <inheritdoc/>
        public override void EnsureSyncException(SyncException syncException)
        {
            if (this.builder != null && !string.IsNullOrEmpty(this.builder.ConnectionString))
            {
                syncException.DataSource = this.builder.DataSource;
            }

            // Can add more info from OracleException
            if (syncException.InnerException is not OracleException oracleException)
                return;

            syncException.Number = oracleException.Number;

            return;
        }

        /// <summary>
        /// Gets a value indicating whether Oracle can be a server side provider.
        /// </summary>
        public override bool CanBeServerProvider => true;

        /// <inheritdoc/>
        public override DbScopeBuilder GetScopeBuilder(string scopeInfoTableName) => new OracleScopeBuilder(scopeInfoTableName);

        /// <inheritdoc/>
        public override DbSyncAdapter GetSyncAdapter(SyncTable tableDescription, ScopeInfo scopeInfo)
            => new OracleSyncAdapter(tableDescription, scopeInfo, this.UseBulkOperations);

        /// <inheritdoc/>
        public override DbDatabaseBuilder GetDatabaseBuilder() => new OracleDatabaseBuilder();
    }
} 