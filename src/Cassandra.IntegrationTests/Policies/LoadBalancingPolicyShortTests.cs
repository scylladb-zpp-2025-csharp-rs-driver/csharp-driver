//
//      Copyright (C) DataStax Inc.
//
//   Licensed under the Apache License, Version 2.0 (the "License");
//   you may not use this file except in compliance with the License.
//   You may obtain a copy of the License at
//
//      http://www.apache.org/licenses/LICENSE-2.0
//
//   Unless required by applicable law or agreed to in writing, software
//   distributed under the License is distributed on an "AS IS" BASIS,
//   WITHOUT WARRANTIES OR CONDITIONS OF ANY KIND, either express or implied.
//   See the License for the specific language governing permissions and
//   limitations under the License.
//

using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Threading;
using Cassandra.IntegrationTests.Policies.Util;
using Cassandra.IntegrationTests.TestBase;
using Cassandra.IntegrationTests.TestClusterManagement;
using Cassandra.Serialization;
using Cassandra.Tests;
using NUnit.Framework;
using Assert = NUnit.Framework.Legacy.ClassicAssert;
using CollectionAssert = NUnit.Framework.Legacy.CollectionAssert;

namespace Cassandra.IntegrationTests.Policies.Tests
{
    [TestFixture, Category(TestCategory.Short), Category(TestCategory.RealCluster), Order(1)]
    public class LoadBalancingPolicyShortTests : SharedClusterTest
    {
        public LoadBalancingPolicyShortTests() : base(3, false, new TestClusterOptions { UseVNodes = true })
        {
        }

        /// <summary>
        /// Validate that two sessions connected to the same DC use separate Policy instances
        /// </summary>
        [Test]
        public void TwoSessionsConnectedToSameDcUseSeparatePolicyInstances()
        {
            var builder = ClusterBuilder();

            using (var cluster1 = builder.WithConnectionString($"Contact Points={TestCluster.ClusterIpPrefix}1").Build())
            using (var cluster2 = builder.WithConnectionString($"Contact Points={TestCluster.ClusterIpPrefix}2").Build())
            {
                var session1 = (Session)cluster1.Connect();
                var session2 = (Session)cluster2.Connect();
                Assert.AreNotSame(session1.Policies.LoadBalancingPolicy, session2.Policies.LoadBalancingPolicy, "Load balancing policy instances should be different");
                Assert.AreNotSame(session1.Policies.ReconnectionPolicy, session2.Policies.ReconnectionPolicy, "Reconnection policy instances should be different");
                Assert.AreNotSame(session1.Policies.RetryPolicy, session2.Policies.RetryPolicy, "Retry policy instances should be different");
            }
        }
        /// <summary>
        /// Validate that no hops occur when inserting GUID values into the key using a prepared statement.
        /// The Rust driver calculates the routing key automatically from the prepared statement metadata;
        /// no manual byte-shuffling hint is needed.
        ///
        /// @test_category load_balancing:dc_aware,round_robin
        /// @test_category replication_strategy
        /// </summary>
        [Test, Ignore("Requires QueryTrace which is not yet implemented")]
        public void TokenAware_Guid_NoHops()
        {
            // Setup
            var cluster = GetNewTemporaryCluster(b => b.WithLoadBalancingPolicy(new TokenAwarePolicy(new RoundRobinPolicy())));

            // Test
            var session = cluster.Connect();
            var ks = TestUtils.GetUniqueKeyspaceName().ToLowerInvariant();
            session.Execute($"CREATE KEYSPACE \"{ks}\" WITH replication = {{'class': 'SimpleStrategy', 'replication_factor': 1}}");
            session.ChangeKeyspace(ks);
            session.Execute("CREATE TABLE tbl (k uuid PRIMARY KEY, i int)");
            var ps = session.Prepare("INSERT INTO tbl (k, i) VALUES (?, ?)");
            var traces = new List<QueryTrace>();
            for (var i = 0; i < 10; i++)
            {
                var statement = ps.Bind(Guid.NewGuid(), i).EnableTracing();
                var rs = session.Execute(statement);
                traces.Add(rs.Info.QueryTrace);
            }
            //Check that there weren't any hops
            foreach (var t in traces)
            {
                //The coordinator must be the only one executing the query
                Assert.True(t.Events.All(e => e.Source.ToString() == t.Coordinator.ToString()), "There were trace events from another host for coordinator " + t.Coordinator);
            }
        }

