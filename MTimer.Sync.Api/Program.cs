using MTimer.Sync.Api.Storage;
using MTimer.Sync.Contracts;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddSingleton(serviceProvider =>
{
    var environment = serviceProvider.GetRequiredService<IHostEnvironment>();
    var configuration = serviceProvider.GetRequiredService<IConfiguration>();
    return new SyncServerStore(ResolveDatabasePath(environment, configuration));
});

var app = builder.Build();

app.MapGet("/health", () => Results.Ok(new
{
    service = "MTimer.Sync.Api",
    status = "ok",
    protocolVersion = SyncProtocol.CurrentVersion,
    serverTimeUtc = DateTime.UtcNow
}));

app.MapPost("/sync/push", async (SyncServerStore store, SyncPushRequest request) =>
{
    if (request.ProtocolVersion != SyncProtocol.CurrentVersion)
    {
        return Results.BadRequest($"Unsupported protocol version: {request.ProtocolVersion}.");
    }

    if (string.IsNullOrWhiteSpace(request.DeviceId) ||
        string.IsNullOrWhiteSpace(request.DeviceName))
    {
        return Results.BadRequest("DeviceId and DeviceName are required.");
    }

    try
    {
        return Results.Ok(await store.PushAsync(request));
    }
    catch (ArgumentException exception)
    {
        return Results.BadRequest(exception.Message);
    }
});

app.MapGet("/sync/pull", async (SyncServerStore store, long after = 0, int protocolVersion = 0) =>
{
    if (protocolVersion != SyncProtocol.CurrentVersion)
    {
        return Results.BadRequest($"Unsupported protocol version: {protocolVersion}.");
    }

    return Results.Ok(await store.PullAsync(Math.Max(0, after)));
});

app.Run();

static string ResolveDatabasePath(IHostEnvironment environment, IConfiguration configuration)
{
    var databasePath = configuration["MTimer:Sync:DatabasePath"] ??
        Environment.GetEnvironmentVariable("MTIMER_SYNC_DATABASE_PATH");
    if (!string.IsNullOrWhiteSpace(databasePath))
    {
        return Path.GetFullPath(databasePath);
    }

    var dataDirectory = configuration["MTimer:Sync:DataDirectory"] ??
        Environment.GetEnvironmentVariable("MTIMER_SYNC_DATA_DIR");
    if (string.IsNullOrWhiteSpace(dataDirectory))
    {
        dataDirectory = Path.Combine(environment.ContentRootPath, "App_Data");
    }

    return Path.GetFullPath(Path.Combine(dataDirectory, "sync.db"));
}
