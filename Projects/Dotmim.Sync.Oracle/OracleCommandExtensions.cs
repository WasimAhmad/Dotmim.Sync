using Oracle.ManagedDataAccess.Client;
using System.Data.Common;

namespace Dotmim.Sync.Oracle
{
    /// <summary>
    /// Helpers for raw <see cref="DbCommand"/> instances created by the Oracle builders.
    /// </summary>
    internal static class OracleCommandExtensions
    {
        /// <summary>
        /// Forces name-based parameter binding on Oracle commands.
        /// <para>
        /// ODP.NET binds parameters by POSITION by default (<see cref="OracleCommand.BindByName"/> is
        /// <c>false</c>): values are applied to placeholders in the order the parameters were added, not by
        /// matching names. Any command with more than one parameter whose add-order differs from the
        /// placeholder order in the SQL text silently binds the wrong values — the relation-columns lookup in
        /// <c>OracleTableBuilder.GetRelationsAsync</c> swapped the foreign/referenced constraint names this
        /// way, producing inverted foreign keys when the schema was re-created on a remote provider.
        /// </para>
        /// </summary>
        /// <param name="command">The command to fix up; non-Oracle commands are returned unchanged.</param>
        /// <returns>The same command instance, for chaining.</returns>
        public static DbCommand EnsureBindByName(this DbCommand command)
        {
            if (command is OracleCommand oracleCommand)
                oracleCommand.BindByName = true;

            return command;
        }
    }
}
