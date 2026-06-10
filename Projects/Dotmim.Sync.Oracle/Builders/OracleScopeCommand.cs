using Oracle.ManagedDataAccess.Client;
using System.Data;
using System.Data.Common;
using System.Threading;
using System.Threading.Tasks;

namespace Dotmim.Sync.Oracle.Builders
{
    /// <summary>
    /// Wraps an <see cref="OracleCommand"/> used for scope commands.
    /// <para>
    /// The orchestrator converts scope parameter values based on <c>parameter.DbType</c>
    /// (no provider hook), and ODP.NET's <c>OracleDbType.Clob</c> reports
    /// <c>DbType.Object</c> — which the framework converter routes to a base64/byte[]
    /// path that throws for JSON strings. So large-text parameters are declared
    /// <c>DbType.String</c> (Varchar2), and just before execution this wrapper promotes
    /// any string parameter whose value exceeds <see cref="ClobPromotionThreshold"/>
    /// characters to <c>OracleDbType.Clob</c> (VARCHAR2 binds are limited to ~4K/32K;
    /// ODP.NET keeps the already-assigned value when the type changes).
    /// </para>
    /// </summary>
    internal sealed class OracleScopeCommand : DbCommand
    {
        internal const int ClobPromotionThreshold = 4000;

        private readonly OracleCommand inner;

        public OracleScopeCommand(OracleCommand inner) => this.inner = inner;

        /// <inheritdoc/>
        public override string CommandText { get => this.inner.CommandText; set => this.inner.CommandText = value; }

        /// <inheritdoc/>
        public override int CommandTimeout { get => this.inner.CommandTimeout; set => this.inner.CommandTimeout = value; }

        /// <inheritdoc/>
        public override CommandType CommandType { get => this.inner.CommandType; set => this.inner.CommandType = value; }

        /// <inheritdoc/>
        public override bool DesignTimeVisible { get => this.inner.DesignTimeVisible; set => this.inner.DesignTimeVisible = value; }

        /// <inheritdoc/>
        public override UpdateRowSource UpdatedRowSource { get => this.inner.UpdatedRowSource; set => this.inner.UpdatedRowSource = value; }

        /// <inheritdoc/>
        protected override DbConnection DbConnection { get => this.inner.Connection; set => this.inner.Connection = (OracleConnection)value; }

        /// <inheritdoc/>
        protected override DbParameterCollection DbParameterCollection => this.inner.Parameters;

        /// <inheritdoc/>
        protected override DbTransaction DbTransaction { get => this.inner.Transaction; set => this.inner.Transaction = (OracleTransaction)value; }

        /// <summary>
        /// Promotes string parameters holding values longer than
        /// <see cref="ClobPromotionThreshold"/> to CLOB binds. Runs after the framework
        /// has converted and assigned every value, so the DbType change is invisible to it.
        /// </summary>
        internal static void PromoteLargeStringsToClob(OracleParameterCollection parameters)
        {
            foreach (OracleParameter parameter in parameters)
            {
                if (parameter.OracleDbType == OracleDbType.Varchar2 && parameter.Value is string value && value.Length > ClobPromotionThreshold)
                    parameter.OracleDbType = OracleDbType.Clob;
            }
        }

        /// <inheritdoc/>
        public override void Cancel() => this.inner.Cancel();

        /// <inheritdoc/>
        public override void Prepare() => this.inner.Prepare();

        /// <inheritdoc/>
        protected override DbParameter CreateDbParameter() => this.inner.CreateParameter();

        /// <inheritdoc/>
        public override int ExecuteNonQuery()
        {
            PromoteLargeStringsToClob(this.inner.Parameters);
            return this.inner.ExecuteNonQuery();
        }

        /// <inheritdoc/>
        public override Task<int> ExecuteNonQueryAsync(CancellationToken cancellationToken)
        {
            PromoteLargeStringsToClob(this.inner.Parameters);
            return this.inner.ExecuteNonQueryAsync(cancellationToken);
        }

        /// <inheritdoc/>
        public override object ExecuteScalar()
        {
            PromoteLargeStringsToClob(this.inner.Parameters);
            return this.inner.ExecuteScalar();
        }

        /// <inheritdoc/>
        public override Task<object> ExecuteScalarAsync(CancellationToken cancellationToken)
        {
            PromoteLargeStringsToClob(this.inner.Parameters);
            return this.inner.ExecuteScalarAsync(cancellationToken);
        }

        /// <inheritdoc/>
        protected override DbDataReader ExecuteDbDataReader(CommandBehavior behavior)
        {
            PromoteLargeStringsToClob(this.inner.Parameters);
            return this.inner.ExecuteReader(behavior);
        }

        /// <inheritdoc/>
        protected override async Task<DbDataReader> ExecuteDbDataReaderAsync(CommandBehavior behavior, CancellationToken cancellationToken)
        {
            PromoteLargeStringsToClob(this.inner.Parameters);
            return await this.inner.ExecuteReaderAsync(behavior, cancellationToken).ConfigureAwait(false);
        }

        /// <inheritdoc/>
        protected override void Dispose(bool disposing)
        {
            if (disposing)
                this.inner.Dispose();

            base.Dispose(disposing);
        }
    }
}
