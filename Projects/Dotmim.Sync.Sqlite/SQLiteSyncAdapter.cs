﻿using System;
using System.Collections.Generic;
using System.Linq;

using System.Data.Common;
using System.Data;
using Dotmim.Sync.Builders;
using Microsoft.Data.Sqlite;
using System.Threading.Tasks;

namespace Dotmim.Sync.Sqlite
{
    public class SqliteSyncAdapter : DbSyncAdapter
    {
        private SqliteObjectNames sqliteObjectNames;

        public override bool SupportsOutputParameters => false;

        public SqliteSyncAdapter(SyncTable tableDescription, ParserName tableName, ParserName trackingName, SyncSetup setup, string scopeName) : base(tableDescription, setup, scopeName)
        {
            this.sqliteObjectNames = new SqliteObjectNames(this.TableDescription, tableName, trackingName, this.Setup, scopeName);
        }

        public override (DbCommand, bool) GetCommand(DbCommandType commandType, SyncFilter filter = null)
        {
            var command = new SqliteCommand();
            string text;
            text = this.sqliteObjectNames.GetCommandName(commandType, filter);

            // on Sqlite, everything is text :)
            command.CommandType = CommandType.Text;
            command.CommandText = text;

            return (command, false);
        }

        public override DbCommand EnsureCommandParameters(DbCommand command, DbCommandType commandType, DbConnection connection, DbTransaction transaction, SyncFilter filter = null)
        {
            //if (commandType == DbCommandType.InsertRow || commandType == DbCommandType.InsertRows)
            //{
            //    var p = GetParameter(command, "sync_force_write");
            //    if (p != null)
            //        command.Parameters.Remove(p);
            //    p = GetParameter(command, "sync_min_timestamp");
            //    if (p != null)
            //        command.Parameters.Remove(p);
            //}
            return command;
        }

        public override DbCommand EnsureCommandParametersValues(DbCommand command, DbCommandType commandType, DbConnection connection, DbTransaction transaction)
        {

            return command;
        }

        public override Task ExecuteBatchCommandAsync(DbCommand cmd, Guid senderScopeId, IEnumerable<SyncRow> applyRows, SyncTable schemaChangesTable, SyncTable failedRows, long? lastTimestamp, DbConnection connection, DbTransaction transaction = null)
            => throw new NotImplementedException();


        private DbType GetValidDbType(DbType dbType)
        {
            if (dbType == DbType.Time)
                return DbType.String;

            if (dbType == DbType.Object)
                return DbType.String;

            return dbType;
        }
    }
}
