using Oracle.ManagedDataAccess.Client;
using System;
using System.Collections.Generic;

namespace Dotmim.Sync.Oracle
{
    /// <summary>
    /// Detect transient errors specific to Oracle
    /// </summary>
    public static class OracleTransientExceptionDetector
    {
        // List of Oracle error numbers that are considered transient/recoverable.
        // Note: ORA-17002 (the JDBC-style "IO error" code) is intentionally NOT listed —
        // whether ODP.NET managed surfaces mid-session network drops as 17002 or as
        // 3113/3135 must be confirmed against a live database first (fix-plan Task 16).
        private static readonly HashSet<int> transientErrorNumbers = new HashSet<int>
        {
            // Network errors
            12152, // TNS:unable to send break message
            12157, // TNS:internal network communication error
            12170, // TNS:Connect timeout occurred
            12224, // TNS:no listener
            12225, // TNS:destination host unreachable

            // Connectivity errors
            1033,  // ORACLE initialization or shutdown in progress
            1034,  // ORACLE not available
            1089,  // immediate shutdown in progress - no operations are permitted
            3113,  // end-of-file on communication channel
            3114,  // not connected to ORACLE
            3135,  // connection lost contact

            // Resource errors
            51,    // timeout occurred while waiting for resource
            54,    // resource busy and acquire with NOWAIT specified

            // Concurrency errors
            60,    // deadlock detected while waiting for resource
            2049,  // distributed lock timeout
            8176,  // consistent read failure; rollback data not available
            8177,  // can't serialize access for this transaction

            // Memory errors
            4030,  // out of process memory
            4031,  // unable to allocate bytes of shared memory
            4068   // existing state of packages has been discarded
        };

        /// <summary>
        /// Returns true when the Oracle error number is considered transient.
        /// </summary>
        public static bool IsTransient(int oracleErrorNumber) => transientErrorNumbers.Contains(oracleErrorNumber);

        /// <summary>
        /// Determines whether the specified exception should be retried.
        /// </summary>
        public static bool ShouldRetryOn(Exception ex)
        {
            if (ex is OracleException oracleException)
                return IsTransient(oracleException.Number);

            return false;
        }
    }
} 