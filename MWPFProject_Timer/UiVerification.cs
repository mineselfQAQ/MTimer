using System.Globalization;
using System.IO;
using System.Text;
using System.Text.Json;

namespace MWPFProject_Timer;

internal enum UiVerificationScenario
{
    Calendar,
    LongTermTasks,
    Statistics
}

internal sealed record UiVerificationRequest(
    UiVerificationScenario Scenario,
    string DataRoot,
    string OutputPath)
{
    internal static bool IsRequested(string[] args) =>
        args.Any(argument => string.Equals(argument, "--verify-ui", StringComparison.OrdinalIgnoreCase));

    internal static UiVerificationRequest Parse(string[] args)
    {
        if (args.Length != 6 ||
            !string.Equals(args[0], "--verify-ui", StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(args[2], "--data-root", StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(args[4], "--output", StringComparison.OrdinalIgnoreCase) ||
            !Enum.TryParse(args[1], ignoreCase: true, out UiVerificationScenario scenario))
        {
            throw new ArgumentException(
                "验证参数必须为 --verify-ui <Calendar|LongTermTasks|Statistics> --data-root <目录> --output <PNG路径>。");
        }

        return new UiVerificationRequest(
            scenario,
            Path.GetFullPath(args[3]),
            Path.GetFullPath(args[5]));
    }
}

internal static class UiVerificationFixture
{
    internal static readonly DateTime BusinessDate = new(2026, 7, 22, 12, 0, 0);

    internal static void Write(TimerDataPaths dataPaths)
    {
        Directory.CreateDirectory(dataPaths.RootDirectory);

        LongTermTask longTermTask = LongTermTask.CreateLongTermTask();
        longTermTask.Id = "ui-verification-long-task";
        longTermTask.Name = "阅读";
        longTermTask.ProgressMode = "Children";
        longTermTask.SubTasks.Add(new LongTermSubTask
        {
            Id = "ui-verification-reading-unity",
            Name = "Unity 某项目",
            ProgressPercent = 35
        });
        longTermTask.SubTasks.Add(new LongTermSubTask
        {
            Id = "ui-verification-reading-wpf",
            Name = "WPF 源码阅读",
            ProgressPercent = 70
        });
        longTermTask.DefaultPlannedHours = 2;

        Dictionary<string, DailyEntry> entries = new()
        {
            [BusinessDate.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)] = new DailyEntry
            {
                TotalPlannedMinutes = 180,
                ActualMinutes = 95,
                Tasks = new List<PlanTask>
                {
                    new()
                    {
                        Name = "算法题状态验证",
                        TrackProblemNumbers = true,
                        TimerMode = "CountUp",
                        ProblemNumberEntries = new List<ProblemNumberEntry>
                        {
                            new() { Value = "AC-01", IsCorrect = true },
                            new() { Value = "Near-02", IsNeedsImprovement = true },
                            new() { Value = "WA-03", IsCorrect = false },
                            new() { Value = "Legacy-04", IsCorrect = null }
                        }
                    },
                    new()
                    {
                        Name = "阅读",
                        LongTermTaskId = longTermTask.Id,
                        PlannedHours = 2,
                        ActualMinutes = 75
                    },
                    new()
                    {
                        Name = "整理每日计划",
                        PlannedHours = 1,
                        ActualMinutes = 20
                    }
                }
            }
        };

        JsonSerializerOptions options = new()
        {
            WriteIndented = true,
            PropertyNameCaseInsensitive = true
        };

        File.WriteAllText(
            dataPaths.PlanFilePath,
            JsonSerializer.Serialize(entries, options),
            Encoding.UTF8);
        File.WriteAllText(
            dataPaths.LongTaskFilePath,
            JsonSerializer.Serialize(new List<LongTermTask> { longTermTask }, options),
            Encoding.UTF8);
        File.WriteAllText(dataPaths.CountFilePath, "95", Encoding.UTF8);
    }
}