        /// <summary>
        /// Validate that no hops occur when inserting into a composite key with a prepared statement
        /// @test_category load_balancing:token_aware
        /// </summary>
        [Test, Ignore("Requires QueryTrace which is not yet implemented")]
        public void TokenAware_Prepared_Composite_NoHops()
        {
            // Setup
            PolicyTestTools policyTestTools = new PolicyTestTools();
            var cluster = GetNewTemporaryCluster(b => b.WithLoadBalancingPolicy(new TokenAwarePolicy(new RoundRobinPolicy())));

            // Test
            var session = cluster.Connect();
            var ks = TestUtils.GetUniqueKeyspaceName().ToLowerInvariant();
            policyTestTools.CreateSchema(session, 1, ks);
            policyTestTools.TableName = TestUtils.GetUniqueTableName();
            session.Execute($"CREATE TABLE {policyTestTools.TableName} (k1 text, k2 int, i int, PRIMARY KEY ((k1, k2)))");
            Thread.Sleep(1000);
            var ps = session.Prepare($"INSERT INTO {policyTestTools.TableName} (k1, k2, i) VALUES (?, ?, ?)");
            var traces = new List<QueryTrace>();
            for (var i = 0; i < 10; i++)
            {
                var statement = ps.Bind(i.ToString(), i, i).EnableTracing();
                //Routing key is calculated by the driver
                Assert.NotNull(statement.RoutingKey);
                var rs = session.Execute(statement);
                traces.Add(rs.Info.QueryTrace);
            }
            //Check that there weren't any hops
            foreach (var t in traces)
            {
                //The coordinator must be the only one executing the query
                Assert.True(t.Events.All(e => e.Source.ToString() == t.Coordinator.ToString()), "There were trace events from another host for coordinator " + t.Coordinator);
            }
        }

        /// <summary>
        /// Validate that no hops occur when inserting string values via a prepared statement
        ///
        /// @test_category load_balancing:dc_aware,round_robin
        /// @test_category replication_strategy
        /// @test_category prepared_statements
        /// </summary>
        [Test, Ignore("Requires QueryTrace which is not yet implemented")]
        public void TokenAware_BindString_NoHops()
        {
            // Setup
            PolicyTestTools policyTestTools = new PolicyTestTools();
            var cluster = GetNewTemporaryCluster(b => b.WithLoadBalancingPolicy(new TokenAwarePolicy(new RoundRobinPolicy())));

            // Test
            var session = cluster.Connect();
            var ks = TestUtils.GetUniqueKeyspaceName().ToLowerInvariant();
            policyTestTools.CreateSchema(session, 1, ks);
            policyTestTools.TableName = TestUtils.GetUniqueTableName();
            session.Execute($"CREATE TABLE {policyTestTools.TableName} (k text PRIMARY KEY, i int)");
            var ps = session.Prepare($"INSERT INTO {policyTestTools.TableName} (k, i) VALUES (?, ?)");
            var traces = new List<QueryTrace>();
            string key = "value";
            for (var i = 100; i < 140; i++)
            {
                key += (char)i;
                var statement = ps.Bind(key, i).EnableTracing();
                var rs = session.Execute(statement);
                traces.Add(rs.Info.QueryTrace);
            }
            //Check that there weren't any hops
            foreach (var t in traces)
            {
                //The coordinator must be the only one executing the query
                Assert.True(t.Events.All(e => e.Source.ToString() == t.Coordinator.ToString()), "There were trace events from another host for coordinator " + t.Coordinator);
            }
        }

