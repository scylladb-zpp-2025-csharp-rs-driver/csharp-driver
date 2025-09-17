namespace Cassandra.Connections
{
    /// <summary>
    /// Represents Scylla connection options as sent in SUPPORTED
    /// frame.
    /// </summary>
    public class ShardingInfo
    {
        public int ScyllaShard { get; }
        public int ScyllaNrShards { get; }
        public string ScyllaPartitioner { get; }
        public string ScyllaShardingAlgorithm { get; }
        public long ScyllaShardingIgnoreMSB { get; }
        public int ScyllaShardAwarePort { get; }
        public int ScyllaShardAwarePortSSL { get; }

        private ShardingInfo(int scyllaShard, int scyllaNrShards, string scyllaPartitioner,
                         string scyllaShardingAlgorithm, long scyllaShardingIgnoreMSB,
                         int scyllaShardAwarePort, int scyllaShardAwarePortSSL)
        {
            ScyllaShard = scyllaShard;
            ScyllaNrShards = scyllaNrShards;
            ScyllaPartitioner = scyllaPartitioner;
            ScyllaShardingAlgorithm = scyllaShardingAlgorithm;
            ScyllaShardingIgnoreMSB = scyllaShardingIgnoreMSB;
            ScyllaShardAwarePort = scyllaShardAwarePort;
            ScyllaShardAwarePortSSL = scyllaShardAwarePortSSL;
        }

        public static ShardingInfo Create(string scyllaShard, string scyllaNrShards, string scyllaPartitioner,
                                        string scyllaShardingAlgorithm, string scyllaShardingIgnoreMSB,
                                        string scyllaShardAwarePort, string scyllaShardAwarePortSSL)
        {
            return new ShardingInfo(
                int.Parse(scyllaShard),
                int.Parse(scyllaNrShards),
                scyllaPartitioner,
                scyllaShardingAlgorithm,
                long.Parse(scyllaShardingIgnoreMSB),
                int.Parse(scyllaShardAwarePort),
                int.Parse(scyllaShardAwarePortSSL)
            );
        }

        public override string ToString()
        {
            return $"ShardingInfo: " +
                $"ScyllaShard={ScyllaShard}, " +
                $"ScyllaNrShards={ScyllaNrShards}, " +
                $"ScyllaPartitioner={ScyllaPartitioner}, " +
                $"ScyllaShardingAlgorithm={ScyllaShardingAlgorithm}, " +
                $"ScyllaShardingIgnoreMSB={ScyllaShardingIgnoreMSB}, " +
                $"ScyllaShardAwarePort={ScyllaShardAwarePort}, " +
                $"ScyllaShardAwarePortSSL={ScyllaShardAwarePortSSL}";
        }
    }
}
