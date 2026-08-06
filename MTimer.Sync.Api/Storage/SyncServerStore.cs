using System.Globalization;
using Microsoft.Data.Sqlite;
using MTimer.Sync.Contracts;

namespace MTimer.Sync.Api.Storage;

public sealed class SyncServerStore
{
    private readonly SemaphoreSlim _initializationLock = new(1, 1);
    private bool _isInitialized;

    public SyncServerStore(string databasePath)
    {
        DatabasePath = Path.GetFullPath(databasePath);
    }

    public string DatabasePath { get; }

    public async Task<SyncPushResponse> PushAsync(SyncPushRequest request)
    {
        await EnsureInitializedAsync();
        await using var connection = await OpenConnectionAsync();
        await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync();

        string deviceId = request.DeviceId.Trim();
        string deviceName = NormalizeDeviceName(request.DeviceName);
        await UpsertDeviceAsync(connection, transaction, deviceId, deviceName);

        var acceptedIds = new List<Guid>();
        var rejectedIds = new List<Guid>();
        foreach (SyncEntityChange change in request.Changes)
        {
            ValidateChange(change);

            bool? previousOutcome = await ReadProcessedOutcomeAsync(
                connection,
                transaction,
                change.OperationId);
            if (previousOutcome is not null)
            {
                (previousOutcome.Value ? acceptedIds : rejectedIds).Add(change.OperationId);
                continue;
            }

            DateTime updatedAtUtc = EnsureUtc(change.UpdatedAtUtc);
            bool accepted = await ShouldAcceptAsync(
                connection,
                transaction,
                change,
                updatedAtUtc,
                deviceId);
            if (accepted)
            {
                long sequence = await AppendChangeSequenceAsync(
                    connection,
                    transaction,
                    change.EntityType,
                    change.EntityId);
                await UpsertEntityAsync(
                    connection,
                    transaction,
                    change,
                    updatedAtUtc,
                    deviceId,
                    sequence);
                acceptedIds.Add(change.OperationId);
            }
            else
            {
                rejectedIds.Add(change.OperationId);
            }

            await RecordProcessedOperationAsync(
                connection,
                transaction,
                change.OperationId,
                accepted);
        }

        await transaction.CommitAsync();
        return new SyncPushResponse
        {
            ProtocolVersion = SyncProtocol.CurrentVersion,
            ServerTimeUtc = DateTime.UtcNow,
            ServerCursor = await GetServerCursorAsync(connection),
            AcceptedOperationIds = acceptedIds,
            RejectedStaleOperationIds = rejectedIds
        };
    }

    public async Task<SyncPullResponse> PullAsync(long after)
    {
        await EnsureInitializedAsync();
        await using var connection = await OpenConnectionAsync();
        await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync();

        long cursor = await GetServerCursorAsync(connection, transaction);
        IReadOnlyList<ServerSyncEntity> changes = await LoadEntitiesAsync(
            connection,
            transaction,
            after,
            cursor);
        IReadOnlyList<SyncDeviceDescriptor> devices = await LoadDevicesAsync(connection, transaction);
        await transaction.CommitAsync();

        return new SyncPullResponse
        {
            ProtocolVersion = SyncProtocol.CurrentVersion,
            ServerTimeUtc = DateTime.UtcNow,
            ServerCursor = cursor,
            Changes = changes,
            Devices = devices
        };
    }

    private async Task EnsureInitializedAsync()
    {
        if (_isInitialized)
        {
            return;
        }

        await _initializationLock.WaitAsync();
        try
        {
            if (_isInitialized)
            {
                return;
            }

            string? directory = Path.GetDirectoryName(DatabasePath);
            if (!string.IsNullOrWhiteSpace(directory))
            {
                Directory.CreateDirectory(directory);
            }

            await using var connection = await OpenConnectionAsync();
            await using var command = connection.CreateCommand();
            command.CommandText = """
                CREATE TABLE IF NOT EXISTS devices (
                    device_id TEXT NOT NULL PRIMARY KEY,
                    display_name TEXT NOT NULL,
                    updated_at_utc TEXT NOT NULL
                );

                CREATE TABLE IF NOT EXISTS change_clock (
                    sequence INTEGER NOT NULL PRIMARY KEY AUTOINCREMENT,
                    entity_type TEXT NOT NULL,
                    entity_id TEXT NOT NULL,
                    created_at_utc TEXT NOT NULL
                );

                CREATE TABLE IF NOT EXISTS sync_entities (
                    entity_type TEXT NOT NULL,
                    entity_id TEXT NOT NULL,
                    payload_json TEXT NULL,
                    is_deleted INTEGER NOT NULL,
                    updated_at_utc TEXT NOT NULL,
                    client_version INTEGER NOT NULL,
                    device_id TEXT NOT NULL,
                    server_sequence INTEGER NOT NULL,
                    PRIMARY KEY (entity_type, entity_id)
                );

                CREATE INDEX IF NOT EXISTS ix_sync_entities_server_sequence
                    ON sync_entities(server_sequence);

                CREATE TABLE IF NOT EXISTS processed_operations (
                    operation_id TEXT NOT NULL PRIMARY KEY,
                    was_accepted INTEGER NOT NULL,
                    processed_at_utc TEXT NOT NULL
                );
                """;
            await command.ExecuteNonQueryAsync();
            _isInitialized = true;
        }
        finally
        {
            _initializationLock.Release();
        }
    }