        /// <summary>
        /// Validate that no hops occur when inserting int values via a prepared statement
        ///
        /// @test_category load_balancing:dc_aware,round_robin
        /// @test_category replication_strategy
        /// @test_category prepared_statements
        /// </summary>
        [Test, Ignore("Requires QueryTrace which is not yet implemented")]
        public void TokenAware_BindInt_NoHops()
        {
            // Setup
            PolicyTestTools policyTestTools = new PolicyTestTools();
            var cluster = GetNewTemporaryCluster(b => b.WithLoadBalancingPolicy(new TokenAwarePolicy(new RoundRobinPolicy())));

            // Test
            var session = cluster.Connect();
            var ks = TestUtils.GetUniqueKeyspaceName().ToLowerInvariant();
            policyTestTools.TableName = TestUtils.GetUniqueTableName();
            policyTestTools.CreateSchema(session, 1, ks);
            var traces = new List<QueryTrace>();
            var pstmt = session.Prepare("INSERT INTO " + policyTestTools.TableName + " (k, i) VALUES (?, ?)");
            for (var i = (int)short.MinValue; i < short.MinValue + 40; i++)
            {
                var statement = pstmt
                    .Bind(i, i)
                    .EnableTracing();
                var rs = session.Execute(statement);
                traces.Add(rs.Info.QueryTrace);
            }
            //Check that there weren't any hops
            foreach (var t in traces)
            {
                //The coordinator must be the only one executing the query
                Assert.True(t.Events.All(e => e.Source.ToString() == t.Coordinator.ToString()), "There were trace events from another host for coordinator " + t.Coordinator);
            }
        }

        // TODO: Re-enable TokenAware_VNodes_Test when both QueryTrace and Host.Tokens are implemented.
        // The test verifies:
        //   1. Each host in a vnode cluster owns 256 tokens: Assert.AreEqual(256, cluster.AllHosts().First().Tokens.Count())
        //   2. With a prepared INSERT on a uuid pk, the driver routes to the owning replica with no internal hops (checked via QueryTrace.Events).
        // [Test, TestCase(true), TestCase(false)]
        // public void TokenAware_VNodes_Test(bool metadataSync)
        // {
        //     var cluster = ClusterBuilder()
        //                          .AddContactPoint(TestCluster.InitialContactPoint)
        //                          .WithMetadataSyncOptions(new MetadataSyncOptions().SetMetadataSyncEnabled(metadataSync))
        //                          .Build();
        //     try
        //     {
        //         var session = cluster.Connect();
        //         Assert.AreEqual(256, cluster.AllHosts().First().Tokens.Count());
        //         var ks = TestUtils.GetUniqueKeyspaceName();
        //         session.Execute($"CREATE KEYSPACE \"{ks}\" WITH replication = {{'class': 'SimpleStrategy', 'replication_factor' : 1}}");
        //         session.ChangeKeyspace(ks);
        //         session.Execute("CREATE TABLE tbl1 (id uuid primary key)");
        //         var ps = session.Prepare("INSERT INTO tbl1 (id) VALUES (?)");
        //         var traces = new List<QueryTrace>();
        //         for (var i = 0; i < 10; i++)
        //         {
        //             var id = Guid.NewGuid();
        //             var bound = ps
        //                 .Bind(id)
        //                 .EnableTracing();
        //             var rs = session.Execute(bound);
        //             traces.Add(rs.Info.QueryTrace);
        //         }
        //         //Check that there weren't any hops
        //         foreach (var t in traces)
        //         {
        //             //The coordinator must be the only one executing the query
        //             Assert.True(t.Events.All(e => e.Source.ToString() == t.Coordinator.ToString()), "There were trace events from another host for coordinator " + t.Coordinator);
        //         }
        //     }
        //     finally
        //     {
        //         cluster.Dispose();
        //     }
        // }

        [Test, TestCase(true), TestCase(false), Ignore("Requires Metadata.GetReplicas which is not yet implemented")]
        public void Token_Aware_Uses_Keyspace_From_Statement_To_Determine_Replication(bool metadataSync)
        {
            var cluster = ClusterBuilder()
                                 .AddContactPoint(TestCluster.InitialContactPoint)
                                 .WithMetadataSyncOptions(new MetadataSyncOptions().SetMetadataSyncEnabled(metadataSync))
                                 .Build();
            try
            {
                // Connect without a keyspace
                var session = cluster.Connect();
                var ks = TestUtils.GetUniqueKeyspaceName();
                session.Execute($"CREATE KEYSPACE \"{ks}\" WITH replication = {{'class': 'SimpleStrategy', 'replication_factor' : 2}}");
                session.ChangeKeyspace(ks);
                session.Execute($"CREATE TABLE tbl1 (id uuid primary key)");
                var ps = session.Prepare($"INSERT INTO tbl1 (id) VALUES (?)");
                var id = Guid.NewGuid();
                var coordinators = new HashSet<IPEndPoint>();
                for (var i = 0; i < 20; i++)
                {
                    var rs = session.Execute(ps.Bind(id));
                    coordinators.Add(rs.Info.QueriedHost);
                }
                // There should be exactly 2 different coordinators for a given token
                Assert.AreEqual(metadataSync ? 2 : 1, coordinators.Count);

                // Manually calculate the routing key
                var routingKey = SerializerManager.Default.GetCurrentSerializer().Serialize(id);
                // Get the replicas
                var replicas = cluster.GetReplicas(ks, routingKey);
                Assert.AreEqual(metadataSync ? 2 : 1, replicas.Count);
                CollectionAssert.AreEquivalent(replicas.Select(h => h.Host.Address), coordinators);
            }
            finally
            {
                cluster.Dispose();
            }
        }

