# Cluster and schema metadata

You can retrieve the cluster topology and the schema metadata information using the C# driver.

After establishing the first connection, the driver retrieves the cluster topology details and exposes these through methods on the `Metadata` class. The `Metadata` instance for a given cluster can be accessed through the `ICluster.Metadata` property.

## Metadata Synchronization

The information mentioned before is kept up to date using Cassandra event notifications.

It's this Metadata synchronization process that computes the internal `TokenMap` which is necessary for [token aware query routing](routing-queries) to work correctly.

By default, this feature is enabled but it can be disabled:

```csharp
var cluster =
   Cluster.Builder()
          .AddContactPoint("127.0.0.1")
          .WithMetadataSyncOptions(new MetadataSyncOptions().SetMetadataSyncEnabled(false))
          .Build();
```

## Retrieving metadata

The following example outputs hosts information about your cluster:

```csharp
foreach (var host in cluster.AllHosts())
{
   Console.WriteLine($"{host.Address}, {host.Datacenter}, {host.Rack}");
}
```

Additionally, the keyspaces information is already loaded into the `Metadata` object, once the client is connected (when matadata synchronization is enabled):

```csharp
foreach (var keyspace in cluster.Metadata.GetKeyspaces())
{
   Console.WriteLine(keyspace);
}
```

To retrieve the definition of a table, use the `Metadata.GetTable()` method:

```csharp
var tableInfo = await cluster.Metadata.GetTableAsync("keyspace", "table").ConfigureAwait(false);
Console.WriteLine($"Table {tableInfo.Name}");
foreach (var c in tableInfo.ColumnsByName)
{
   Console.WriteLine($"Column {c.Value.Name} with type {c.Value.TypeCode}");
}
```

When metadata synchronization is enabled, table metadata is cached on the first request for that specific table and the cache gets evicted whenever schema or topology changes happen that affect the table's keyspace.

## Finding the replicas for a partition key

Sometimes you want to know, ahead of time, which nodes own a given partition — for
example to co-locate work with the data, to build a custom routing scheme, or simply
to inspect the cluster's replica placement. `Metadata.GetReplicas` (also exposed as
`ICluster.GetReplicas`) answers that question.

The result is a collection of `HostShard` values. Each one pairs the replica `Host`
with the `Shard` on that host that owns the partition. (Note the difference - ScyllaDB is sharded per core; against Cassandra the shard is always `0`).

### Recommended overload

```csharp
public ICollection<HostShard> GetReplicas(
    string keyspace, string table, IReadOnlyList<object> partitionKeyValues);
```

Pass the keyspace, the table, and the partition-key column values **in the order they
are declared in the table's partition key**. Example:

```csharp
// CREATE TABLE ks.users (id uuid, name text, PRIMARY KEY (id));
var id = Guid.Parse("f81d4fae-7dec-11d0-a765-00a0c91e6bf6");

foreach (var replica in cluster.Metadata.GetReplicas("ks", "users", new object[] { id }))
{
    Console.WriteLine($"{replica.Host.Address} (shard {replica.Shard})");
}
```

For a composite partition key, list every partition-key column in order:

```csharp
// CREATE TABLE ks.events (day date, region text, seq bigint,
//                         PRIMARY KEY ((day, region), seq));
var replicas = cluster.Metadata.GetReplicas(
    "ks", "events", new object[] { new LocalDate(2026, 7, 15), "eu-central" });
```

Because this overload knows the table, it uses the table's configured partitioner and
supports **tablet-aware** routing — it consults tablet metadata when the keyspace uses tablets. This is the overload you should prefer for all new code.

`keyspace`, `table`, and `partitionKeyValues` must all be non-null, and
`partitionKeyValues` must be non-empty; otherwise an `ArgumentNullException` /
`ArgumentException` is thrown. Keyspace and table names are case-sensitive. A session
must be established before calling this method.

### Legacy, obsolete overloads

```csharp
[Obsolete] public ICollection<HostShard> GetReplicas(byte[] partitionKey);
[Obsolete] public ICollection<HostShard> GetReplicas(string keyspace, byte[] partitionKey);
```

These accept an already-serialized partition key (in routing-key format) rather than
per-column values. They are kept only for backward compatibility and are marked
`[Obsolete]`, because they cannot be table-aware: they **always** use the Murmur3
partitioner and cannot perform tablet-aware routing. When no keyspace is supplied, the result falls back to the single primary token owner. It is discouraged to use this overload.

There is no dedicated public serializer for the `byte[]` routing-key format. If you must use these overloads, the practical way to obtain the bytes is to reuse a routing key the driver already computed, e.g. `prepared.Bind(values).RoutingKey.RawRoutingKey`. Prefer migrating to the `(keyspace, table, partitionKeyValues)` overload instead.

## Schema agreement

Schema changes need to be propagated to all nodes in the cluster. Once they have settled on a common version, we say that they are in agreement.

The driver waits for schema agreement after executing a schema-altering query. This is to ensure that subsequent requests (which might get routed to different nodes) see an up-to-date version of the schema. **Note that this does not prevent race conditions from concurrent schema changes from different client application instances**. DDL queries should be sent sequentially from a single `ISession` instance.

```ditaa
 Application             Driver           Server
------+--------------------+------------------+-----
      |                    |                  |
      |  CREATE TABLE...   |                  |
      |------------------->|                  |
      |                    |   send request   |
      |                    |----------------->|
      |                    |                  |
      |                    |     success      |
      |                    |<-----------------|
      |                    |                  |
      |          /--------------------\       |
      |          :Wait until all nodes+------>|
      |          :agree (or timeout)  :       |
      |          \--------------------/       |
      |                    |        ^         |
      |                    |        |         |
      |                    |        +---------|
      |                    |                  |
      |                    |  refresh schema  |
      |                    |----------------->|
      |                    |<-----------------|
      |   complete query   |                  |
      |<-------------------|                  |
      |                    |                  |
```

The schema agreement wait is performed when a `SCHEMA_CHANGED` response is received when executing a request. The task returned by `ISession.ExecuteAsync` and other similar methods in Mapper and LINQ components will only be complete after the schema agreement wait (or until the timeout specified with `Builder.WithMaxSchemaAgreementWaitSeconds`). The same applies to synchronous methods like `ISession.Execute`, i.e. they will only return after the schema agreement wait. Note that when the schema agreement wait returns due to a timeout, no exception will be thrown but nodes won't be in agreement.

The check is implemented by repeatedly querying system tables for the schema version reported by each node, until they all converge to the same value. If that doesn't happen within a given timeout, the driver will give up waiting.
The default timeout is `10` seconds, it can be customized when creating the `ICluster` instance:

```csharp
var cluster = 
   Cluster.Builder()
          .AddContactPoint("127.0.0.1")
          .WithMaxSchemaAgreementWaitSeconds(5)
          .Build();
```

After executing a statement, you can check whether schema agreement was successful or timed out:

```csharp
var rowSet = await session.ExecuteAsync("CREATE TABLE table1 (id int PRIMARY KEY)").ConfigureAwait(false);
Console.WriteLine($"Is schema in agreement? {rowSet.Info.IsSchemaInAgreement}");
```

Additionally, you can perform an on-demand check at any time:

```csharp
var isSchemaInAgreement = await session.Cluster.Metadata.CheckSchemaAgreementAsync().ConfigureAwait(false);
```

Note that the on-demand check using `Metadata.CheckSchemaAgreementAsync()` does not retry, it only queries system tables once.