    private async Task<SqliteConnection> OpenConnectionAsync()
    {
        var builder = new SqliteConnectionStringBuilder
        {
            DataSource = DatabasePath,
            Mode = SqliteOpenMode.ReadWriteCreate
        };
        var connection = new SqliteConnection(builder.ToString());
        await connection.OpenAsync();

        await using var command = connection.CreateCommand();
        command.CommandText = "PRAGMA busy_timeout=10000; PRAGMA journal_mode=WAL; PRAGMA foreign_keys=ON;";
        await command.ExecuteNonQueryAsync();
        return connection;
    }

    private static async Task UpsertDeviceAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string deviceId,
        string deviceName)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO devices (device_id, display_name, updated_at_utc)
            VALUES ($deviceId, $displayName, $updatedAtUtc)
            ON CONFLICT(device_id) DO UPDATE SET
                display_name = excluded.display_name,
                updated_at_utc = excluded.updated_at_utc;
            """;
        command.Parameters.AddWithValue("$deviceId", deviceId);
        command.Parameters.AddWithValue("$displayName", deviceName);
        command.Parameters.AddWithValue("$updatedAtUtc", ToStorage(DateTime.UtcNow));
        await command.ExecuteNonQueryAsync();
    }

    private static async Task<bool?> ReadProcessedOutcomeAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        Guid operationId)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT was_accepted FROM processed_operations WHERE operation_id = $id;";
        command.Parameters.AddWithValue("$id", operationId.ToString("D"));
        object? value = await command.ExecuteScalarAsync();
        return value is null || value is DBNull ? null : Convert.ToInt32(value) != 0;
    }

    private static async Task<bool> ShouldAcceptAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        SyncEntityChange change,
        DateTime updatedAtUtc,
        string deviceId)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT updated_at_utc, client_version, device_id
            FROM sync_entities
            WHERE entity_type = $entityType AND entity_id = $entityId;
            """;
        command.Parameters.AddWithValue("$entityType", change.EntityType);
        command.Parameters.AddWithValue("$entityId", change.EntityId);
        await using var reader = await command.ExecuteReaderAsync();
        if (!await reader.ReadAsync())
        {
            return true;
        }

        DateTime currentUpdatedAtUtc = FromStorage(reader.GetString(0));
        long currentVersion = reader.GetInt64(1);
        string currentDeviceId = reader.GetString(2);
        int timeComparison = updatedAtUtc.CompareTo(currentUpdatedAtUtc);
        if (timeComparison != 0)
        {
            return timeComparison > 0;
        }

        int versionComparison = change.ClientVersion.CompareTo(currentVersion);
        return versionComparison > 0 ||
               (versionComparison == 0 &&
                string.CompareOrdinal(deviceId, currentDeviceId) > 0);
    }

    private static async Task<long> AppendChangeSequenceAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string entityType,
        string entityId)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO change_clock (entity_type, entity_id, created_at_utc)
            VALUES ($entityType, $entityId, $createdAtUtc);
            SELECT last_insert_rowid();
            """;
        command.Parameters.AddWithValue("$entityType", entityType);
        command.Parameters.AddWithValue("$entityId", entityId);
        command.Parameters.AddWithValue("$createdAtUtc", ToStorage(DateTime.UtcNow));
        return Convert.ToInt64(await command.ExecuteScalarAsync(), CultureInfo.InvariantCulture);
    }

    private static async Task UpsertEntityAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        SyncEntityChange change,
        DateTime updatedAtUtc,
        string deviceId,
        long sequence)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO sync_entities (
                entity_type, entity_id, payload_json, is_deleted, updated_at_utc,
                client_version, device_id, server_sequence
            )
            VALUES (
                $entityType, $entityId, $payloadJson, $isDeleted, $updatedAtUtc,
                $clientVersion, $deviceId, $serverSequence
            )
            ON CONFLICT(entity_type, entity_id) DO UPDATE SET
                payload_json = excluded.payload_json,
                is_deleted = excluded.is_deleted,
                updated_at_utc = excluded.updated_at_utc,
                client_version = excluded.client_version,
                device_id = excluded.device_id,
                server_sequence = excluded.server_sequence;
            """;
        command.Parameters.AddWithValue("$entityType", change.EntityType);
        command.Parameters.AddWithValue("$entityId", change.EntityId);
        command.Parameters.AddWithValue("$payloadJson", change.IsDeleted
            ? DBNull.Value
            : change.PayloadJson ?? (object)DBNull.Value);
        command.Parameters.AddWithValue("$isDeleted", change.IsDeleted ? 1 : 0);
        command.Parameters.AddWithValue("$updatedAtUtc", ToStorage(updatedAtUtc));
        command.Parameters.AddWithValue("$clientVersion", Math.Max(1, change.ClientVersion));
        command.Parameters.AddWithValue("$deviceId", deviceId);
        command.Parameters.AddWithValue("$serverSequence", sequence);
        await command.ExecuteNonQueryAsync();
    }

    private static async Task RecordProcessedOperationAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        Guid operationId,
        bool accepted)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO processed_operations (operation_id, was_accepted, processed_at_utc)
            VALUES ($operationId, $wasAccepted, $processedAtUtc);
            """;
        command.Parameters.AddWithValue("$operationId", operationId.ToString("D"));
        command.Parameters.AddWithValue("$wasAccepted", accepted ? 1 : 0);
        command.Parameters.AddWithValue("$processedAtUtc", ToStorage(DateTime.UtcNow));
        await command.ExecuteNonQueryAsync();
    }

    private static async Task<long> GetServerCursorAsync(
        SqliteConnection connection,
        SqliteTransaction? transaction = null)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT COALESCE(MAX(sequence), 0) FROM change_clock;";
        return Convert.ToInt64(await command.ExecuteScalarAsync(), CultureInfo.InvariantCulture);
    }

    private static async Task<IReadOnlyList<ServerSyncEntity>> LoadEntitiesAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        long after,
        long cursor)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT server_sequence, entity_type, entity_id, payload_json, is_deleted,
                   updated_at_utc, client_version, device_id
            FROM sync_entities
            WHERE server_sequence > $after AND server_sequence <= $cursor
            ORDER BY server_sequence;
            """;
        command.Parameters.AddWithValue("$after", Math.Max(0, after));
        command.Parameters.AddWithValue("$cursor", cursor);

        var entities = new List<ServerSyncEntity>();
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            entities.Add(new ServerSyncEntity
            {
                ServerSequence = reader.GetInt64(0),
                EntityType = reader.GetString(1),
                EntityId = reader.GetString(2),
                PayloadJson = reader.IsDBNull(3) ? null : reader.GetString(3),
                IsDeleted = reader.GetInt32(4) != 0,
                UpdatedAtUtc = FromStorage(reader.GetString(5)),
                ClientVersion = reader.GetInt64(6),
                DeviceId = reader.GetString(7)
            });
        }

        return entities;
    }

    private static async Task<IReadOnlyList<SyncDeviceDescriptor>> LoadDevicesAsync(
        SqliteConnection connection,
        SqliteTransaction transaction)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT device_id, display_name FROM devices ORDER BY device_id;";

        var devices = new List<SyncDeviceDescriptor>();
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            devices.Add(new SyncDeviceDescriptor
            {
                DeviceId = reader.GetString(0),
                DeviceName = reader.GetString(1)
            });
        }

        return devices;
    }

    private static void ValidateChange(SyncEntityChange change)
    {
        if (change.OperationId == Guid.Empty ||
            string.IsNullOrWhiteSpace(change.EntityType) ||
            string.IsNullOrWhiteSpace(change.EntityId) ||
            (!change.IsDeleted && string.IsNullOrWhiteSpace(change.PayloadJson)))
        {
            throw new ArgumentException("Sync change is incomplete.");
        }
    }

    private static string NormalizeDeviceName(string value)
    {
        string normalized = value.Trim();
        var info = new StringInfo(normalized);
        if (info.LengthInTextElements == 0)
        {
            return "PC";
        }

        return info.SubstringByTextElements(0, Math.Min(2, info.LengthInTextElements));
    }

    private static DateTime EnsureUtc(DateTime value)
    {
        return value.Kind switch
        {
            DateTimeKind.Utc => value,
            DateTimeKind.Local => value.ToUniversalTime(),
            _ => DateTime.SpecifyKind(value, DateTimeKind.Utc)
        };
    }

    private static string ToStorage(DateTime value) =>
        EnsureUtc(value).ToString("O", CultureInfo.InvariantCulture);

    private static DateTime FromStorage(string value) =>
        DateTime.Parse(value, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind).ToUniversalTime();
}
