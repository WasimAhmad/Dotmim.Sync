using Dotmim.Sync.Manager;
using Oracle.ManagedDataAccess.Client;
using System;
using System.Data;

namespace Dotmim.Sync.Oracle.Manager
{
    /// <summary>
    /// Oracle metadata. Maps a <see cref="SyncColumn"/> (whose managed <see cref="DbType"/> is the
    /// authoritative source) to Oracle native types and back.
    /// </summary>
    public class OracleDbMetadata : DbMetadata
    {
        /// <inheritdoc/>
        public override bool IsValid(SyncColumn columnDefinition)
        {
            switch (columnDefinition.GetDbType())
            {
                case DbType.AnsiString:
                case DbType.AnsiStringFixedLength:
                case DbType.String:
                case DbType.StringFixedLength:
                case DbType.Boolean:
                case DbType.Byte:
                case DbType.SByte:
                case DbType.Int16:
                case DbType.UInt16:
                case DbType.Int32:
                case DbType.UInt32:
                case DbType.Int64:
                case DbType.UInt64:
                case DbType.Single:
                case DbType.Double:
                case DbType.Decimal:
                case DbType.VarNumeric:
                case DbType.Currency:
                case DbType.Date:
                case DbType.DateTime:
                case DbType.DateTime2:
                case DbType.Time:
                case DbType.DateTimeOffset:
                case DbType.Guid:
                case DbType.Binary:
                case DbType.Xml:
                    return true;
                default:
                    return false;
            }
        }

        /// <inheritdoc/>
        public override int GetMaxLength(SyncColumn columnDefinition)
        {
            var maxLength = columnDefinition.MaxLength;

            switch (columnDefinition.GetDbType())
            {
                case DbType.String:
                case DbType.AnsiString:
                    return maxLength <= 0 || maxLength > 4000 ? 0 : maxLength;
                case DbType.StringFixedLength:
                case DbType.AnsiStringFixedLength:
                    return maxLength <= 0 ? 1 : Math.Min(maxLength, 2000);
                case DbType.Binary:
                    return maxLength <= 0 || maxLength > 2000 ? 0 : maxLength;
                default:
                    return 0;
            }
        }

        /// <inheritdoc/>
        public override object GetOwnerDbType(SyncColumn columnDefinition)
        {
            switch (columnDefinition.GetDbType())
            {
                case DbType.AnsiString:
                case DbType.String:
                    return columnDefinition.MaxLength > 0 && columnDefinition.MaxLength <= 4000 ? OracleDbType.Varchar2 : OracleDbType.Clob;
                case DbType.AnsiStringFixedLength:
                case DbType.StringFixedLength:
                    return OracleDbType.Char;
                case DbType.Boolean:
                case DbType.Byte:
                case DbType.SByte:
                case DbType.Int16:
                case DbType.UInt16:
                case DbType.Int32:
                case DbType.UInt32:
                    return OracleDbType.Int32;
                case DbType.Int64:
                case DbType.UInt64:
                    return OracleDbType.Int64;
                case DbType.Single:
                    return OracleDbType.Single;
                case DbType.Double:
                    return OracleDbType.Double;
                case DbType.Decimal:
                case DbType.VarNumeric:
                case DbType.Currency:
                    return OracleDbType.Decimal;
                case DbType.Date:
                    return OracleDbType.Date;
                case DbType.DateTime:
                case DbType.DateTime2:
                case DbType.Time:
                    return OracleDbType.TimeStamp;
                case DbType.DateTimeOffset:
                    return OracleDbType.TimeStampTZ;
                case DbType.Guid:
                    return OracleDbType.Raw;
                case DbType.Binary:
                    return columnDefinition.MaxLength > 0 && columnDefinition.MaxLength <= 2000 ? OracleDbType.Raw : OracleDbType.Blob;
                case DbType.Xml:
                    return OracleDbType.Clob;
                default:
                    return OracleDbType.Varchar2;
            }
        }

        /// <inheritdoc/>
        public override DbType GetDbType(SyncColumn columnDefinition) => columnDefinition.GetDbType();

        /// <inheritdoc/>
        public override bool IsReadonly(SyncColumn columnDefinition) => columnDefinition.IsReadOnly;

        /// <inheritdoc/>
        public override bool IsNumericType(SyncColumn columnDefinition)
        {
            switch (columnDefinition.GetDbType())
            {
                case DbType.Byte:
                case DbType.SByte:
                case DbType.Int16:
                case DbType.UInt16:
                case DbType.Int32:
                case DbType.UInt32:
                case DbType.Int64:
                case DbType.UInt64:
                case DbType.Single:
                case DbType.Double:
                case DbType.Decimal:
                case DbType.VarNumeric:
                case DbType.Currency:
                    return true;
                default:
                    return false;
            }
        }

        /// <inheritdoc/>
        public override bool IsSupportingScale(SyncColumn columnDefinition)
        {
            switch (columnDefinition.GetDbType())
            {
                case DbType.Decimal:
                case DbType.VarNumeric:
                case DbType.Currency:
                case DbType.Single:
                case DbType.Double:
                    return true;
                default:
                    return false;
            }
        }

        /// <inheritdoc/>
        public override (byte Precision, byte Scale) GetPrecisionAndScale(SyncColumn columnDefinition)
        {
            if (!this.IsSupportingScale(columnDefinition))
                return (0, 0);

            var precision = columnDefinition.Precision;
            if (precision <= 0 || precision > 38)
                precision = 38;

            return (precision, columnDefinition.Scale);
        }

        /// <inheritdoc/>
        public override byte GetPrecision(SyncColumn columnDefinition)
        {
            if (!this.IsNumericType(columnDefinition))
                return 0;

            var precision = columnDefinition.Precision;
            if (precision <= 0 || precision > 38)
                precision = 38;

            return precision;
        }

        /// <inheritdoc/>
        public override Type GetType(SyncColumn columnDefinition) => columnDefinition.GetDataType();
    }
}
