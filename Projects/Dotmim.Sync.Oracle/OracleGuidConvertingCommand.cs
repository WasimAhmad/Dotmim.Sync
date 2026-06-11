using Oracle.ManagedDataAccess.Client;
using System;
using System.Collections;
using System.Data;
using System.Data.Common;
using System.Threading;
using System.Threading.Tasks;

namespace Dotmim.Sync.Oracle
{
    /// <summary>
    /// Wraps the select commands (select changes / select row) so that their data readers
    /// surface RAW(16) Guid columns as <see cref="Guid"/> instead of <c>byte[16]</c>.
    /// <para>
    /// ODP.NET materializes RAW columns as byte arrays. The sync framework serializes rows to
    /// the batch files as JSON, where a byte array becomes a base64 string; on deserialization
    /// the value is converted back using the schema column type (Guid), and base64 is not a
    /// parseable Guid format — the conversion failure is swallowed and the value silently
    /// becomes null (a null primary key on the apply side). Converting to Guid at read time
    /// keeps in-memory rows correctly typed, exactly like SQL Server (uniqueidentifier) and
    /// MySQL (char(36)) readers do.
    /// </para>
    /// </summary>
    internal sealed class OracleGuidConvertingCommand : DbCommand
    {
        private readonly OracleCommand innerCommand;
        private readonly SyncTable tableDescription;

        public OracleGuidConvertingCommand(OracleCommand innerCommand, SyncTable tableDescription)
        {
            this.innerCommand = innerCommand ?? throw new ArgumentNullException(nameof(innerCommand));
            this.tableDescription = tableDescription;
        }

        public override string CommandText { get => this.innerCommand.CommandText; set => this.innerCommand.CommandText = value; }

        public override int CommandTimeout { get => this.innerCommand.CommandTimeout; set => this.innerCommand.CommandTimeout = value; }

        public override CommandType CommandType { get => this.innerCommand.CommandType; set => this.innerCommand.CommandType = value; }

        public override bool DesignTimeVisible { get => this.innerCommand.DesignTimeVisible; set => this.innerCommand.DesignTimeVisible = value; }

        public override UpdateRowSource UpdatedRowSource { get => this.innerCommand.UpdatedRowSource; set => this.innerCommand.UpdatedRowSource = value; }

        protected override DbConnection DbConnection
        {
            get => this.innerCommand.Connection;
            set => this.innerCommand.Connection = (OracleConnection)value;
        }

        protected override DbParameterCollection DbParameterCollection => this.innerCommand.Parameters;

        protected override DbTransaction DbTransaction
        {
            get => this.innerCommand.Transaction;
            set => this.innerCommand.Transaction = (OracleTransaction)value;
        }

        public override void Cancel() => this.innerCommand.Cancel();

        public override int ExecuteNonQuery() => this.innerCommand.ExecuteNonQuery();

        public override Task<int> ExecuteNonQueryAsync(CancellationToken cancellationToken) => this.innerCommand.ExecuteNonQueryAsync(cancellationToken);

        public override object ExecuteScalar() => this.innerCommand.ExecuteScalar();

        public override Task<object> ExecuteScalarAsync(CancellationToken cancellationToken) => this.innerCommand.ExecuteScalarAsync(cancellationToken);

        public override void Prepare() => this.innerCommand.Prepare();

        protected override DbParameter CreateDbParameter() => this.innerCommand.CreateParameter();

        protected override DbDataReader ExecuteDbDataReader(CommandBehavior behavior)
            => new OracleGuidConvertingDataReader(this.innerCommand.ExecuteReader(behavior), this.tableDescription);

        protected override async Task<DbDataReader> ExecuteDbDataReaderAsync(CommandBehavior behavior, CancellationToken cancellationToken)
        {
            var reader = await this.innerCommand.ExecuteReaderAsync(behavior, cancellationToken).ConfigureAwait(false);
            return new OracleGuidConvertingDataReader(reader, this.tableDescription);
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
                this.innerCommand.Dispose();

            base.Dispose(disposing);
        }
    }

    /// <summary>
    /// Data reader that converts RAW(16) values to <see cref="Guid"/> for columns the sync
    /// schema declares as Guid. The stored byte order is <see cref="Guid.ToByteArray()"/> order
    /// (the provider's write path guarantees it), which is exactly what <c>new Guid(byte[])</c>
    /// expects, so the conversion round-trips.
    /// </summary>
    internal sealed class OracleGuidConvertingDataReader : DbDataReader
    {
        private readonly DbDataReader inner;
        private readonly SyncTable tableDescription;
        private bool[] guidOrdinals;

        public OracleGuidConvertingDataReader(DbDataReader inner, SyncTable tableDescription)
        {
            this.inner = inner ?? throw new ArgumentNullException(nameof(inner));
            this.tableDescription = tableDescription;
        }

