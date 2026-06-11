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


namespace Cassandra
{
    /// <summary>
    ///  A Round-robin load balancing policy.
    /// <para> This policy queries nodes in a random order.
    ///  </para>
    /// <para> This policy is not datacenter aware and will include every known
    ///  Cassandra host in its round robin algorithm. If you use multiple datacenter
    ///  this will be inefficient and you will want to use the
    ///  <see cref="DCAwareRoundRobinPolicy"/> load balancing policy instead.
    /// </para>
    /// </summary>
    public class RoundRobinPolicy : ILoadBalancingPolicy
    {
        public void Initialize(ICluster cluster)
        {
            throw new NotSupportedException(
                "Initialize is not supported. Load balancing is handled by the Rust driver internally.");
        }

        /// <summary>
        ///  Return the HostDistance for the provided host. <p> This policy consider all
        ///  nodes as local. This is generally the right thing to do in a single
        ///  datacenter deployment. If you use multiple datacenter, see
        ///  <link>DCAwareRoundRobinPolicy</link> instead.</p>
        /// </summary>
        /// <param name="host"> the host of which to return the distance of. </param>
        /// <returns>the HostDistance to <c>host</c>.</returns>
        public HostDistance Distance(Host host)
        {
            return HostDistance.Local;
        }

        public IEnumerable<HostShard> NewQueryPlan(string keyspace, IStatement query)
        {
            throw new NotSupportedException(
                "NewQueryPlan is not supported. Query routing is handled by the Rust driver internally.");
        }
    }
}
