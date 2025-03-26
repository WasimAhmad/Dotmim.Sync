using Dotmim.Sync.Manager;
using Oracle.ManagedDataAccess.Client;
using System;
using System.Collections.Generic;
using System.Data;
using System.Data.Common;
using System.Linq;
using System.Threading.Tasks;
using Dotmim.Sync.Builders;

namespace Dotmim.Sync.Oracle.Manager
{
    /// <summary>
    /// The Oracle metadata class provides a way to get schemas, tables, and columns
    /// </summary>
    public class OracleDbMetadata : DbMetadata
    {
        // Define mappings between Oracle and .NET types
        private readonly Dictionary<string, string> oracleToNetTypeMap = new(StringComparer.OrdinalIgnoreCase)
        {
            { "VARCHAR2", "String" },
            { "NVARCHAR2", "String" },
            { "CHAR", "String" },
            { "NCHAR", "String" },
            { "CLOB", "String" },
            { "NCLOB", "String" },
            { "NUMBER", "Decimal" },
            { "FLOAT", "Double" },
            { "BINARY_FLOAT", "Single" },
            { "BINARY_DOUBLE", "Double" },
            { "DATE", "DateTime" },
            { "TIMESTAMP", "DateTime" },
            { "TIMESTAMP WITH TIME ZONE", "DateTimeOffset" },
            { "TIMESTAMP WITH LOCAL TIME ZONE", "DateTime" },
            { "INTERVAL YEAR TO MONTH", "String" },
            { "INTERVAL DAY TO SECOND", "TimeSpan" },
            { "RAW", "Byte[]" },
            { "LONG RAW", "Byte[]" },
            { "BLOB", "Byte[]" },
            { "BFILE", "Byte[]" },
            { "ROWID", "String" },
            { "UROWID", "String" },
            { "XMLType", "String" }
        };

        /// <summary>
        /// Validate if a column definition is actually supported by the provider.
        /// </summary>
        public override bool IsValid(SyncColumn columnDefinition)
        {
            // Most data types are supported in Oracle
            // We'll check for a few known unsupported types
            var typeName = columnDefinition.GetDbType().ToString().ToUpperInvariant();
            
            // Oracle doesn't support some SQL Server specific types
            return typeName != "HIERARCHYID" && 
                   typeName != "GEOGRAPHY" && 
                   typeName != "GEOMETRY" && 
                   typeName != "XML";
        }

        /// <summary>
        /// Gets and validate a max length issued from the database definition.
        /// </summary>
        public override int GetMaxLength(SyncColumn columnDefinition)
        {
            var typeName = columnDefinition.GetDbType().ToString().ToUpperInvariant();
            var maxLength = columnDefinition.MaxLength;
            
            switch (typeName)
            {
                case "VARCHAR2":
                case "NVARCHAR2":
                    // Oracle VARCHAR2 has a max size of 4000 bytes for single-byte character set
                    // and 2000 characters for multi-byte character sets
                    return maxLength <= 0 ? 4000 : Math.Min(maxLength, 4000);
                case "CHAR":
                case "NCHAR":
                    // CHAR has a max size of 2000 bytes
                    return maxLength <= 0 ? 1 : Math.Min(maxLength, 2000);
                case "RAW":
                    // RAW has a max size of 2000 bytes
                    return maxLength <= 0 ? 2000 : Math.Min(maxLength, 2000);
                default:
                    return maxLength;
            }
        }

        /// <summary>
        /// Get the native datastore DbType
        /// </summary>
        public override object GetOwnerDbType(SyncColumn columnDefinition)
        {
            var typeName = columnDefinition.GetDbType().ToString().ToUpperInvariant();
            
            switch (typeName)
            {
                case "VARCHAR2":
                    return OracleDbType.Varchar2;
                case "NVARCHAR2":
                    return OracleDbType.NVarchar2;
                case "CHAR":
                    return OracleDbType.Char;
                case "NCHAR":
                    return OracleDbType.NChar;
                case "NUMBER":
                    if (columnDefinition.Scale > 0)
                        return OracleDbType.Decimal;
                    if (columnDefinition.Precision == 1)
                        return OracleDbType.Byte;
                    if (columnDefinition.Precision <= 4)
                        return OracleDbType.Int16;
                    if (columnDefinition.Precision <= 9)
                        return OracleDbType.Int32;
                    if (columnDefinition.Precision <= 19)
                        return OracleDbType.Int64;
                    return OracleDbType.Decimal;
                case "FLOAT":
                case "BINARY_DOUBLE":
                    return OracleDbType.Double;
                case "BINARY_FLOAT":
                    return OracleDbType.Single;
                case "DATE":
                    return OracleDbType.Date;
                case "TIMESTAMP":
                    return OracleDbType.TimeStamp;
                case "TIMESTAMP WITH TIME ZONE":
                    return OracleDbType.TimeStampTZ;
                case "TIMESTAMP WITH LOCAL TIME ZONE":
                    return OracleDbType.TimeStampLTZ;
                case "INTERVAL YEAR TO MONTH":
                    return OracleDbType.IntervalYM;
                case "INTERVAL DAY TO SECOND":
                    return OracleDbType.IntervalDS;
                case "RAW":
                    return OracleDbType.Raw;
                case "LONG RAW":
                case "BLOB":
                    return OracleDbType.Blob;
                case "CLOB":
                    return OracleDbType.Clob;
                case "NCLOB":
                    return OracleDbType.NClob;
                case "XMLTYPE":
                    return OracleDbType.XmlType;
                default:
                    return OracleDbType.Varchar2;
            }
        }