        /// <summary>
        /// With RF=2 and a fixed prepared-statement key, the Rust driver rotates between
        /// both replicas (<c>maybe_shuffled_replicas</c> in the Rust LBP). The test confirms
        /// the driver routes only to replicas — not all 3 nodes — by asserting exactly 2
        /// distinct coordinators appear across many executions.
        /// <para>
        /// Note: this test cannot verify that those 2 coordinators are the <em>correct</em>
        /// replicas for this token; that requires <c>GetReplicas</c>, which is not yet implemented.
        /// </para>
        /// </summary>
        [Test]
        public void TokenAware_RF2_BothReplicasUsedAsCoordinators()
        {
            var cluster = GetNewTemporaryCluster(b => b.WithLoadBalancingPolicy(new TokenAwarePolicy(new RoundRobinPolicy())));
            var session = cluster.Connect();
            var ks = TestUtils.GetUniqueKeyspaceName().ToLowerInvariant();
            session.Execute($"CREATE KEYSPACE \"{ks}\" WITH replication = {{'class': 'SimpleStrategy', 'replication_factor': 2}}");
            session.ChangeKeyspace(ks);
            session.Execute("CREATE TABLE tbl (k int PRIMARY KEY, v int)");

            // Use a fixed key so every execution targets the same token and the same replica set
            var ps = session.Prepare("SELECT v FROM tbl WHERE k = ?");
            var coordinators = new HashSet<IPEndPoint>();
            for (var i = 0; i < 30; i++)
            {
                var rs = session.Execute(ps.Bind(42));
                coordinators.Add(rs.Info.QueriedHost);
            }
            // With RF=2 on a 3-node cluster, token-aware routing must use exactly 2 coordinators.
            // If routing were random (not token-aware) we would see all 3; if broken we would see 1.
            Assert.AreEqual(2, coordinators.Count,
                "With RF=2 and token-aware routing, all queries for the same key must go to exactly the 2 owning replicas");
        }

        /// <summary>
        /// With RF=1, every partition key has exactly one replica, so a token-aware
        /// policy must always route the same key to the same coordinator.
        /// </summary>
        [Test]
        public void TokenAware_SameKey_AlwaysSameCoordinator()
        {
            var cluster = GetNewTemporaryCluster(b => b.WithLoadBalancingPolicy(new TokenAwarePolicy(new RoundRobinPolicy())));
            var session = cluster.Connect();
            var ks = TestUtils.GetUniqueKeyspaceName().ToLowerInvariant();
            session.Execute($"CREATE KEYSPACE \"{ks}\" WITH replication = {{'class': 'SimpleStrategy', 'replication_factor': 1}}");
            session.ChangeKeyspace(ks);
            session.Execute("CREATE TABLE tbl (k int PRIMARY KEY, v int)");
            session.Execute("INSERT INTO tbl (k, v) VALUES (42, 1)");

            var ps = session.Prepare("SELECT v FROM tbl WHERE k = ?");
            var coordinators = new HashSet<IPEndPoint>();
            for (var i = 0; i < 10; i++)
            {
                var rs = session.Execute(ps.Bind(42));
                coordinators.Add(rs.Info.QueriedHost);
            }
            Assert.AreEqual(1, coordinators.Count, "With RF=1, the same partition key must always route to the same coordinator");
        }

