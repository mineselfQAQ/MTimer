namespace MTimer.Sync.Contracts;

public static class SyncProtocol
{
    public const int CurrentVersion = 1;
}

public sealed class SyncEntityChange
{
    public Guid OperationId { get; set; }

    public string EntityType { get; set; } = string.Empty;

    public string EntityId { get; set; } = string.Empty;

    public string? PayloadJson { get; set; }

    public bool IsDeleted { get; set; }

    public DateTime UpdatedAtUtc { get; set; }

    public long ClientVersion { get; set; }
}

public sealed class SyncPushRequest
{
    public int ProtocolVersion { get; set; } = SyncProtocol.CurrentVersion;

    public string DeviceId { get; set; } = string.Empty;

    public string DeviceName { get; set; } = string.Empty;

    public IReadOnlyList<SyncEntityChange> Changes { get; set; } = [];
}

public sealed class SyncPushResponse
{
    public int ProtocolVersion { get; set; } = SyncProtocol.CurrentVersion;

    public DateTime ServerTimeUtc { get; set; } = DateTime.UtcNow;

    public long ServerCursor { get; set; }

    public IReadOnlyList<Guid> AcceptedOperationIds { get; set; } = [];

    public IReadOnlyList<Guid> RejectedStaleOperationIds { get; set; } = [];
}

public sealed class SyncPullResponse
{
    public int ProtocolVersion { get; set; } = SyncProtocol.CurrentVersion;

    public DateTime ServerTimeUtc { get; set; } = DateTime.UtcNow;

    public long ServerCursor { get; set; }

    public IReadOnlyList<ServerSyncEntity> Changes { get; set; } = [];

    public IReadOnlyList<SyncDeviceDescriptor> Devices { get; set; } = [];
}

public sealed class ServerSyncEntity
{
    public long ServerSequence { get; set; }

    public string EntityType { get; set; } = string.Empty;

    public string EntityId { get; set; } = string.Empty;

    public string? PayloadJson { get; set; }

    public bool IsDeleted { get; set; }

    public DateTime UpdatedAtUtc { get; set; }

    public long ClientVersion { get; set; }

    public string DeviceId { get; set; } = string.Empty;
}

public sealed class SyncDeviceDescriptor
{
    public string DeviceId { get; set; } = string.Empty;

    public string DeviceName { get; set; } = string.Empty;
}
