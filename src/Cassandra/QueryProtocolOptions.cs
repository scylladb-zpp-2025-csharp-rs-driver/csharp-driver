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
using Cassandra.ExecutionProfiles;
using Cassandra.Serialization;

namespace Cassandra
{
    public class QueryProtocolOptions
    {
        //This class was leaked to the API, making it internal would be a breaking change
        [Flags]
        public enum QueryFlags
        {
            Values = 0x01,
            SkipMetadata = 0x02,
            PageSize = 0x04,
            WithPagingState = 0x08,
            WithSerialConsistency = 0x10,
            WithDefaultTimestamp = 0x20,
            WithNameForValues = 0x40,
            WithKeyspace = 0x80
        }

        public static readonly QueryProtocolOptions Default =
            new QueryProtocolOptions(ConsistencyLevel.One, null, false, QueryOptions.DefaultPageSize, null, ConsistencyLevel.Any, null, null, null);

        public readonly int PageSize;
        public readonly ConsistencyLevel SerialConsistency;

        private readonly string _keyspace;

        public bool SkipMetadata { get; }

        public byte[] PagingState { get; set; }

        public object[] Values { get; private set; }

        public ConsistencyLevel Consistency { get; set; }

        public DateTimeOffset? Timestamp
        {
            get
            {
                return RawTimestamp == null ? (DateTimeOffset?)null :
                    TypeSerializer.UnixStart.AddTicks(RawTimestamp.Value * 10);
            }
        }

        internal long? RawTimestamp { get; }

        /// <summary>
        /// Names of the query parameters
        /// </summary>
        public IList<string> ValueNames { get; set; }

        internal RowSetMetadata VariablesMetadata { get; }

        internal string Keyspace => _keyspace;

        internal QueryProtocolOptions(ConsistencyLevel consistency,
                                      object[] values,
                                      bool skipMetadata,
                                      int pageSize,
                                      byte[] pagingState,
                                      ConsistencyLevel serialConsistency,
                                      long? timestamp,
                                      string keyspace,
                                      RowSetMetadata variablesMetadata)
        {
            Consistency = consistency;
            Values = values;
            SkipMetadata = skipMetadata;
            if (pageSize <= 0)
            {
                PageSize = QueryOptions.DefaultPageSize;
            }
            else if (pageSize == int.MaxValue)
            {
                PageSize = -1;
            }
            else
            {
                PageSize = pageSize;
            }
            PagingState = pagingState;
            SerialConsistency = serialConsistency;
            RawTimestamp = timestamp;
            _keyspace = keyspace;
            VariablesMetadata = variablesMetadata;
        }
    }
}