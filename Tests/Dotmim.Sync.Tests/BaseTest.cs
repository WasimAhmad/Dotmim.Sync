﻿using Dotmim.Sync.Tests.Fixtures;
using Microsoft.Data.SqlClient;
using MySqlConnector;
using Npgsql;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Reflection;
using System.Text;
using Xunit;
using Xunit.Abstractions;
using Xunit.Sdk;
using System.Linq;
using System.Threading.Tasks;

namespace Dotmim.Sync.Tests
{
    public class BaseTest<T> : IClassFixture<DatabaseServerFixture<T>>, IDisposable where T : RelationalFixture
    {
        public DatabaseServerFixture<T> Fixture { get; }
        public ITestOutputHelper Output { get; }
        public XunitTest Test { get; }
        public Stopwatch Stopwatch { get; }
        
        private Stopwatch initializeStopwatch;
        public BaseTest(ITestOutputHelper output, DatabaseServerFixture<T> fixture)
        {
            this.Fixture = fixture;
            // Getting the test running
            this.Output = output;
            var type = output.GetType();
            var testMember = type.GetField("test", BindingFlags.Instance | BindingFlags.NonPublic);
            this.Test = (XunitTest)testMember.GetValue(output);

            initializeStopwatch = Stopwatch.StartNew();

            SqlConnection.ClearAllPools();
            MySqlConnection.ClearAllPools();
            NpgsqlConnection.ClearAllPools();

            Fixture.DropAllTablesAsync(Fixture.GetServerProvider(), false).GetAwaiter().GetResult();

            foreach (var clientProvider in Fixture.GetClientProviders())
                Fixture.DropAllTablesAsync(clientProvider, true).GetAwaiter().GetResult();

            initializeStopwatch.Stop();

            this.Stopwatch = Stopwatch.StartNew();
        }

        

        public void OutputCurrentState(string subCategory = null)
        {
            var t = string.IsNullOrEmpty(subCategory) ? "" : $" - {subCategory}";

            //var methodParameters = this.Test.TestCase.Method.GetParameters().ToList();
            //var parameters = new StringBuilder();
            //if (methodParameters != null && methodParameters.Count > 0)
            //    foreach (ReflectionParameterInfo methodParameter in methodParameters)
            //        parameters.Append($"{methodParameter.Name}:{methodParameter.ParameterInfo.DefaultValue}. ");

            var preparationTime = $"[Prework :{this.initializeStopwatch.Elapsed.Minutes}:{this.initializeStopwatch.Elapsed.Seconds}.{this.initializeStopwatch.Elapsed.Milliseconds}]";

            t = $"{this.Test.TestCase.Method.Name}{t}: {preparationTime} - {this.Stopwatch.Elapsed.Minutes}:{this.Stopwatch.Elapsed.Seconds}.{this.Stopwatch.Elapsed.Milliseconds}.";
            Console.WriteLine(t);
            Debug.WriteLine(t);
            this.Output.WriteLine(t);
        }

        public void Dispose()
        {
            this.Stopwatch.Stop();

            OutputCurrentState();
        }

    }
}
