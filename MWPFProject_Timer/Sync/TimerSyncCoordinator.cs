using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Net.Http;
using MTimer.Sync.Contracts;

namespace MWPFProject_Timer.Sync;

internal sealed record TimerSyncSnapshot(
    string EntityType,
    string EntityId,
    string PayloadJson,
    DateTime SourceUpdatedAtUtc);

internal sealed class TimerSynchronizationResult
{
    internal bool Succeeded { get; init; }

    internal bool WasBusy { get; init; }

    internal string Message { get; init; } = string.Empty;

    internal string? Endpoint { get; init; }

    internal int UploadedCount { get; init; }

    internal int ConflictCount { get; init; }

    internal SyncPullResponse? PullResponse { get; init; }
}

internal sealed class TimerSyncCoordinator
{
    private static readonly TimeSpan HealthTimeout = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(20);

    private TimerSyncOptions _options;
    private readonly TimerSyncStateStore _stateStore;
    private readonly SemaphoreSlim _syncLock = new(1, 1);
    private readonly TimerSyncState _state;

    internal TimerSyncCoordinator(string statePath, TimerSyncOptions options)
    {
        _options = options;
        _stateStore = new TimerSyncStateStore(statePath);
        _state = _stateStore.Load(options.DeviceName);
    }

    internal TimerDeviceIdentity DeviceIdentity => new(_state.DeviceId, _state.DeviceName);

    internal async Task ReconfigureAsync(
        TimerSyncOptions options,
        CancellationToken cancellationToken)
    {
        await _syncLock.WaitAsync(cancellationToken);
        try
        {
            _options = options;
            _state.DeviceName = options.DeviceName;
            _state.KnownDevices[_state.DeviceId] = options.DeviceName;
            if (_state.LastSuccessfulEndpoint is not null &&
                !options.Endpoints.Contains(
                    _state.LastSuccessfulEndpoint,
                    StringComparer.OrdinalIgnoreCase))
            {
                _state.LastSuccessfulEndpoint = null;
            }

            _stateStore.Save(_state);
        }
        finally
        {
            _syncLock.Release();
        }
    }

    internal string ResolveDeviceName(string deviceId)
    {
        if (_state.KnownDevices.TryGetValue(deviceId, out string? deviceName))
        {
            return deviceName;
        }

        return deviceId.Length <= 2 ? deviceId : deviceId[..2];
    }

    internal async Task<TimerSynchronizationResult> SynchronizeAsync(
        IReadOnlyList<TimerSyncSnapshot> snapshots,
        IReadOnlySet<string> deletionAuthoritativeEntityTypes,
        bool waitForActiveSync,
        CancellationToken cancellationToken)
    {
        bool lockAcquired;
        if (waitForActiveSync)
        {
            await _syncLock.WaitAsync(cancellationToken);
            lockAcquired = true;
        }
        else
        {
            lockAcquired = await _syncLock.WaitAsync(0, cancellationToken);
        }

        if (!lockAcquired)
        {
            return new TimerSynchronizationResult { WasBusy = true, Message = "已有同步正在运行" };
        }

        try
        {
            CaptureLocalChanges(snapshots, deletionAuthoritativeEntityTypes);
            Uri? endpoint = await TimerSyncHttpClient.ResolveHealthyEndpointAsync(
                BuildEndpointCandidates(),
                HealthTimeout,
                cancellationToken);
            if (endpoint is null)
            {
                return new TimerSynchronizationResult { Message = "没有可用同步端点" };
            }

            using HttpClient httpClient = TimerSyncHttpClient.CreateClient(endpoint, RequestTimeout);
            var client = new TimerSyncHttpClient(httpClient);
            int uploadCount = _state.PendingChanges.Count;
            int conflictCount = 0;
            if (uploadCount > 0)
            {
                var pushRequest = new SyncPushRequest
                {
                    DeviceId = _state.DeviceId,
                    DeviceName = _state.DeviceName,
                    Changes = _state.PendingChanges.ToList()
                };
                SyncPushResponse pushResponse = await client.PushAsync(pushRequest, cancellationToken);
                if (pushResponse.ProtocolVersion != SyncProtocol.CurrentVersion)
                {
                    throw new InvalidDataException(
                        $"同步协议版本不兼容：客户端 {SyncProtocol.CurrentVersion}，服务端 {pushResponse.ProtocolVersion}。");
                }

                var completedIds = pushResponse.AcceptedOperationIds
                    .Concat(pushResponse.RejectedStaleOperationIds)
                    .ToHashSet();
                _state.PendingChanges.RemoveAll(change => completedIds.Contains(change.OperationId));
                conflictCount = pushResponse.RejectedStaleOperationIds.Count;
                _stateStore.Save(_state);
            }
            else
            {
                await RegisterDeviceAsync(client, cancellationToken);
            }

            long pullAfter = conflictCount > 0 ? 0 : _state.LastServerCursor;
            SyncPullResponse pullResponse = await client.PullAsync(pullAfter, cancellationToken);
            if (pullResponse.ProtocolVersion != SyncProtocol.CurrentVersion)
            {
                throw new InvalidDataException(
                    $"同步协议版本不兼容：客户端 {SyncProtocol.CurrentVersion}，服务端 {pullResponse.ProtocolVersion}。");
            }
            return new TimerSynchronizationResult
            {
                Succeeded = true,
                Message = "同步完成",
                Endpoint = endpoint.GetLeftPart(UriPartial.Authority),
                UploadedCount = uploadCount,
                ConflictCount = conflictCount,
                PullResponse = pullResponse
            };
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return new TimerSynchronizationResult { Message = "同步已取消" };
        }
        catch (Exception exception)
        {
            return new TimerSynchronizationResult { Message = exception.Message };
        }
        finally
        {
            _syncLock.Release();
        }
    }