        /// <summary>
        /// With RF=1 and 3 nodes, 100 distinct partition keys should be distributed
        /// across more than one coordinator.
        /// </summary>
        [Test]
        public void TokenAware_DifferentKeys_MultipleCoordinators()
        {
            var cluster = GetNewTemporaryCluster(b => b.WithLoadBalancingPolicy(new TokenAwarePolicy(new RoundRobinPolicy())));
            var session = cluster.Connect();
            var ks = TestUtils.GetUniqueKeyspaceName().ToLowerInvariant();
            session.Execute($"CREATE KEYSPACE \"{ks}\" WITH replication = {{'class': 'SimpleStrategy', 'replication_factor': 1}}");
            session.ChangeKeyspace(ks);
            session.Execute("CREATE TABLE tbl (k int PRIMARY KEY, v int)");
            var ps = session.Prepare("INSERT INTO tbl (k, v) VALUES (?, ?)");
            for (var i = 0; i < 100; i++)
                session.Execute(ps.Bind(i, i));

            var selectPs = session.Prepare("SELECT v FROM tbl WHERE k = ?");
            var coordinators = new HashSet<IPEndPoint>();
            for (var i = 0; i < 100; i++)
            {
                var rs = session.Execute(selectPs.Bind(i));
                coordinators.Add(rs.Info.QueriedHost);
            }
            Assert.Greater(coordinators.Count, 1, "100 distinct partition keys across 3 nodes should use multiple coordinators");
        }

        /// <summary>
        /// Validate that DCAwareRoundRobinPolicy with the correct datacenter allows queries to succeed.
        /// Tests that the DC-aware config is correctly translated to the Rust layer.
        /// </summary>
        [Test]
        public void DcAware_CorrectDc_QueriesSucceed()
        {
            var cluster = GetNewTemporaryCluster(b => b.WithLoadBalancingPolicy(new DCAwareRoundRobinPolicy("datacenter1")));
            var session = cluster.Connect();
            var ks = TestUtils.GetUniqueKeyspaceName().ToLowerInvariant();
            session.Execute($"CREATE KEYSPACE \"{ks}\" WITH replication = {{'class': 'SimpleStrategy', 'replication_factor': 1}}");
            session.ChangeKeyspace(ks);
            session.Execute("CREATE TABLE tbl (k int PRIMARY KEY, v int)");
            session.Execute("INSERT INTO tbl (k, v) VALUES (1, 42)");
            var rs = session.Execute("SELECT v FROM tbl WHERE k = 1");
            Assert.AreEqual(42, rs.First().GetValue<int>("v"));
        }

        /// <summary>
        /// Validate that TokenAwarePolicy wrapping RoundRobinPolicy allows queries to succeed.
        /// Tests that token-aware config is correctly translated to the Rust layer.
        /// </summary>
        [Test]
        public void TokenAware_RoundRobin_QueriesSucceed()
        {
            var cluster = GetNewTemporaryCluster(b => b.WithLoadBalancingPolicy(new TokenAwarePolicy(new RoundRobinPolicy())));
            var session = cluster.Connect();
            var ks = TestUtils.GetUniqueKeyspaceName().ToLowerInvariant();
            session.Execute($"CREATE KEYSPACE \"{ks}\" WITH replication = {{'class': 'SimpleStrategy', 'replication_factor': 1}}");
            session.ChangeKeyspace(ks);
            session.Execute("CREATE TABLE tbl (k int PRIMARY KEY, v int)");
            for (var i = 0; i < 5; i++)
                session.Execute($"INSERT INTO tbl (k, v) VALUES ({i}, {i * 10})");
            var count = session.Execute("SELECT COUNT(*) FROM tbl").First().GetValue<long>(0);
            Assert.AreEqual(5, count);
        }

        /// <summary>
        /// Validate that an explicit RoundRobinPolicy allows queries to succeed.
        /// Tests that the round-robin config is correctly translated to the Rust layer.
        /// </summary>
        [Test]
        public void RoundRobin_QueriesSucceed()
        {
            var cluster = GetNewTemporaryCluster(b => b.WithLoadBalancingPolicy(new RoundRobinPolicy()));
            var session = cluster.Connect();
            var ks = TestUtils.GetUniqueKeyspaceName().ToLowerInvariant();
            session.Execute($"CREATE KEYSPACE \"{ks}\" WITH replication = {{'class': 'SimpleStrategy', 'replication_factor': 1}}");
            session.ChangeKeyspace(ks);
            session.Execute("CREATE TABLE tbl (k int PRIMARY KEY, v int)");
            for (var i = 0; i < 5; i++)
                session.Execute($"INSERT INTO tbl (k, v) VALUES ({i}, {i * 10})");
            var count = session.Execute("SELECT COUNT(*) FROM tbl").First().GetValue<long>(0);
            Assert.AreEqual(5, count);
        }
    }

