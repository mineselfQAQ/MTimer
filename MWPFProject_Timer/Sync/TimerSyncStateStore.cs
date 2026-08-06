using System.IO;
using System.Text;
using System.Text.Json;
using MTimer.Sync.Contracts;

namespace MWPFProject_Timer.Sync;

internal sealed class TimerSyncStateStore
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true
    };

    private readonly string _statePath;

    internal TimerSyncStateStore(string statePath)
    {
        _statePath = Path.GetFullPath(statePath);
    }

    internal TimerSyncState Load(string deviceName)
    {
        TimerSyncState state = new();
        if (File.Exists(_statePath))
        {
            try
            {
                string json = File.ReadAllText(_statePath, Encoding.UTF8);
                state = JsonSerializer.Deserialize<TimerSyncState>(json, JsonOptions) ?? new TimerSyncState();
            }
            catch
            {
                state = new TimerSyncState();
            }
        }

        state.Normalize(deviceName);
        Save(state);
        return state;
    }

    internal void Save(TimerSyncState state)
    {
        string? directory = Path.GetDirectoryName(_statePath);
        if (!string.IsNullOrWhiteSpace(directory))
        {
            Directory.CreateDirectory(directory);
        }

        string temporaryPath = $"{_statePath}.{Guid.NewGuid():N}.tmp";
        try
        {
            string json = JsonSerializer.Serialize(state, JsonOptions);
            File.WriteAllText(temporaryPath, json, Encoding.UTF8);
            File.Move(temporaryPath, _statePath, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporaryPath))
            {
                File.Delete(temporaryPath);
            }
        }
    }
}

internal sealed class TimerSyncState
{
    public string DeviceId { get; set; } = string.Empty;

    public string DeviceName { get; set; } = string.Empty;

    public long LastServerCursor { get; set; }

    public string? LastSuccessfulEndpoint { get; set; }

    public Dictionary<string, TimerKnownEntityState> KnownEntities { get; set; } =
        new(StringComparer.Ordinal);

    public List<SyncEntityChange> PendingChanges { get; set; } = new();

    public Dictionary<string, string> KnownDevices { get; set; } =
        new(StringComparer.Ordinal);

    internal void Normalize(string deviceName)
    {
        if (!Guid.TryParse(DeviceId, out _))
        {
            DeviceId = Guid.NewGuid().ToString("D");
        }

        DeviceName = TimerSyncOptions.NormalizeDeviceName(deviceName);
        LastServerCursor = Math.Max(0, LastServerCursor);
        KnownEntities = new Dictionary<string, TimerKnownEntityState>(
            KnownEntities ?? new Dictionary<string, TimerKnownEntityState>(),
            StringComparer.Ordinal);
        PendingChanges ??= new List<SyncEntityChange>();
        PendingChanges = PendingChanges
            .Where(change => change.OperationId != Guid.Empty &&
                             !string.IsNullOrWhiteSpace(change.EntityType) &&
                             !string.IsNullOrWhiteSpace(change.EntityId))
            .ToList();
        KnownDevices = new Dictionary<string, string>(
            KnownDevices ?? new Dictionary<string, string>(),
            StringComparer.Ordinal)
        {
            [DeviceId] = DeviceName
        };
    }
}

internal sealed class TimerKnownEntityState
{
    public string PayloadHash { get; set; } = string.Empty;

    public bool IsDeleted { get; set; }

    public DateTime UpdatedAtUtc { get; set; }

    public long ClientVersion { get; set; }

    public string DeviceId { get; set; } = string.Empty;
}