    internal void CompletePull(TimerSynchronizationResult result)
    {
        if (!result.Succeeded || result.PullResponse is null)
        {
            return;
        }

        foreach (SyncDeviceDescriptor device in result.PullResponse.Devices)
        {
            if (!string.IsNullOrWhiteSpace(device.DeviceId))
            {
                _state.KnownDevices[device.DeviceId] =
                    TimerSyncOptions.NormalizeDeviceName(device.DeviceName);
            }
        }

        foreach (ServerSyncEntity change in result.PullResponse.Changes)
        {
            _state.KnownEntities[BuildEntityKey(change.EntityType, change.EntityId)] =
                new TimerKnownEntityState
                {
                    PayloadHash = change.IsDeleted ? string.Empty : ComputeHash(change.PayloadJson ?? string.Empty),
                    IsDeleted = change.IsDeleted,
                    UpdatedAtUtc = change.UpdatedAtUtc,
                    ClientVersion = change.ClientVersion,
                    DeviceId = change.DeviceId
                };
        }

        _state.LastServerCursor = Math.Max(
            _state.LastServerCursor,
            result.PullResponse.ServerCursor);
        _state.LastSuccessfulEndpoint = result.Endpoint;
        _state.KnownDevices[_state.DeviceId] = _state.DeviceName;
        _stateStore.Save(_state);
    }

    private async Task RegisterDeviceAsync(
        TimerSyncHttpClient client,
        CancellationToken cancellationToken)
    {
        SyncPushResponse response = await client.PushAsync(
            new SyncPushRequest
            {
                DeviceId = _state.DeviceId,
                DeviceName = _state.DeviceName,
                Changes = []
            },
            cancellationToken);
        if (response.ProtocolVersion != SyncProtocol.CurrentVersion)
        {
            throw new InvalidDataException(
                $"同步协议版本不兼容：客户端 {SyncProtocol.CurrentVersion}，服务端 {response.ProtocolVersion}。");
        }
    }

    private IEnumerable<string?> BuildEndpointCandidates()
    {
        if (_state.LastSuccessfulEndpoint is not null &&
            _options.Endpoints.Contains(
                _state.LastSuccessfulEndpoint,
                StringComparer.OrdinalIgnoreCase))
        {
            yield return _state.LastSuccessfulEndpoint;
        }

        foreach (string endpoint in _options.Endpoints)
        {
            yield return endpoint;
        }
    }

