using Cassandra;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using ReservationService.Cassandra;

namespace ReservationService.IntegrationTests;

// Connects to the real local Docker Cassandra. Never silently skips a missing database.
public sealed class CassandraApiFixture : WebApplicationFactory<Program>, IAsyncLifetime
{
    private readonly string _keyspace = "rs_test_" + Guid.NewGuid().ToString("N");
    public HttpClient Client { get; private set; } = null!;

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Development");
        builder.ConfigureAppConfiguration((_, configuration) => configuration.AddInMemoryCollection(
            new Dictionary<string, string?> { ["Cassandra:Keyspace"] = _keyspace }));
    }

    public async Task InitializeAsync()
    {
        Client = CreateClient();
        (await Client.GetAsync("/health")).EnsureSuccessStatusCode();
    }

    async Task IAsyncLifetime.DisposeAsync()
    {
        try
        {
            if (!_keyspace.StartsWith("rs_test_", StringComparison.Ordinal))
                throw new InvalidOperationException("Refusing to drop a non-test keyspace.");
            await Services.GetRequiredService<CassandraDatabase>().Session
                .ExecuteAsync(new SimpleStatement($"DROP KEYSPACE IF EXISTS {_keyspace}"));
        }
        finally
        {
            Client?.Dispose();
            await base.DisposeAsync();
        }
    }
}