        /// <summary>
        /// Get a DbType from a datastore type name.
        /// </summary>
        public override DbType GetDbType(SyncColumn columnDefinition)
        {
            var typeName = columnDefinition.GetDbType().ToString().ToUpperInvariant();
            
            switch (typeName)
            {
                case "VARCHAR2":
                case "NVARCHAR2":
                case "CHAR":
                case "NCHAR":
                case "CLOB":
                case "NCLOB":
                    return DbType.String;
                case "NUMBER":
                    if (columnDefinition.Scale > 0)
                        return DbType.Decimal;
                    if (columnDefinition.Precision == 1)
                        return DbType.Boolean;
                    if (columnDefinition.Precision <= 4)
                        return DbType.Int16;
                    if (columnDefinition.Precision <= 9)
                        return DbType.Int32;
                    if (columnDefinition.Precision <= 19)
                        return DbType.Int64;
                    return DbType.Decimal;
                case "FLOAT":
                case "BINARY_DOUBLE":
                    return DbType.Double;
                case "BINARY_FLOAT":
                    return DbType.Single;
                case "DATE":
                case "TIMESTAMP":
                    return DbType.DateTime;
                case "TIMESTAMP WITH TIME ZONE":
                    return DbType.DateTimeOffset;
                case "RAW":
                case "LONG RAW":
                case "BLOB":
                    return DbType.Binary;
                default:
                    return DbType.String;
            }
        }

        /// <summary>
        /// Validate if a column is readonly or not.
        /// </summary>
        public override bool IsReadonly(SyncColumn columnDefinition)
        {
            // In Oracle, typically LOBs and computed columns would be readonly
            var typeName = columnDefinition.GetDbType().ToString().ToUpperInvariant();
            
            // Consider ROWID and UROWID as readonly
            return typeName == "ROWID" || typeName == "UROWID";
        }

        /// <summary>
        /// Check if a type name is a numeric type.
        /// </summary>
        public override bool IsNumericType(SyncColumn columnDefinition)
        {
            var typeName = columnDefinition.GetDbType().ToString().ToUpperInvariant();
            
            return typeName == "NUMBER" || 
                   typeName == "FLOAT" || 
                   typeName == "BINARY_FLOAT" || 
                   typeName == "BINARY_DOUBLE";
        }

        /// <summary>
        /// Check if a type name support scale.
        /// </summary>
        public override bool IsSupportingScale(SyncColumn columnDefinition)
        {
            var typeName = columnDefinition.GetDbType().ToString().ToUpperInvariant();
            
            // In Oracle, NUMBER supports scale
            return typeName == "NUMBER";
        }

        /// <summary>
        /// Get precision and scale from a SyncColumn.
        /// </summary>
        public override (byte Precision, byte Scale) GetPrecisionAndScale(SyncColumn columnDefinition)
        {
            var typeName = columnDefinition.GetDbType().ToString().ToUpperInvariant();
            
            if (typeName == "NUMBER")
            {
                // Ensure precision is within Oracle's limits (max 38)
                byte precision = columnDefinition.Precision;
                if (precision <= 0 || precision > 38)
                    precision = 38;
                
                return (precision, columnDefinition.Scale);
            }
            
            return (0, 0);
        }

        /// <summary>
        /// Get precision if supported (Oracle NUMBER supports precision).
        /// </summary>
        public override byte GetPrecision(SyncColumn columnDefinition)
        {
            var typeName = columnDefinition.GetDbType().ToString().ToUpperInvariant();
            
            if (typeName == "NUMBER")
            {
                // Ensure precision is within Oracle's limits (max 38)
                byte precision = columnDefinition.Precision;
                if (precision <= 0 || precision > 38)
                    precision = 38;
                
                return precision;
            }
            
            return 0;
        }

        /// <summary>
        /// Get a managed type from a datastore dbType.
        /// </summary>
        public override Type GetType(SyncColumn columnDefinition)
        {
            var typeName = columnDefinition.GetDbType().ToString().ToUpperInvariant();
            
            if (oracleToNetTypeMap.TryGetValue(typeName, out var netTypeName))
            {
                switch (netTypeName)
                {
                    case "String":
                        return typeof(string);
                    case "Decimal":
                        return typeof(decimal);
                    case "Double":
                        return typeof(double);
                    case "Single":
                        return typeof(float);
                    case "DateTime":
                        return typeof(DateTime);
                    case "DateTimeOffset":
                        return typeof(DateTimeOffset);
                    case "TimeSpan":
                        return typeof(TimeSpan);
                    case "Byte[]":
                        return typeof(byte[]);
                    default:
                        return typeof(string);
                }
            }
            
            return typeof(string);
        }

        // Helper method for creating Oracle parameters
        public DbParameter CreateOracleParameter(DbCommand command, string columnName, DbType dbType, string typeName, int maxLength, int precision, int scale)
        {
            var parameter = command.CreateParameter();
            parameter.ParameterName = $":{columnName}";
            parameter.DbType = dbType;
            
            // Set length for string or binary parameters
            if ((dbType == DbType.String || dbType == DbType.StringFixedLength || dbType == DbType.AnsiString || dbType == DbType.AnsiStringFixedLength) && maxLength > 0)
                parameter.Size = maxLength;
            
            // Set precision and scale for decimal parameters
            if (dbType == DbType.Decimal && precision > 0)
            {
                parameter.Precision = (byte)precision;
                if (scale > 0)
                    parameter.Scale = (byte)scale;
            }
            
            return parameter;
        }
    }
} 