using System.Globalization;
using System.IO;

namespace MWPFProject_Timer.Sync;

internal sealed class TimerSyncOptions
{
    private const string DeviceNameEnvironmentVariable = "MTIMER_DEVICE_NAME";

    internal const string DefaultPrimaryEndpoint = "http://100.93.235.98:5124";
    internal const string DefaultSecondaryEndpoint = "http://192.168.1.88:5124";

    private static readonly IReadOnlyList<string> BuiltInEndpoints =
    [
        DefaultPrimaryEndpoint,
        DefaultSecondaryEndpoint
    ];

    private TimerSyncOptions(string deviceName)
    {
        DeviceName = deviceName;
    }

    internal IReadOnlyList<string> Endpoints => BuiltInEndpoints;

    internal string DeviceName { get; }

    internal static TimerSyncOptions FromSources(TimerSyncConfiguration? configuration)
    {
        string? configuredDeviceName = string.IsNullOrWhiteSpace(configuration?.DeviceName)
            ? Environment.GetEnvironmentVariable(DeviceNameEnvironmentVariable)
            : configuration.DeviceName;
        configuredDeviceName ??= Environment.MachineName;
        return new TimerSyncOptions(
            NormalizeDeviceName(configuredDeviceName, rejectTooLong: false));
    }

    internal static TimerSyncOptions CreateForVerification(string deviceName) =>
        new(NormalizeDeviceName(deviceName, rejectTooLong: false));

    internal static bool TryCreateFromUserInput(
        string? deviceName,
        out TimerSyncOptions? options,
        out string errorMessage)
    {
        try
        {
            options = new TimerSyncOptions(
                NormalizeDeviceName(deviceName, rejectTooLong: true));
            errorMessage = string.Empty;
            return true;
        }
        catch (InvalidDataException exception)
        {
            options = null;
            errorMessage = exception.Message;
            return false;
        }
    }

    internal static string NormalizeDeviceName(string? value) =>
        NormalizeDeviceName(value, rejectTooLong: false);

    private static string NormalizeDeviceName(string? value, bool rejectTooLong)
    {
        string normalized = value?.Trim() ?? string.Empty;
        var info = new StringInfo(normalized);
        if (info.LengthInTextElements == 0)
        {
            if (rejectTooLong)
            {
                throw new InvalidDataException("电脑简称不能为空。");
            }

            return "PC";
        }

        if (rejectTooLong && info.LengthInTextElements > 2)
        {
            throw new InvalidDataException("电脑简称只能包含 1–2 个文字。");
        }

        return info.SubstringByTextElements(0, Math.Min(2, info.LengthInTextElements));
    }
}

internal sealed record TimerDeviceIdentity(string DeviceId, string DeviceName);