    private void CaptureLocalChanges(
        IReadOnlyList<TimerSyncSnapshot> snapshots,
        IReadOnlySet<string> deletionAuthoritativeEntityTypes)
    {
        var currentKeys = new HashSet<string>(StringComparer.Ordinal);
        bool stateChanged = false;
        foreach (TimerSyncSnapshot snapshot in snapshots)
        {
            string key = BuildEntityKey(snapshot.EntityType, snapshot.EntityId);
            currentKeys.Add(key);
            string hash = ComputeHash(snapshot.PayloadJson);
            _state.KnownEntities.TryGetValue(key, out TimerKnownEntityState? known);
            if (known is { IsDeleted: false } && known.PayloadHash == hash)
            {
                continue;
            }

            DateTime updatedAtUtc = known is null && _state.LastServerCursor == 0
                ? ClampInitialTimestamp(snapshot.SourceUpdatedAtUtc)
                : DateTime.UtcNow;
            long version = Math.Max(0, known?.ClientVersion ?? 0) + 1;
            ReplacePendingChange(new SyncEntityChange
            {
                OperationId = Guid.NewGuid(),
                EntityType = snapshot.EntityType,
                EntityId = snapshot.EntityId,
                PayloadJson = snapshot.PayloadJson,
                IsDeleted = false,
                UpdatedAtUtc = updatedAtUtc,
                ClientVersion = version
            });
            _state.KnownEntities[key] = new TimerKnownEntityState
            {
                PayloadHash = hash,
                IsDeleted = false,
                UpdatedAtUtc = updatedAtUtc,
                ClientVersion = version,
                DeviceId = _state.DeviceId
            };
            stateChanged = true;
        }

        foreach ((string key, TimerKnownEntityState known) in _state.KnownEntities.ToList())
        {
            if (known.IsDeleted || currentKeys.Contains(key))
            {
                continue;
            }

            (string entityType, string entityId) = ParseEntityKey(key);
            if (!deletionAuthoritativeEntityTypes.Contains(entityType))
            {
                continue;
            }

            DateTime updatedAtUtc = DateTime.UtcNow;
            long version = Math.Max(0, known.ClientVersion) + 1;
            ReplacePendingChange(new SyncEntityChange
            {
                OperationId = Guid.NewGuid(),
                EntityType = entityType,
                EntityId = entityId,
                IsDeleted = true,
                UpdatedAtUtc = updatedAtUtc,
                ClientVersion = version
            });
            known.PayloadHash = string.Empty;
            known.IsDeleted = true;
            known.UpdatedAtUtc = updatedAtUtc;
            known.ClientVersion = version;
            known.DeviceId = _state.DeviceId;
            stateChanged = true;
        }

        if (stateChanged)
        {
            _stateStore.Save(_state);
        }
    }

    private void ReplacePendingChange(SyncEntityChange replacement)
    {
        _state.PendingChanges.RemoveAll(change =>
            string.Equals(change.EntityType, replacement.EntityType, StringComparison.Ordinal) &&
            string.Equals(change.EntityId, replacement.EntityId, StringComparison.Ordinal));
        _state.PendingChanges.Add(replacement);
    }

    private static DateTime ClampInitialTimestamp(DateTime value)
    {
        DateTime utc = value.Kind switch
        {
            DateTimeKind.Utc => value,
            DateTimeKind.Local => value.ToUniversalTime(),
            _ => DateTime.SpecifyKind(value, DateTimeKind.Utc)
        };
        DateTime now = DateTime.UtcNow;
        return utc > now ? now : utc;
    }

    private static string ComputeHash(string value)
    {
        byte[] bytes = SHA256.HashData(Encoding.UTF8.GetBytes(value));
        return Convert.ToHexString(bytes);
    }

    private static string BuildEntityKey(string entityType, string entityId) =>
        $"{entityType}\n{entityId}";

    private static (string EntityType, string EntityId) ParseEntityKey(string key)
    {
        int separatorIndex = key.IndexOf('\n');
        if (separatorIndex <= 0 || separatorIndex >= key.Length - 1)
        {
            throw new InvalidDataException("同步实体键格式无效。");
        }

        return (key[..separatorIndex], key[(separatorIndex + 1)..]);
    }
}
