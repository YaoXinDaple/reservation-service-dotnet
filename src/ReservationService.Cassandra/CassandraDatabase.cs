using Cassandra;

namespace ReservationService.Cassandra;

// One Cluster and one long-lived Session per process, never one connection per request.
public sealed class CassandraDatabase(CassandraOptions options) : IDisposable
{
    private ICluster? _cluster;
    private ISession? _session;
    public ISession Session => _session ?? throw new InvalidOperationException("Database not initialized.");

    public async Task InitializeAsync()
    {
        options.Validate();
        _cluster = Cluster.Builder()
            .AddContactPoints(options.ContactPoints)
            .WithPort(options.Port)
            .WithLoadBalancingPolicy(new DCAwareRoundRobinPolicy(options.LocalDatacenter))
            .WithQueryOptions(new QueryOptions().SetConsistencyLevel(ConsistencyLevel.LocalQuorum))
            .WithSocketOptions(new SocketOptions().SetReadTimeoutMillis(12000))
            .Build();
        _session = await _cluster.ConnectAsync();

        // Identifier is validated above; all business values use bound parameters instead.
        await Session.ExecuteAsync(new SimpleStatement($$"""
            CREATE KEYSPACE IF NOT EXISTS {{options.Keyspace}}
            WITH replication = {'class': 'NetworkTopologyStrategy', '{{Escape(options.LocalDatacenter)}}': 1}
            """));
        Session.ChangeKeyspace(options.Keyspace);

        var assembly = typeof(CassandraDatabase).Assembly;
        var resource = assembly.GetManifestResourceNames().Single(n => n.EndsWith("schema.cql"));
        using var reader = new StreamReader(assembly.GetManifestResourceStream(resource)!);
        var schema = await reader.ReadToEndAsync();
        foreach (var statement in schema.Split(';', StringSplitOptions.RemoveEmptyEntries))
        {
            if (!string.IsNullOrWhiteSpace(statement))
                await Session.ExecuteAsync(new SimpleStatement(statement));
        }
    }

    private static string Escape(string value) => value.Replace("'", "''");

    public void Dispose()
    {
        _session?.Dispose();
        _cluster?.Dispose();
    }
}
