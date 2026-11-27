using AuroraFileBucket.Server.Data;

namespace AuroraFileBucket.Server.Services;

/// <summary>
/// Creates the SQLite schema before the first request is served. Registered as the first
/// hosted service so the database always exists when the receiver starts.
/// </summary>
public sealed class DatabaseInitializer : IHostedService
{
    private readonly AuroraDatabase _database;
    private readonly ILogger<DatabaseInitializer> _logger;

    public DatabaseInitializer(AuroraDatabase database, ILogger<DatabaseInitializer> logger)
    {
        _database = database;
        _logger = logger;
    }

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        await _database.InitializeAsync(cancellationToken);
        _logger.LogInformation("The Aurora database is ready.");
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