        public override object this[int ordinal] => this.GetValue(ordinal);

        public override object this[string name] => this.GetValue(this.GetOrdinal(name));

        public override int Depth => this.inner.Depth;

        public override int FieldCount => this.inner.FieldCount;

        public override bool HasRows => this.inner.HasRows;

        public override bool IsClosed => this.inner.IsClosed;

        public override int RecordsAffected => this.inner.RecordsAffected;

        public override object GetValue(int ordinal)
        {
            var value = this.inner.GetValue(ordinal);

            return value is byte[] bytes && bytes.Length == 16 && this.IsGuidOrdinal(ordinal) ? new Guid(bytes) : value;
        }

        public override int GetValues(object[] values)
        {
            var count = this.inner.GetValues(values);

            for (var i = 0; i < count; i++)
            {
                if (values[i] is byte[] bytes && bytes.Length == 16 && this.IsGuidOrdinal(i))
                    values[i] = new Guid(bytes);
            }

            return count;
        }

        public override Guid GetGuid(int ordinal) => this.inner.GetValue(ordinal) is byte[] bytes && bytes.Length == 16 ? new Guid(bytes) : this.inner.GetGuid(ordinal);

        public override Type GetFieldType(int ordinal) => this.IsGuidOrdinal(ordinal) ? typeof(Guid) : this.inner.GetFieldType(ordinal);

        public override bool GetBoolean(int ordinal) => this.inner.GetBoolean(ordinal);

        public override byte GetByte(int ordinal) => this.inner.GetByte(ordinal);

        public override long GetBytes(int ordinal, long dataOffset, byte[] buffer, int bufferOffset, int length) => this.inner.GetBytes(ordinal, dataOffset, buffer, bufferOffset, length);

        public override char GetChar(int ordinal) => this.inner.GetChar(ordinal);

        public override long GetChars(int ordinal, long dataOffset, char[] buffer, int bufferOffset, int length) => this.inner.GetChars(ordinal, dataOffset, buffer, bufferOffset, length);

        public override string GetDataTypeName(int ordinal) => this.inner.GetDataTypeName(ordinal);

        public override DateTime GetDateTime(int ordinal) => this.inner.GetDateTime(ordinal);

        public override decimal GetDecimal(int ordinal) => this.inner.GetDecimal(ordinal);

        public override double GetDouble(int ordinal) => this.inner.GetDouble(ordinal);

        public override float GetFloat(int ordinal) => this.inner.GetFloat(ordinal);

        public override short GetInt16(int ordinal) => this.inner.GetInt16(ordinal);

        public override int GetInt32(int ordinal) => this.inner.GetInt32(ordinal);

        public override long GetInt64(int ordinal) => this.inner.GetInt64(ordinal);

        public override string GetName(int ordinal) => this.inner.GetName(ordinal);

        public override int GetOrdinal(string name) => this.inner.GetOrdinal(name);

        public override string GetString(int ordinal) => this.inner.GetString(ordinal);

        public override bool IsDBNull(int ordinal) => this.inner.IsDBNull(ordinal);

        public override Task<bool> IsDBNullAsync(int ordinal, CancellationToken cancellationToken) => this.inner.IsDBNullAsync(ordinal, cancellationToken);

        public override bool NextResult()
        {
            this.guidOrdinals = null;
            return this.inner.NextResult();
        }

        public override async Task<bool> NextResultAsync(CancellationToken cancellationToken)
        {
            this.guidOrdinals = null;
            return await this.inner.NextResultAsync(cancellationToken).ConfigureAwait(false);
        }

        public override bool Read() => this.inner.Read();

        public override Task<bool> ReadAsync(CancellationToken cancellationToken) => this.inner.ReadAsync(cancellationToken);

        public override IEnumerator GetEnumerator() => new DbEnumerator(this, closeReader: false);

        public override void Close() => this.inner.Close();

        protected override void Dispose(bool disposing)
        {
            if (disposing)
                this.inner.Dispose();

            base.Dispose(disposing);
        }

        private bool IsGuidOrdinal(int ordinal)
        {
            this.guidOrdinals ??= this.BuildGuidOrdinalMap();

            return ordinal >= 0 && ordinal < this.guidOrdinals.Length && this.guidOrdinals[ordinal];
        }

        private bool[] BuildGuidOrdinalMap()
        {
            var map = new bool[this.inner.FieldCount];

            if (this.tableDescription == null)
                return map;

            for (var i = 0; i < map.Length; i++)
            {
                var column = this.tableDescription.Columns[this.inner.GetName(i)];
                map[i] = column != null && column.GetDataType() == typeof(Guid);
            }

            return map;
        }
    }
}