    /// <summary>
    /// DC-aware and token-aware + DC-aware load balancing tests.
    /// Uses a separate 2-DC cluster (1 node per DC) and must run after
    /// <see cref="LoadBalancingPolicyShortTests"/> to avoid destroying its shared cluster.
    /// </summary>
    [TestFixture, Category(TestCategory.Short), Category(TestCategory.RealCluster), Order(2)]
    public class LoadBalancingPolicyMultiDcTests : SharedClusterTest
    {
        public LoadBalancingPolicyMultiDcTests()
            : base(1, false, new TestClusterOptions { Dc2NodeLength = 1 }) { }

        /// <summary>
        /// With a 2-DC cluster, DCAwareRoundRobinPolicy should route all queries to
        /// the local DC while it is available.
        /// </summary>
        [Test]
        public void DcAware_AllQueriesGoToLocalDc()
        {
            var localDcNode = TestCluster.ClusterIpPrefix + "1";
            var cluster = GetNewTemporaryCluster(b => b.WithLoadBalancingPolicy(new DCAwareRoundRobinPolicy("dc1")));
            var session = cluster.Connect();
            var ks = TestUtils.GetUniqueKeyspaceName().ToLowerInvariant();
            session.Execute($"CREATE KEYSPACE \"{ks}\" WITH replication = {{'class': 'SimpleStrategy', 'replication_factor': 1}}");
            session.ChangeKeyspace(ks);
            session.Execute("CREATE TABLE tbl (k int PRIMARY KEY, v int)");
            session.Execute("INSERT INTO tbl (k, v) VALUES (1, 42)");

            var coordinators = new HashSet<IPEndPoint>();
            for (var i = 0; i < 20; i++)
            {
                var rs = session.Execute("SELECT v FROM tbl WHERE k = 1");
                coordinators.Add(rs.Info.QueriedHost);
            }
            Assert.IsTrue(
                coordinators.All(ep => ep.Address.ToString() == localDcNode),
                $"All queries should go to the local DC node ({localDcNode}), but got: {string.Join(", ", coordinators)}");
        }

        /// <summary>
        /// With a 2-DC cluster, TokenAwarePolicy wrapping DCAwareRoundRobinPolicy should
        /// keep all coordinators inside the local DC.
        /// </summary>
        [Test]
        public void TokenAware_DcAware_CoordinatorsStayInLocalDc()
        {
            var localDcNode = TestCluster.ClusterIpPrefix + "1";
            var cluster = GetNewTemporaryCluster(b => b.WithLoadBalancingPolicy(new TokenAwarePolicy(new DCAwareRoundRobinPolicy("dc1"))));
            var session = cluster.Connect();
            var ks = TestUtils.GetUniqueKeyspaceName().ToLowerInvariant();
            session.Execute($"CREATE KEYSPACE \"{ks}\" WITH replication = {{'class': 'SimpleStrategy', 'replication_factor': 1}}");
            session.ChangeKeyspace(ks);
            session.Execute("CREATE TABLE tbl (k int PRIMARY KEY, v int)");
            var ps = session.Prepare("INSERT INTO tbl (k, v) VALUES (?, ?)");
            for (var i = 0; i < 20; i++)
                session.Execute(ps.Bind(i, i));

            var selectPs = session.Prepare("SELECT v FROM tbl WHERE k = ?");
            var coordinators = new HashSet<IPEndPoint>();
            for (var i = 0; i < 20; i++)
            {
                var rs = session.Execute(selectPs.Bind(i));
                coordinators.Add(rs.Info.QueriedHost);
            }
            Assert.IsTrue(
                coordinators.All(ep => ep.Address.ToString() == localDcNode),
                $"All queries should stay in the local DC ({localDcNode}), but got: {string.Join(", ", coordinators)}");
        }
    }
}
