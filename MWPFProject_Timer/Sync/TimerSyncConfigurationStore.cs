using System.IO;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;

namespace MWPFProject_Timer.Sync;

internal sealed class TimerSyncConfigurationStore
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        WriteIndented = true
    };

    private readonly string _configurationPath;

    internal TimerSyncConfigurationStore(string configurationPath)
    {
        _configurationPath = Path.GetFullPath(configurationPath);
    }

    internal TimerSyncConfigurationLoadResult Load()
    {
        try
        {
            TimerSyncConfiguration? configuration = null;
            if (File.Exists(_configurationPath))
            {
                string json = File.ReadAllText(_configurationPath, Encoding.UTF8);
                configuration = JsonSerializer.Deserialize<TimerSyncConfiguration>(json, JsonOptions) ??
                    throw new InvalidDataException("同步配置内容为空。");
            }

            return new TimerSyncConfigurationLoadResult(
                TimerSyncOptions.FromSources(configuration),
                ErrorMessage: null);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException or InvalidDataException)
        {
            return new TimerSyncConfigurationLoadResult(
                TimerSyncOptions.FromSources(configuration: null),
                $"sync_config.json 无法读取：{exception.Message}");
        }
    }

    internal void Save(TimerSyncOptions options)
    {
        string? directory = Path.GetDirectoryName(_configurationPath);
        if (!string.IsNullOrWhiteSpace(directory))
        {
            Directory.CreateDirectory(directory);
        }

        var configuration = new TimerSyncConfiguration
        {
            DeviceName = options.DeviceName
        };
        string temporaryPath = $"{_configurationPath}.{Guid.NewGuid():N}.tmp";
        try
        {
            string json = JsonSerializer.Serialize(configuration, JsonOptions);
            File.WriteAllText(temporaryPath, json, Encoding.UTF8);
            File.Move(temporaryPath, _configurationPath, overwrite: true);
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

internal sealed class TimerSyncConfiguration
{
    public string? DeviceName { get; set; }
}

internal sealed record TimerSyncConfigurationLoadResult(
    TimerSyncOptions Options,
    string? ErrorMessage);
