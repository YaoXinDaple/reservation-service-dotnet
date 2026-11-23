using Cassandra;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using ReservationService.Cassandra;

namespace ReservationService.Api;

public sealed class DatabaseInitializer(
    CassandraDatabase database,
    CassandraReservationRepository repository,
    ILogger<DatabaseInitializer> logger) : IHostedService
{
    public async Task StartAsync(CancellationToken cancellationToken)
    {
        logger.LogInformation("Initializing Cassandra schema and prepared statements");
        await database.InitializeAsync();
        await repository.InitializeAsync();
        logger.LogInformation("Cassandra is ready");
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}

public sealed class CassandraHealthCheck(CassandraDatabase database) : IHealthCheck
{
    public async Task<HealthCheckResult> CheckHealthAsync(
        HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        try
        {
            await database.Session.ExecuteAsync(new SimpleStatement("SELECT release_version FROM system.local"))
                .WaitAsync(cancellationToken);
            return HealthCheckResult.Healthy();
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            return HealthCheckResult.Unhealthy("Cassandra is unavailable", exception);
        }
    }
}
