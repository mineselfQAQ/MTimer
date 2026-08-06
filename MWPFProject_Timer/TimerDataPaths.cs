using System.IO;

namespace MWPFProject_Timer;

internal sealed class TimerDataPaths
{
    internal TimerDataPaths(string rootDirectory)
    {
        RootDirectory = Path.GetFullPath(rootDirectory);
        CountFilePath = Path.Combine(RootDirectory, "minute_count.txt");
        PlanFilePath = Path.Combine(RootDirectory, "daily_plans.json");
        LongTaskFilePath = Path.Combine(RootDirectory, "long_tasks.json");
        SyncConfigFilePath = Path.Combine(RootDirectory, "sync_config.json");
        SyncStateFilePath = Path.Combine(RootDirectory, "sync_state.json");
    }

    internal string RootDirectory { get; }

    internal string CountFilePath { get; }

    internal string PlanFilePath { get; }

    internal string LongTaskFilePath { get; }

    internal string SyncConfigFilePath { get; }

    internal string SyncStateFilePath { get; }
}
