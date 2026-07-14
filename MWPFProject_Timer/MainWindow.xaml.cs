using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;

namespace MWPFProject_Timer;

/// <summary>
/// Interaction logic for MainWindow.xaml
/// </summary>
public partial class MainWindow : Window
{
    private const int INTERVAL_TIME = 1;
    private const double COMPACT_WIDTH = 256;
    private const double COMPACT_HEIGHT = 118;
    private const double EXPANDED_WIDTH = 950;
    private const double EXPANDED_HEIGHT = 650;
    private const string COUNT_FILE = "minute_count.txt";
    private const string PLAN_FILE = "daily_plans.json";
    private const string LONG_TASK_FILE = "long_tasks.json";
    internal const string FREE_STUDY_TASK_NAME = "自由学习";
    private const string UNNAMED_TASK_PREFIX = "未命名任务";
    private const int MONITOR_DEFAULTTONEAREST = 2;

    private static readonly CultureInfo ZhCn = new("zh-CN");
    private static readonly TimeSpan DailyRefreshTime = TimeSpan.FromHours(4);
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true
    };

    private static readonly Brush CompactRootBackground = CreateBrush("#40FFFFFF");
    private static readonly Brush ExpandedRootBackground = CreateBrush("#20262C");
    private static readonly Brush RunningBrush = CreateBrush("#2FB344");
    private static readonly Brush StoppedBrush = CreateBrush("#D84C4C");
    private static readonly Brush IdleBrush = Brushes.White;
    private static readonly Brush RemainingBrush = CreateBrush("#D84C4C");
    private static readonly Brush CompletedBrush = CreateBrush("#2FB344");

    private static readonly Brush CalendarNormalBackground = CreateBrush("#2B343C");
    private static readonly Brush CalendarMutedBackground = CreateBrush("#222A31");
    private static readonly Brush CalendarTodayBackground = CreateBrush("#303A43");
    private static readonly Brush CalendarSelectedBackground = CreateBrush("#FF7A1A");
    private static readonly Brush CalendarNormalBorder = CreateBrush("#34404A");
    private static readonly Brush CalendarMutedBorder = CreateBrush("#2B333B");
    private static readonly Brush CalendarAccentBorder = CreateBrush("#FF7A1A");
    private static readonly Brush CalendarSelectedBorder = CreateBrush("#FFB066");
    private static readonly Brush CalendarNormalText = CreateBrush("#E9EEF2");
    private static readonly Brush CalendarMutedText = CreateBrush("#697580");
    private static readonly Brush CalendarSelectedText = CreateBrush("#1A1E22");
    private static readonly Brush CalendarEntryText = CreateBrush("#FF7A1A");
    private static readonly Brush CalendarEntryDot = CreateBrush("#FF9D45");
    private static readonly Brush CalendarSelectedDot = CreateBrush("#1A1E22");

    private readonly Dictionary<string, DailyEntry> _dailyEntries = new(StringComparer.Ordinal);
    private readonly ObservableCollection<PlanTask> _selectedTasks = new();
    private readonly ObservableCollection<LongTermTask> _longTermTasks = new();
    private readonly ObservableCollection<StatisticsBar> _statisticsBars = new();

    private DispatcherTimer? _minuteTimer;
    private int _minuteCount;
    private double _speedFactor = 1.0;
    private DateTime _currentBusinessDate;
    private DateTime _displayMonth;
    private DateTime _selectedDate;
    private int _currentTaskIndex;
    private bool _isLoadingEntry = true;
    private bool _isLoadingLongTermTasks;
    private bool _isUpdatingTotalPlan;
    private LeftPaneMode _leftPaneMode = LeftPaneMode.Calendar;

    private sealed record FreeTaskTransfer(PlanTask FreeTask, PlanTask TargetTask);

    private enum LeftPaneMode
    {
        Calendar,
        LongTermTasks,
        Statistics
    }

    public MainWindow()
    {
        _currentBusinessDate = GetBusinessDate(DateTime.Now);
        _displayMonth = new DateTime(_currentBusinessDate.Year, _currentBusinessDate.Month, 1);
        _selectedDate = _currentBusinessDate;

        InitializeComponent();
        PlanTaskItems.ItemsSource = _selectedTasks;
        LongTaskItems.ItemsSource = _longTermTasks;
        LongTaskFocusItems.ItemsSource = _longTermTasks;
        StatisticsBarItems.ItemsSource = _statisticsBars;
        InitializeTimer();

        LoadDailyEntries();
        LoadLongTermTasks();
        ApplyLegacyCountToCurrentDate(LoadSavedCount());
        UpdateMinuteCountFromCurrentBusinessDate();
        UpdateCounterDisplay();
        SetIndicator(IdleBrush);
        LoadSelectedEntry();
        RenderCalendar();
        RenderStatistics();
        RefreshCurrentTaskDisplay();
    }

    private void InitializeTimer()
    {
        _minuteTimer = new DispatcherTimer();
        UpdateTimerInterval();
        _minuteTimer.Tick += MinuteTimer_Tick;
    }

    private void MinuteTimer_Tick(object? sender, EventArgs e)
    {
        RefreshBusinessDateIfNeeded();

        DailyEntry entry = GetEntry(_currentBusinessDate, create: true)!;
        entry.ActualMinutes++;
        _minuteCount = entry.ActualMinutes;

        PlanTask? activeTask = GetCurrentTask();
        PlanTask trackedTask = GetTaskForCurrentTick(entry, activeTask);
        bool wasCompleted = trackedTask.IsCompleted;
        trackedTask.ActualMinutes++;
        EnsureSelectedTaskVisible(trackedTask);
        bool progressUpdated = !wasCompleted && trackedTask.IsCompleted && ApplyTaskCompletionProgress(trackedTask, true);

        if (trackedTask.IsFreeTask || IsTaskCompleted(trackedTask))
        {
            SelectCurrentFreeTask();
        }

        UpdateCounterDisplay();
        SaveCurrentCount();
        if (progressUpdated)
        {
            SaveLongTermTasks();
        }
        SaveDailyEntries();
        RenderCalendar();
        RefreshCurrentTaskDisplay();
    }

    private void StartBtn_Click(object sender, RoutedEventArgs e)
    {
        if (_minuteTimer != null && !_minuteTimer.IsEnabled)
        {
            RefreshBusinessDateIfNeeded();
            _speedFactor = 1;
            UpdateTimerInterval();

            if (IsTaskCompleted(GetCurrentTask()))
            {
                SelectCurrentFreeTask();
            }

            _minuteTimer.Start();
            SetIndicator(RunningBrush);
        }
    }

    private void StopBtn_Click(object sender, RoutedEventArgs e)
    {
        if (_minuteTimer != null && _minuteTimer.IsEnabled)
        {
            _minuteTimer.Stop();
            SetIndicator(StoppedBrush);

            SaveCurrentCount();
            SaveDailyEntries();
        }
    }

    private void CompleteCurrentTaskBtn_Click(object sender, RoutedEventArgs e)
    {
        PlanTask? task = GetCurrentTask();
        if (task?.CanCompleteManually != true)
        {
            return;
        }

        CompleteRecurringTask(task);
    }

    private void CompletePlanTaskBtn_Click(object sender, RoutedEventArgs e)
    {
        if (_selectedDate.Date != _currentBusinessDate.Date ||
            sender is not Button { Tag: PlanTask task } ||
            !task.CanCompleteManually)
        {
            return;
        }

        CompleteRecurringTask(task);
    }

    private void ExpandBtn_Click(object sender, RoutedEventArgs e)
    {
        SetExpanded(true);
    }

    private void CollapseBtn_Click(object sender, RoutedEventArgs e)
    {
        SaveSelectedEntry();
        SetExpanded(false);
    }

    private void StatsBtn_Click(object sender, RoutedEventArgs e)
    {
        SetLeftPaneMode(_leftPaneMode == LeftPaneMode.Statistics
            ? LeftPaneMode.Calendar
            : LeftPaneMode.Statistics);
    }

    private void CloseBtn_Click(object sender, RoutedEventArgs e)
    {
        Close();
    }

    private void PrevMonthBtn_Click(object sender, RoutedEventArgs e)
    {
        SaveSelectedEntry();
        _displayMonth = _displayMonth.AddMonths(-1);
        RenderCalendar();
    }

    private void NextMonthBtn_Click(object sender, RoutedEventArgs e)
    {
        SaveSelectedEntry();
        _displayMonth = _displayMonth.AddMonths(1);
        RenderCalendar();
    }

    private void CalendarDay_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: DateTime date })
        {
            return;
        }

        SaveSelectedEntry();
        _selectedDate = date.Date;
        _displayMonth = new DateTime(date.Year, date.Month, 1);
        LoadSelectedEntry();
        RenderCalendar();
    }

    private void AddTaskBtn_Click(object sender, RoutedEventArgs e)
    {
        if (!IsPlanEditable(_selectedDate))
        {
            return;
        }

        PlanTask task = new()
        {
            IsReadOnly = false
        };
        _selectedTasks.Add(task);
        SaveSelectedEntry();
        RenderCalendar();
        RefreshCurrentTaskDisplay();
    }

    private void RemoveTaskBtn_Click(object sender, RoutedEventArgs e)
    {
        if (!IsPlanEditable(_selectedDate) ||
            sender is not Button { Tag: PlanTask task } ||
            task.IsFreeTask)
        {
            return;
        }

        if (task.IsRecurringTask)
        {
            task.SkipOccurrence();
        }
        else
        {
            _selectedTasks.Remove(task);
        }

        SaveSelectedEntry();
        RenderCalendar();
        RenderStatistics();
        LoadSelectedEntry();
        RefreshCurrentTaskDisplay();
    }

    private void TransferFreeTaskBtn_Click(object sender, RoutedEventArgs e)
    {
        if (!IsPlanEditable(_selectedDate) ||
            sender is not Button { Tag: PlanTask freeTask } button ||
            !freeTask.IsFreeTask ||
            freeTask.ActualMinutes <= 0)
        {
            return;
        }

        List<PlanTask> targetTasks = _selectedTasks
            .Where(task => !task.IsFreeTask && !task.IsHidden && !task.IsEmpty)
            .ToList();
        if (targetTasks.Count == 0)
        {
            return;
        }

        ContextMenu menu = new()
        {
            PlacementTarget = button,
            Placement = PlacementMode.Bottom
        };

        for (int i = 0; i < targetTasks.Count; i++)
        {
            PlanTask targetTask = targetTasks[i];
            MenuItem item = new()
            {
                Header = GetTaskDisplayName(targetTask, i),
                Tag = new FreeTaskTransfer(freeTask, targetTask)
            };
            item.Click += TransferFreeTaskMenuItem_Click;
            menu.Items.Add(item);
        }

        button.ContextMenu = menu;
        menu.IsOpen = true;
    }

    private void TransferFreeTaskMenuItem_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not MenuItem { Tag: FreeTaskTransfer transfer } ||
            transfer.FreeTask.ActualMinutes <= 0)
        {
            return;
        }

        transfer.TargetTask.ActualMinutes += transfer.FreeTask.ActualMinutes;
        transfer.FreeTask.ActualMinutes = 0;
        _selectedTasks.Remove(transfer.FreeTask);

        SaveSelectedEntry();
        RenderCalendar();
        RefreshCurrentTaskDisplay();
    }

    private void EnsureSelectedTaskVisible(PlanTask task)
    {
        if (_selectedDate.Date != _currentBusinessDate.Date ||
            task.IsHidden ||
            task.IsEmpty ||
            _selectedTasks.Contains(task))
        {
            return;
        }

        task.IsReadOnly = !IsPlanEditable(_selectedDate);
        _selectedTasks.Add(task);
    }

    private void AddLongTaskBtn_Click(object sender, RoutedEventArgs e)
    {
        _longTermTasks.Add(LongTermTask.CreateLongTermTask());
        SetLeftPaneMode(LeftPaneMode.LongTermTasks);
        SaveLongTermTasks();
    }

    private void ExpandLongTasksBtn_Click(object sender, RoutedEventArgs e)
    {
        SetLeftPaneMode(LeftPaneMode.LongTermTasks);
    }

    private void ReturnToCalendarBtn_Click(object sender, RoutedEventArgs e)
    {
        SetLeftPaneMode(LeftPaneMode.Calendar);
    }

    private void RemoveLongTaskBtn_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: LongTermTask task })
        {
            return;
        }

        _longTermTasks.Remove(task);
        SaveLongTermTasks();
        RenderCalendar();
        RenderStatistics();
    }

    private void DecreaseLongTaskProgressBtn_Click(object sender, RoutedEventArgs e)
    {
        AdjustLongTaskProgress(sender, -1);
    }

    private void IncreaseLongTaskProgressBtn_Click(object sender, RoutedEventArgs e)
    {
        AdjustLongTaskProgress(sender, 1);
    }

    private void AdjustLongTaskProgress(object sender, int delta)
    {
        if (sender is not Button { Tag: LongTermTask task })
        {
            return;
        }

        task.AdjustProgress(delta);
        SaveLongTermTasks();
        RenderStatistics();
    }

    private void AddLongTaskToTodayBtn_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: LongTermTask task } ||
            !task.CanAddToToday)
        {
            return;
        }

        SaveSelectedEntry();

        DateTime targetDate = _currentBusinessDate.Date;
        DailyEntry entry = NormalizeEntry(GetEntry(targetDate, create: true)!);
        PlanTask? existingTask = task.IsRecurringTask
            ? entry.Tasks.FirstOrDefault(item => item.RecurringTaskId == task.Id)
            : entry.Tasks.FirstOrDefault(item => item.LongTermTaskId == task.Id);

        if (existingTask != null)
        {
            if (task.IsRecurringTask)
            {
                existingTask.Name = task.Name.Trim();
                existingTask.IsSkipped = false;
            }
        }
        else
        {
            entry.Tasks.Add(CreatePlanTaskFromLongTerm(task));
        }

        entry.Plan = BuildLegacyPlan(entry.Tasks);

        _selectedDate = targetDate;
        _displayMonth = new DateTime(targetDate.Year, targetDate.Month, 1);
        SaveDailyEntries();
        RenderCalendar();
        LoadSelectedEntry();
        RefreshCurrentTaskDisplay();
    }

    private static PlanTask CreatePlanTaskFromLongTerm(LongTermTask task)
    {
        PlanTask planTask = new()
        {
            Name = task.Name.Trim(),
            LongTermTaskId = task.Id,
            RecurringTaskId = task.IsRecurringTask ? task.Id : string.Empty
        };
        ApplyLongTermTaskSettings(planTask, task);
        return planTask;
    }

    private static void ApplyLongTermTaskSettings(PlanTask planTask, LongTermTask task)
    {
        planTask.LongTermTaskId = task.Id;
        planTask.RecurringTaskId = task.IsRecurringTask ? task.Id : string.Empty;
        planTask.TimerMode = task.TimerMode;
        if (task.IsCountdownTimer)
        {
            planTask.PlannedHours = task.DefaultPlannedHours;
            planTask.PlannedMinutes = task.DefaultPlannedMinutes;
        }
        else
        {
            planTask.PlannedHours = 0;
            planTask.PlannedMinutes = 0;
        }

        if (!planTask.IsProgressCountApplied)
        {
            planTask.ProgressIncrement = task.ProgressMode == "Count" ? task.CompletionIncrement : 0;
        }
    }

    private bool SynchronizeLongTermTaskForDate(LongTermTask task, DateTime date)
    {
        if (string.IsNullOrWhiteSpace(task.Id))
        {
            return false;
        }

        DailyEntry? entry = GetEntry(date, create: false);
        if (entry == null)
        {
            return false;
        }

        bool changed = false;
        foreach (PlanTask planTask in entry.Tasks.Where(item =>
                     item.LongTermTaskId == task.Id || item.RecurringTaskId == task.Id))
        {
            int previousPlannedMinutes = planTask.PlannedTotalMinutes;
            string previousTimerMode = planTask.TimerMode;
            int previousIncrement = planTask.ProgressIncrement;
            string previousLongTermTaskId = planTask.LongTermTaskId;
            string previousRecurringTaskId = planTask.RecurringTaskId;
            ApplyLongTermTaskSettings(planTask, task);
            changed |= previousPlannedMinutes != planTask.PlannedTotalMinutes ||
                       previousTimerMode != planTask.TimerMode ||
                       previousIncrement != planTask.ProgressIncrement ||
                       previousLongTermTaskId != planTask.LongTermTaskId ||
                       previousRecurringTaskId != planTask.RecurringTaskId;
        }

        if (changed)
        {
            entry.Plan = BuildLegacyPlan(entry.Tasks);
        }

        return changed;
    }

    private void PlanTaskTextChanged(object sender, TextChangedEventArgs e)
    {
        if (_isLoadingEntry)
        {
            return;
        }

        if (sender is TextBox textBox)
        {
            textBox.GetBindingExpression(TextBox.TextProperty)?.UpdateSource();
        }

        SaveSelectedEntry();
        RenderCalendar();
        RefreshCurrentTaskDisplay();
    }

    private void LongTaskTextChanged(object sender, TextChangedEventArgs e)
    {
        if (_isLoadingLongTermTasks)
        {
            return;
        }

        if (sender is TextBox textBox)
        {
            textBox.GetBindingExpression(TextBox.TextProperty)?.UpdateSource();
            if (textBox.DataContext is LongTermTask task)
            {
                SynchronizeLongTermTaskForDate(task, _currentBusinessDate);
                if (task.IsRecurringTask)
                {
                    EnsureRecurringTasksForDate(_currentBusinessDate);
                }
            }
        }

        SaveLongTermTasks();
        SaveDailyEntries();
        RenderCalendar();
        RenderStatistics();
        RefreshCurrentTaskDisplay();
    }

    private void LongTaskSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_isLoadingLongTermTasks ||
            sender is not FrameworkElement { DataContext: LongTermTask task })
        {
            return;
        }

        task.Normalize();
        SynchronizeLongTermTaskForDate(task, _currentBusinessDate);
        EnsureRecurringTasksForDate(_currentBusinessDate);
        SaveLongTermTasks();
        SaveDailyEntries();
        RenderCalendar();
        RenderStatistics();
        LoadSelectedEntry();
        RefreshCurrentTaskDisplay();
    }

    private void LongTaskScheduleChanged(object sender, RoutedEventArgs e)
    {
        if (_isLoadingLongTermTasks ||
            sender is not FrameworkElement { DataContext: LongTermTask task })
        {
            return;
        }

        task.Normalize();
        SynchronizeLongTermTaskForDate(task, _currentBusinessDate);
        EnsureRecurringTasksForDate(_currentBusinessDate);
        SaveLongTermTasks();
        SaveDailyEntries();
        RenderCalendar();
        RenderStatistics();
        LoadSelectedEntry();
        RefreshCurrentTaskDisplay();
    }

    private void TotalPlanTextChanged(object sender, TextChangedEventArgs e)
    {
        if (_isLoadingEntry || _isUpdatingTotalPlan)
        {
            return;
        }

        SaveSelectedEntry();
        RenderCalendar();
        RefreshCurrentTaskDisplay();
    }

    private void PlanTaskInput_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter)
        {
            return;
        }

        e.Handled = true;

        if (sender is TextBox textBox)
        {
            textBox.GetBindingExpression(TextBox.TextProperty)?.UpdateSource();
            if (textBox.DataContext is PlanTask task)
            {
                task.NormalizePlannedTime();
            }
        }

        NormalizeTotalPlanInputs();
        RootBorder.Focus();
        SaveSelectedEntry();
        RenderCalendar();
        RefreshCurrentTaskDisplay();
    }

    private void LongTaskInput_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter)
        {
            return;
        }

        e.Handled = true;

        if (sender is TextBox textBox)
        {
            textBox.GetBindingExpression(TextBox.TextProperty)?.UpdateSource();
            if (textBox.DataContext is LongTermTask task)
            {
                task.NormalizeProgress();
            }
        }

        RootBorder.Focus();
        SaveLongTermTasks();
    }

    private void TotalPlanTimeLostFocus(object sender, RoutedEventArgs e)
    {
        if (_isLoadingEntry || _isUpdatingTotalPlan)
        {
            return;
        }

        NormalizeTotalPlanInputs();
        SaveSelectedEntry();
        RenderCalendar();
        RefreshCurrentTaskDisplay();
    }

    private void PlanTaskTimeLostFocus(object sender, RoutedEventArgs e)
    {
        if (_isLoadingEntry ||
            sender is not TextBox { DataContext: PlanTask task } textBox ||
            task.IsInputReadOnly)
        {
            return;
        }

        textBox.GetBindingExpression(TextBox.TextProperty)?.UpdateSource();
        task.NormalizePlannedTime();

        SaveSelectedEntry();
        RenderCalendar();
        RefreshCurrentTaskDisplay();
    }

    private void LongTaskProgressLostFocus(object sender, RoutedEventArgs e)
    {
        if (_isLoadingLongTermTasks ||
            sender is not TextBox { DataContext: LongTermTask task } textBox)
        {
            return;
        }

        textBox.GetBindingExpression(TextBox.TextProperty)?.UpdateSource();
        task.NormalizeProgress();
        SaveLongTermTasks();
    }

    private void LongTaskNote_PreviewMouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (sender is not TextBox { DataContext: LongTermTask task } textBox)
        {
            return;
        }

        task.BeginNoteEdit();
        textBox.IsReadOnly = false;
        textBox.Focus();
        textBox.SelectAll();
        e.Handled = true;
    }

    private void LongTaskNote_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter)
        {
            return;
        }

        e.Handled = true;
        CommitLongTaskNote(sender as TextBox);
        RootBorder.Focus();
    }

    private void LongTaskNote_LostFocus(object sender, RoutedEventArgs e)
    {
        CommitLongTaskNote(sender as TextBox);
    }

    private void CommitLongTaskNote(TextBox? textBox)
    {
        if (_isLoadingLongTermTasks ||
            textBox is not { DataContext: LongTermTask task })
        {
            return;
        }

        textBox.GetBindingExpression(TextBox.TextProperty)?.UpdateSource();
        task.EndNoteEdit();
        SaveLongTermTasks();
    }

    private void TaskUpBtn_Click(object sender, RoutedEventArgs e)
    {
        MoveCurrentTask(-1);
    }

    private void TaskDownBtn_Click(object sender, RoutedEventArgs e)
    {
        MoveCurrentTask(1);
    }

    private void Window_MouseDown(object? sender, MouseButtonEventArgs e)
    {
        if (e.LeftButton == MouseButtonState.Pressed &&
            e.ClickCount == 1 &&
            !IsFromInteractiveElement(e.OriginalSource as DependencyObject))
        {
            DragMove();
        }
    }

    private void Window_Closing(object? sender, CancelEventArgs e)
    {
        SaveSelectedEntry();
        SaveLongTermTasks();
        SaveCurrentCount();
        SaveDailyEntries();
    }

    private void SetExpanded(bool expanded)
    {
        SaveSelectedEntry();
        Point windowCenter = GetWindowCenter();

        CompactPanel.Visibility = expanded ? Visibility.Collapsed : Visibility.Visible;
        ExpandedPanel.Visibility = expanded ? Visibility.Visible : Visibility.Collapsed;
        RootBorder.Background = expanded ? ExpandedRootBackground : CompactRootBackground;

        SetFixedWindowSize(
            expanded ? EXPANDED_WIDTH : COMPACT_WIDTH,
            expanded ? EXPANDED_HEIGHT : COMPACT_HEIGHT);
        PositionWindowAroundCenter(windowCenter);

        if (expanded)
        {
            LoadSelectedEntry();
            RenderCalendar();
            RenderStatistics();
        }
        else
        {
            RefreshCurrentTaskDisplay();
        }
    }

    private void SetLeftPaneMode(LeftPaneMode mode)
    {
        _leftPaneMode = mode;
        CalendarPanel.Visibility = mode == LeftPaneMode.Calendar ? Visibility.Visible : Visibility.Collapsed;
        LongTaskFocusPanel.Visibility = mode == LeftPaneMode.LongTermTasks ? Visibility.Visible : Visibility.Collapsed;
        StatisticsPanel.Visibility = mode == LeftPaneMode.Statistics ? Visibility.Visible : Visibility.Collapsed;

        StatsBtn.ToolTip = mode == LeftPaneMode.Statistics ? "返回日历" : "查看统计";
        StatsBtn.Background = mode == LeftPaneMode.Statistics ? CreateBrush("#3B4650") : CreateBrush("#2E363E");

        if (mode == LeftPaneMode.Statistics)
        {
            RenderStatistics();
        }
    }

    private void CompleteRecurringTask(PlanTask task)
    {
        if (!task.CanCompleteManually)
        {
            return;
        }

        bool isCompleting = !task.IsManuallyCompleted;
        task.ToggleManualCompletion();
        bool progressUpdated = ApplyTaskCompletionProgress(task, isCompleting);
        if (isCompleting && _minuteTimer?.IsEnabled == true)
        {
            _minuteTimer.Stop();
            SetIndicator(StoppedBrush);
        }

        SaveSelectedEntry();
        SaveCurrentCount();
        if (progressUpdated)
        {
            SaveLongTermTasks();
        }
        SaveDailyEntries();
        RenderCalendar();
        RenderStatistics();
        LoadSelectedEntry();
        if (isCompleting)
        {
            SelectCurrentFreeTask();
        }

        RefreshCurrentTaskDisplay();
    }

    private bool ApplyTaskCompletionProgress(PlanTask task, bool isCompleting)
    {
        if (task.ProgressIncrement <= 0)
        {
            return false;
        }

        LongTermTask? longTask = _longTermTasks.FirstOrDefault(item => item.Id == task.LongTermTaskId) ??
            _longTermTasks.FirstOrDefault(item => item.Id == task.RecurringTaskId);
        if (longTask == null || longTask.ProgressMode != "Count")
        {
            return false;
        }

        if (isCompleting)
        {
            if (task.IsProgressCountApplied)
            {
                return false;
            }

            longTask.AdjustProgress(task.ProgressIncrement);
            task.IsProgressCountApplied = true;
            return true;
        }

        if (!task.IsProgressCountApplied)
        {
            return false;
        }

        longTask.AdjustProgress(-task.ProgressIncrement);
        task.IsProgressCountApplied = false;
        return true;
    }

    private void SetFixedWindowSize(double width, double height)
    {
        MinWidth = 0;
        MinHeight = 0;
        MaxWidth = double.PositiveInfinity;
        MaxHeight = double.PositiveInfinity;

        Width = width;
        Height = height;
        MinWidth = width;
        MinHeight = height;
        MaxWidth = width;
        MaxHeight = height;
    }

    private Point GetWindowCenter()
    {
        Rect workArea = SystemParameters.WorkArea;
        double left = double.IsNaN(Left) ? workArea.Left : Left;
        double top = double.IsNaN(Top) ? workArea.Top : Top;
        return new Point(left + (Width / 2), top + (Height / 2));
    }

    private void PositionWindowAroundCenter(Point center)
    {
        Rect workArea = GetWorkAreaForCenter(center);
        double left = center.X - (Width / 2);
        double top = center.Y - (Height / 2);

        Left = ClampWindowCoordinate(left, workArea.Left, workArea.Right - Width);
        Top = ClampWindowCoordinate(top, workArea.Top, workArea.Bottom - Height);
    }

    private Rect GetWorkAreaForCenter(Point center)
    {
        PresentationSource? source = PresentationSource.FromVisual(this);
        Matrix toDevice = source?.CompositionTarget?.TransformToDevice ?? Matrix.Identity;
        Matrix fromDevice = source?.CompositionTarget?.TransformFromDevice ?? Matrix.Identity;
        Point deviceCenter = toDevice.Transform(center);

        NativePoint nativePoint = new()
        {
            X = (int)Math.Round(deviceCenter.X),
            Y = (int)Math.Round(deviceCenter.Y)
        };
        IntPtr monitor = MonitorFromPoint(nativePoint, MONITOR_DEFAULTTONEAREST);
        if (monitor == IntPtr.Zero)
        {
            return SystemParameters.WorkArea;
        }

        MonitorInfo monitorInfo = new()
        {
            Size = Marshal.SizeOf<MonitorInfo>()
        };
        if (!GetMonitorInfo(monitor, ref monitorInfo))
        {
            return SystemParameters.WorkArea;
        }

        Point topLeft = fromDevice.Transform(new Point(monitorInfo.WorkArea.Left, monitorInfo.WorkArea.Top));
        Point bottomRight = fromDevice.Transform(new Point(monitorInfo.WorkArea.Right, monitorInfo.WorkArea.Bottom));
        return new Rect(topLeft, bottomRight);
    }

    private static double ClampWindowCoordinate(double value, double min, double max)
    {
        if (max < min)
        {
            return min;
        }

        return Math.Clamp(value, min, max);
    }

    [DllImport("user32.dll")]
    private static extern IntPtr MonitorFromPoint(NativePoint point, int flags);

    [DllImport("user32.dll", CharSet = CharSet.Auto)]
    private static extern bool GetMonitorInfo(IntPtr monitor, ref MonitorInfo monitorInfo);

    [StructLayout(LayoutKind.Sequential)]
    private struct NativePoint
    {
        public int X;

        public int Y;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Auto)]
    private struct MonitorInfo
    {
        public int Size;

        public NativeRect MonitorArea;

        public NativeRect WorkArea;

        public int Flags;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeRect
    {
        public int Left;

        public int Top;

        public int Right;

        public int Bottom;
    }

    private void RenderCalendar()
    {
        MonthTitle.Text = _displayMonth.ToString("yyyy年M月", ZhCn);

        DateTime firstDay = _displayMonth;
        int mondayOffset = ((int)firstDay.DayOfWeek + 6) % 7;
        DateTime gridStart = firstDay.AddDays(-mondayOffset);

        List<CalendarDayViewModel> days = new();
        for (int i = 0; i < 42; i++)
        {
            DateTime date = gridStart.AddDays(i);
            bool isCurrentMonth = date.Month == _displayMonth.Month && date.Year == _displayMonth.Year;
            bool isSelected = date.Date == _selectedDate.Date;
            bool isToday = date.Date == _currentBusinessDate.Date;
            bool hasEntry = HasEntry(date);
            string timeText = GetCalendarTimeText(date);

            days.Add(CreateCalendarDay(date, isCurrentMonth, isSelected, isToday, hasEntry, timeText));
        }

        CalendarDayItems.ItemsSource = days;
        RenderStatistics();
    }

    private void RenderStatistics()
    {
        DateTime monthStart = new(_displayMonth.Year, _displayMonth.Month, 1);
        int daysInMonth = DateTime.DaysInMonth(monthStart.Year, monthStart.Month);
        int actualMinutes = 0;
        int plannedMinutes = 0;
        int activeDays = 0;
        int recurringScheduled = 0;
        int recurringCompleted = 0;
        List<int> dailyActualMinutes = new(daysInMonth);

        for (int day = 1; day <= daysInMonth; day++)
        {
            DailyEntry? entry = GetEntry(monthStart.AddDays(day - 1), create: false);
            int actual = entry?.ActualMinutes ?? 0;
            dailyActualMinutes.Add(actual);
            actualMinutes += actual;

            if (entry == null)
            {
                continue;
            }

            plannedMinutes += GetEffectivePlannedMinutes(entry);
            if (actual > 0)
            {
                activeDays++;
            }

            recurringScheduled += entry.Tasks.Count(task => task.IsRecurringTask && !task.IsSkipped);
            recurringCompleted += entry.Tasks.Count(task => task.IsRecurringTask && task.IsCompleted);
        }

        StatisticsMonthTitle.Text = monthStart.ToString("yyyy年M月统计", ZhCn);
        StatisticsActualText.Text = FormatDuration(actualMinutes);
        StatisticsPlanText.Text = FormatDuration(plannedMinutes);
        StatisticsActiveDaysText.Text = $"{activeDays} 天";
        StatisticsRecurringText.Text = recurringScheduled == 0
            ? "暂无"
            : $"{recurringCompleted} / {recurringScheduled}";

        int maximumMinutes = Math.Max(1, dailyActualMinutes.Max());
        _statisticsBars.Clear();
        for (int index = 0; index < dailyActualMinutes.Count; index++)
        {
            int dailyMinutes = dailyActualMinutes[index];
            _statisticsBars.Add(new StatisticsBar
            {
                Label = (index + 1).ToString(CultureInfo.InvariantCulture),
                Value = dailyMinutes > 0 ? FormatCompactDuration(dailyMinutes) : string.Empty,
                Height = dailyMinutes <= 0
                    ? 2
                    : Math.Max(8, Math.Round((double)dailyMinutes / maximumMinutes * 130))
            });
        }
    }

    private void LoadSelectedEntry()
    {
        _isLoadingEntry = true;

        bool addedRecurringTask = EnsureRecurringTasksForDate(_selectedDate);
        DailyEntry entry = GetEntry(_selectedDate, create: false) ?? new DailyEntry();
        NormalizeEntry(entry);

        _selectedTasks.Clear();
        bool planEditable = IsPlanEditable(_selectedDate);
        foreach (PlanTask task in entry.Tasks.Where(task => !task.IsHidden && !task.IsSkipped && !task.IsEmpty))
        {
            task.IsReadOnly = !planEditable;
            _selectedTasks.Add(task);
        }

        SetTotalPlanInputs(GetEffectivePlannedMinutes(entry));
        SelectedDateTitle.Text = _selectedDate.ToString("yyyy年M月d日 dddd", ZhCn);

        AddTaskBtn.IsEnabled = planEditable;
        TotalHoursBox.IsReadOnly = !planEditable;
        TotalMinutesBox.IsReadOnly = !planEditable;
        TotalHoursBox.Opacity = planEditable ? 1 : 0.68;
        TotalMinutesBox.Opacity = planEditable ? 1 : 0.68;

        _isLoadingEntry = false;

        if (addedRecurringTask)
        {
            SaveDailyEntries();
        }
    }

    private int LoadSavedCount()
    {
        if (!File.Exists(COUNT_FILE))
        {
            return 0;
        }

        try
        {
            DateTime fileBusinessDate = GetBusinessDate(File.GetLastWriteTime(COUNT_FILE));
            if (fileBusinessDate != _currentBusinessDate)
            {
                return 0;
            }

            string content = File.ReadAllText(COUNT_FILE);
            return int.TryParse(content, out int savedCount) ? savedCount : 0;
        }
        catch
        {
            return 0;
        }
    }

    private void ApplyLegacyCountToCurrentDate(int savedCount)
    {
        if (savedCount <= 0)
        {
            return;
        }

        DailyEntry entry = GetEntry(_currentBusinessDate, create: true)!;
        if (entry.ActualMinutes <= 0)
        {
            entry.ActualMinutes = savedCount;
            if (entry.Tasks.All(task => task.ActualMinutes <= 0))
            {
                EnsureFreeTask(entry).ActualMinutes = savedCount;
            }
        }
    }

    private void SaveCurrentCount()
    {
        try
        {
            File.WriteAllText(COUNT_FILE, _minuteCount.ToString(CultureInfo.InvariantCulture));
        }
        catch
        {
        }
    }

    private void LoadDailyEntries()
    {
        if (!File.Exists(PLAN_FILE))
        {
            return;
        }

        try
        {
            string content = File.ReadAllText(PLAN_FILE, Encoding.UTF8);
            Dictionary<string, DailyEntry>? savedEntries = JsonSerializer.Deserialize<Dictionary<string, DailyEntry>>(content, JsonOptions);
            if (savedEntries == null)
            {
                return;
            }

            foreach ((string key, DailyEntry? value) in savedEntries)
            {
                if (DateTime.TryParseExact(key, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out _) &&
                    value != null)
                {
                    _dailyEntries[key] = NormalizeEntry(value);
                }
            }
        }
        catch
        {
            _dailyEntries.Clear();
        }
    }

    private void SaveDailyEntries()
    {
        try
        {
            Dictionary<string, DailyEntry> entriesToSave = _dailyEntries
                .Where(pair => !IsEntryEmpty(pair.Value))
                .ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal);

            string json = JsonSerializer.Serialize(entriesToSave, JsonOptions);
            File.WriteAllText(PLAN_FILE, json, Encoding.UTF8);
        }
        catch
        {
        }
    }

    private void LoadLongTermTasks()
    {
        _isLoadingLongTermTasks = true;
        try
        {
            _longTermTasks.Clear();
            if (!File.Exists(LONG_TASK_FILE))
            {
                return;
            }

            string content = File.ReadAllText(LONG_TASK_FILE, Encoding.UTF8);
            List<LongTermTask>? savedTasks = JsonSerializer.Deserialize<List<LongTermTask>>(content, JsonOptions);
            if (savedTasks == null)
            {
                return;
            }

            foreach (LongTermTask task in savedTasks)
            {
                task.Normalize();
                if (!task.IsEmpty)
                {
                    _longTermTasks.Add(task);
                }
            }
        }
        catch
        {
            _longTermTasks.Clear();
        }
        finally
        {
            _isLoadingLongTermTasks = false;
        }
    }

    private void SaveLongTermTasks()
    {
        if (_isLoadingLongTermTasks)
        {
            return;
        }

        try
        {
            List<LongTermTask> tasksToSave = _longTermTasks
                .Where(task => !task.IsEmpty)
                .ToList();

            if (tasksToSave.Count == 0)
            {
                if (File.Exists(LONG_TASK_FILE))
                {
                    File.Delete(LONG_TASK_FILE);
                }

                return;
            }

            string json = JsonSerializer.Serialize(tasksToSave, JsonOptions);
            File.WriteAllText(LONG_TASK_FILE, json, Encoding.UTF8);
        }
        catch
        {
        }
    }

    private void SaveSelectedEntry()
    {
        if (_isLoadingEntry)
        {
            return;
        }

        string key = GetDateKey(_selectedDate);
        DailyEntry entry = GetEntry(_selectedDate, create: true)!;
        PlanTask? existingHiddenTask = entry.Tasks.FirstOrDefault(task => task.IsHidden);
        List<PlanTask> skippedRecurringTasks = entry.Tasks
            .Where(task => task.IsRecurringTask && task.IsSkipped)
            .ToList();

        List<PlanTask> visibleTasks = _selectedTasks
            .Where(task => !task.IsHidden && !task.IsSkipped && !task.IsEmpty)
            .ToList();
        int visiblePlannedMinutes = visibleTasks.Sum(task => task.PlannedTotalMinutes);
        int totalPlannedMinutes = ReadTotalPlanMinutes();
        if (totalPlannedMinutes <= 0 && visiblePlannedMinutes > 0)
        {
            totalPlannedMinutes = visiblePlannedMinutes;
            SetTotalPlanInputs(totalPlannedMinutes);
        }

        entry.TotalPlannedMinutes = totalPlannedMinutes;
        entry.Tasks = visibleTasks.Concat(skippedRecurringTasks).ToList();
        EnsureHiddenTask(entry, existingHiddenTask);
        entry.Plan = BuildLegacyPlan(entry.Tasks);

        if (IsEntryEmpty(entry))
        {
            _dailyEntries.Remove(key);
        }

        SaveDailyEntries();
    }

    private int ReadTotalPlanMinutes()
    {
        int hours = ParseNonNegativeInteger(TotalHoursBox.Text);
        int minutes = ParseNonNegativeInteger(TotalMinutesBox.Text);
        return (hours * 60) + minutes;
    }

    private void SetTotalPlanInputs(int totalMinutes)
    {
        _isUpdatingTotalPlan = true;
        try
        {
            int safeTotalMinutes = Math.Max(0, totalMinutes);
            int hours = safeTotalMinutes / 60;
            int minutes = safeTotalMinutes % 60;
            TotalHoursBox.Text = hours > 0 ? hours.ToString(CultureInfo.InvariantCulture) : string.Empty;
            TotalMinutesBox.Text = minutes > 0 ? minutes.ToString(CultureInfo.InvariantCulture) : string.Empty;
        }
        finally
        {
            _isUpdatingTotalPlan = false;
        }
    }

    private void NormalizeTotalPlanInputs()
    {
        SetTotalPlanInputs(ReadTotalPlanMinutes());
    }

    private void UpdateTimerInterval()
    {
        if (_minuteTimer == null)
        {
            return;
        }

        double intervalMinutes = INTERVAL_TIME / _speedFactor;
        _minuteTimer.Interval = TimeSpan.FromMinutes(intervalMinutes);
    }

    private void UpdateCounterDisplay()
    {
        string timeText = FormatCounterMinutes(_minuteCount);
        MinuteCounter.Text = timeText;
        ExpandedMinuteCounter.Text = timeText;
    }

    private void SetIndicator(Brush brush)
    {
        CompactIndicator.Background = brush;
        ExpandedIndicator.Background = brush;
    }

    private void RefreshBusinessDateIfNeeded()
    {
        DateTime businessDate = GetBusinessDate(DateTime.Now);
        if (businessDate == _currentBusinessDate)
        {
            return;
        }

        SaveSelectedEntry();
        _currentBusinessDate = businessDate;
        _selectedDate = businessDate;
        _displayMonth = new DateTime(businessDate.Year, businessDate.Month, 1);
        UpdateMinuteCountFromCurrentBusinessDate();
        LoadSelectedEntry();
        RenderCalendar();
        RefreshCurrentTaskDisplay();
    }

    private void UpdateMinuteCountFromCurrentBusinessDate()
    {
        DailyEntry? currentEntry = GetEntry(_currentBusinessDate, create: false);
        _minuteCount = currentEntry?.ActualMinutes ?? 0;
    }

    private void MoveCurrentTask(int delta)
    {
        List<PlanTask> tasks = GetCurrentBusinessTasks();
        if (tasks.Count == 0)
        {
            return;
        }

        _currentTaskIndex = (_currentTaskIndex + delta + tasks.Count) % tasks.Count;
        RefreshCurrentTaskDisplay();
    }

    private void RefreshCurrentTaskDisplay()
    {
        List<PlanTask> tasks = GetCurrentBusinessTasks();
        if (tasks.Count == 0)
        {
            _currentTaskIndex = 0;
            CurrentTaskName.Text = FREE_STUDY_TASK_NAME;
            CurrentTaskState.Text = $"已进行{FormatElapsedDuration(_minuteCount)}";
            CurrentTaskState.Foreground = CompletedBrush;
            TaskUpBtn.IsEnabled = false;
            TaskDownBtn.IsEnabled = false;
            CompleteTaskBtn.IsEnabled = false;
            CompleteTaskBtn.ToolTip = "完成当前周期任务";
            return;
        }

        if (_currentTaskIndex >= tasks.Count)
        {
            _currentTaskIndex = tasks.Count - 1;
        }

        PlanTask task = tasks[_currentTaskIndex];
        CurrentTaskName.Text = GetTaskDisplayName(task, _currentTaskIndex);
        RefreshCurrentTaskState(task);
        TaskUpBtn.IsEnabled = tasks.Count > 1;
        TaskDownBtn.IsEnabled = tasks.Count > 1;
        CompleteTaskBtn.IsEnabled = task.CanCompleteManually;
        CompleteTaskBtn.ToolTip = task.ManualCompleteToolTip;
    }

    private void RefreshCurrentTaskState(PlanTask task)
    {
        if (task.IsCompleted)
        {
            CurrentTaskState.Text = "已完成";
            CurrentTaskState.Foreground = CompletedBrush;
            return;
        }

        if (task.IsFreeTask)
        {
            CurrentTaskState.Text = $"已进行{FormatElapsedDuration(task.ActualMinutes)}";
            CurrentTaskState.Foreground = CompletedBrush;
            return;
        }

        int plannedMinutes = task.PlannedTotalMinutes;
        if (plannedMinutes <= 0)
        {
            CurrentTaskState.Text = $"已进行{FormatElapsedDuration(task.ActualMinutes)}";
            CurrentTaskState.Foreground = CompletedBrush;
            return;
        }

        int remainingMinutes = Math.Max(0, plannedMinutes - task.ActualMinutes);
        if (remainingMinutes == 0)
        {
            CurrentTaskState.Text = "已完成";
            CurrentTaskState.Foreground = CompletedBrush;
            return;
        }

        CurrentTaskState.Text = $"剩余 {FormatDuration(remainingMinutes)}";
        CurrentTaskState.Foreground = RemainingBrush;
    }

    private PlanTask? GetCurrentTask()
    {
        List<PlanTask> tasks = GetCurrentBusinessTasks();
        if (tasks.Count == 0)
        {
            return null;
        }

        if (_currentTaskIndex >= tasks.Count)
        {
            _currentTaskIndex = tasks.Count - 1;
        }

        return tasks[_currentTaskIndex];
    }

    private List<PlanTask> GetCurrentBusinessTasks()
    {
        if (EnsureRecurringTasksForDate(_currentBusinessDate))
        {
            SaveDailyEntries();
        }

        DailyEntry? entry = GetEntry(_currentBusinessDate, create: false);
        List<PlanTask> tasks = entry?.Tasks
            .Where(IsSelectablePlanTask)
            .ToList() ?? new List<PlanTask>();

        PlanTask freeTask = entry?.Tasks.FirstOrDefault(task => task.IsFreeTask) ?? CreateFreeTask();
        tasks.Add(freeTask);
        return tasks;
    }

    private PlanTask GetTaskForCurrentTick(DailyEntry entry, PlanTask? activeTask)
    {
        if (activeTask == null || activeTask.IsFreeTask || IsTaskCompleted(activeTask))
        {
            return EnsureFreeTask(entry);
        }

        return activeTask;
    }

    private void SelectCurrentFreeTask()
    {
        List<PlanTask> tasks = GetCurrentBusinessTasks();
        int freeTaskIndex = tasks.FindIndex(task => task.IsFreeTask);
        if (freeTaskIndex >= 0)
        {
            _currentTaskIndex = freeTaskIndex;
        }
    }

    private static bool IsSelectablePlanTask(PlanTask task)
    {
        return !task.IsHidden &&
               !task.IsFreeTask &&
               !task.IsSkipped &&
               !task.IsCompleted &&
               !task.IsEmpty;
    }

    private string GetCalendarTimeText(DateTime date)
    {
        DailyEntry? entry = GetEntry(date, create: false);
        if (entry == null)
        {
            return string.Empty;
        }

        int plannedMinutes = GetPlannedMinutes(entry);
        int displayMinutes;
        if (date.Date > _currentBusinessDate.Date)
        {
            displayMinutes = plannedMinutes;
        }
        else if (date.Date == _currentBusinessDate.Date)
        {
            displayMinutes = entry.ActualMinutes > 0 ? entry.ActualMinutes : plannedMinutes;
        }
        else
        {
            displayMinutes = entry.ActualMinutes > 0 ? entry.ActualMinutes : plannedMinutes;
        }

        return FormatCompactDuration(displayMinutes);
    }

    private DailyEntry? GetEntry(DateTime date, bool create)
    {
        string key = GetDateKey(date);
        if (_dailyEntries.TryGetValue(key, out DailyEntry? entry))
        {
            return entry;
        }

        if (!create)
        {
            return null;
        }

        entry = new DailyEntry();
        _dailyEntries[key] = entry;
        return entry;
    }

    private bool EnsureRecurringTasksForDate(DateTime date)
    {
        DateTime targetDate = date.Date;
        List<LongTermTask> allRecurringTemplates = _longTermTasks
            .Where(task => task.IsRecurringTask &&
                           !string.IsNullOrWhiteSpace(task.Name))
            .ToList();

        DailyEntry? existingEntry = GetEntry(targetDate, create: false);
        bool changed = false;
        if (existingEntry != null)
        {
            DailyEntry entryToSynchronize = NormalizeEntry(existingEntry);
            foreach (PlanTask existingTask in entryToSynchronize.Tasks.Where(task => task.IsRecurringTask))
            {
                LongTermTask? template = allRecurringTemplates
                    .FirstOrDefault(item => item.Id == existingTask.RecurringTaskId);
                if (template != null)
                {
                    if (!string.Equals(existingTask.Name, template.Name.Trim(), StringComparison.Ordinal))
                    {
                        existingTask.Name = template.Name.Trim();
                        changed = true;
                    }

                    int previousPlannedMinutes = existingTask.PlannedTotalMinutes;
                    string previousTimerMode = existingTask.TimerMode;
                    int previousIncrement = existingTask.ProgressIncrement;
                    string previousLongTermTaskId = existingTask.LongTermTaskId;
                    string previousRecurringTaskId = existingTask.RecurringTaskId;
                    ApplyLongTermTaskSettings(existingTask, template);
                    changed |= previousPlannedMinutes != existingTask.PlannedTotalMinutes ||
                               previousTimerMode != existingTask.TimerMode ||
                               previousIncrement != existingTask.ProgressIncrement ||
                               previousLongTermTaskId != existingTask.LongTermTaskId ||
                               previousRecurringTaskId != existingTask.RecurringTaskId;
                }
            }

            if (changed)
            {
                entryToSynchronize.Plan = BuildLegacyPlan(entryToSynchronize.Tasks);
            }
        }

        if (targetDate < _currentBusinessDate.Date)
        {
            return changed;
        }

        List<LongTermTask> scheduledTemplates = allRecurringTemplates
            .Where(task => task.OccursOn(targetDate))
            .ToList();
        if (scheduledTemplates.Count == 0)
        {
            return changed;
        }

        DailyEntry entry = existingEntry == null
            ? NormalizeEntry(GetEntry(targetDate, create: true)!)
            : NormalizeEntry(existingEntry);
        foreach (LongTermTask template in scheduledTemplates)
        {
            PlanTask? existingTask = entry.Tasks.FirstOrDefault(task => task.RecurringTaskId == template.Id);
            if (existingTask != null)
            {
                continue;
            }

            entry.Tasks.Add(CreatePlanTaskFromLongTerm(template));
            changed = true;
        }

        if (changed)
        {
            entry.Plan = BuildLegacyPlan(entry.Tasks);
        }

        return changed;
    }

    private bool HasEntry(DateTime date)
    {
        DailyEntry? entry = GetEntry(date, create: false);
        return entry != null &&
               (entry.ActualMinutes > 0 ||
                entry.Tasks.Any(task => !task.IsHidden &&
                                        !task.IsFreeTask &&
                                        !task.IsSkipped &&
                                        (task.ActualMinutes > 0 || task.IsCompleted)));
    }

    private bool IsPlanEditable(DateTime date)
    {
        return date.Date >= _currentBusinessDate.Date;
    }

    private static CalendarDayViewModel CreateCalendarDay(
        DateTime date,
        bool isCurrentMonth,
        bool isSelected,
        bool isToday,
        bool hasEntry,
        string timeText)
    {
        Visibility timeVisibility = string.IsNullOrWhiteSpace(timeText) ? Visibility.Collapsed : Visibility.Visible;

        if (isSelected)
        {
            return new CalendarDayViewModel
            {
                Date = date.Date,
                DayText = date.Day.ToString(CultureInfo.InvariantCulture),
                TimeText = timeText,
                TimeVisibility = timeVisibility,
                Background = CalendarSelectedBackground,
                BorderBrush = CalendarSelectedBorder,
                Foreground = CalendarSelectedText,
                TimeForeground = CalendarSelectedText,
                FontWeight = FontWeights.Bold,
                DotBrush = CalendarSelectedDot,
                DotVisibility = hasEntry ? Visibility.Visible : Visibility.Collapsed
            };
        }

        if (isToday)
        {
            return new CalendarDayViewModel
            {
                Date = date.Date,
                DayText = date.Day.ToString(CultureInfo.InvariantCulture),
                TimeText = timeText,
                TimeVisibility = timeVisibility,
                Background = CalendarTodayBackground,
                BorderBrush = CalendarAccentBorder,
                Foreground = CalendarNormalText,
                TimeForeground = CalendarEntryText,
                FontWeight = FontWeights.SemiBold,
                DotBrush = CalendarEntryDot,
                DotVisibility = hasEntry ? Visibility.Visible : Visibility.Collapsed
            };
        }

        return new CalendarDayViewModel
        {
            Date = date.Date,
            DayText = date.Day.ToString(CultureInfo.InvariantCulture),
            TimeText = timeText,
            TimeVisibility = timeVisibility,
            Background = isCurrentMonth ? CalendarNormalBackground : CalendarMutedBackground,
            BorderBrush = isCurrentMonth ? CalendarNormalBorder : CalendarMutedBorder,
            Foreground = isCurrentMonth ? CalendarNormalText : CalendarMutedText,
            TimeForeground = CalendarEntryText,
            FontWeight = FontWeights.Normal,
            DotBrush = CalendarEntryDot,
            DotVisibility = hasEntry ? Visibility.Visible : Visibility.Collapsed
        };
    }

    private static DailyEntry NormalizeEntry(DailyEntry entry)
    {
        entry.Tasks ??= new List<PlanTask>();

        foreach (PlanTask task in entry.Tasks)
        {
            task.Normalize();
        }

        if (entry.Tasks.Count == 0 && !string.IsNullOrWhiteSpace(entry.Plan))
        {
            string[] legacyLines = entry.Plan
                .Split(new[] { "\r\n", "\n" }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

            foreach (string legacyLine in legacyLines)
            {
                entry.Tasks.Add(CreateTaskFromLegacyLine(legacyLine));
            }
        }

        if (entry.TotalPlannedMinutes <= 0)
        {
            entry.TotalPlannedMinutes = entry.Tasks
                .Where(task => !task.IsHidden && !task.IsFreeTask)
                .Sum(task => task.PlannedTotalMinutes);
        }

        MoveHiddenAndUnassignedActualMinutesToFreeTask(entry);
        EnsureHiddenTask(entry, entry.Tasks.FirstOrDefault(task => task.IsHidden));
        entry.Summary ??= string.Empty;
        entry.Plan ??= string.Empty;
        return entry;
    }

    private static bool IsEntryEmpty(DailyEntry entry)
    {
        return entry.ActualMinutes <= 0 &&
               entry.TotalPlannedMinutes <= 0 &&
               entry.Tasks.All(task => task.IsEmpty);
    }

    private static string BuildLegacyPlan(IEnumerable<PlanTask> tasks)
    {
        return string.Join(
            Environment.NewLine,
            tasks.Where(task => !task.IsHidden && !task.IsFreeTask).Select((task, index) =>
                string.IsNullOrWhiteSpace(task.PlannedText)
                    ? GetTaskDisplayName(task, index)
                    : $"{GetTaskDisplayName(task, index)} {task.PlannedText.Trim()}"));
    }

    private static PlanTask CreateTaskFromLegacyLine(string legacyLine)
    {
        string trimmedLine = legacyLine.Trim();
        int splitIndex = trimmedLine.LastIndexOf(' ');
        if (splitIndex > 0)
        {
            string possibleDuration = trimmedLine[(splitIndex + 1)..];
            if (ParsePlannedMinutes(possibleDuration) > 0)
            {
                return new PlanTask
                {
                    Name = trimmedLine[..splitIndex],
                    PlannedText = possibleDuration
                };
            }
        }

        return new PlanTask
        {
            Name = trimmedLine
        };
    }

    private static int GetPlannedMinutes(DailyEntry entry)
    {
        return GetEffectivePlannedMinutes(entry);
    }

    private static int GetEffectivePlannedMinutes(DailyEntry entry)
    {
        return entry.TotalPlannedMinutes > 0
            ? entry.TotalPlannedMinutes
            : GetVisiblePlannedMinutes(entry);
    }

    private static int GetVisiblePlannedMinutes(DailyEntry entry)
    {
        return entry.Tasks
            .Where(task => !task.IsHidden && !task.IsFreeTask)
            .Sum(task => task.PlannedTotalMinutes);
    }

    private static void MoveHiddenAndUnassignedActualMinutesToFreeTask(DailyEntry entry)
    {
        int hiddenActualMinutes = entry.Tasks
            .Where(task => task.IsHidden)
            .Sum(task => task.ActualMinutes);
        if (hiddenActualMinutes > 0)
        {
            EnsureFreeTask(entry).ActualMinutes += hiddenActualMinutes;
            foreach (PlanTask hiddenTask in entry.Tasks.Where(task => task.IsHidden))
            {
                hiddenTask.ActualMinutes = 0;
            }
        }

        int assignedActualMinutes = entry.Tasks
            .Where(task => !task.IsHidden)
            .Sum(task => task.ActualMinutes);
        int unassignedActualMinutes = Math.Max(0, entry.ActualMinutes - assignedActualMinutes);
        if (unassignedActualMinutes > 0)
        {
            EnsureFreeTask(entry).ActualMinutes += unassignedActualMinutes;
        }
    }

    private static PlanTask EnsureFreeTask(DailyEntry entry)
    {
        entry.Tasks ??= new List<PlanTask>();
        PlanTask? freeTask = entry.Tasks.FirstOrDefault(task => task.IsFreeTask);
        if (freeTask == null)
        {
            freeTask = CreateFreeTask();
            entry.Tasks.Add(freeTask);
        }

        freeTask.NormalizeFreeTask(FREE_STUDY_TASK_NAME);
        return freeTask;
    }

    private static PlanTask CreateFreeTask()
    {
        PlanTask freeTask = new()
        {
            IsFreeTask = true
        };
        freeTask.NormalizeFreeTask(FREE_STUDY_TASK_NAME);
        return freeTask;
    }

    private static void EnsureHiddenTask(DailyEntry entry, PlanTask? existingHiddenTask)
    {
        entry.Tasks.RemoveAll(task => task.IsHidden);

        int hiddenMinutes = Math.Max(0, entry.TotalPlannedMinutes - GetVisiblePlannedMinutes(entry));
        if (hiddenMinutes <= 0)
        {
            return;
        }

        PlanTask hiddenTask = new()
        {
            IsHidden = true,
            ActualMinutes = 0
        };
        hiddenTask.PlannedHours = hiddenMinutes / 60;
        hiddenTask.PlannedMinutes = hiddenMinutes % 60;
        entry.Tasks.Add(hiddenTask);
    }

    private static bool IsTaskCompleted(PlanTask? task)
    {
        return task != null &&
               !task.IsHidden &&
               !task.IsFreeTask &&
               task.IsCompleted;
    }

    internal static int ParsePlannedMinutes(string input)
    {
        string text = input.Trim().ToLowerInvariant();
        if (string.IsNullOrWhiteSpace(text))
        {
            return 0;
        }

        if (TryParseChineseDuration(text, out int chineseMinutes))
        {
            return chineseMinutes;
        }

        if (text.EndsWith('m') &&
            double.TryParse(text[..^1], NumberStyles.Float, CultureInfo.InvariantCulture, out double minutes))
        {
            return Math.Max(0, (int)Math.Round(minutes));
        }

        if (text.EndsWith('h') &&
            double.TryParse(text[..^1], NumberStyles.Float, CultureInfo.InvariantCulture, out double hoursWithSuffix))
        {
            return Math.Max(0, (int)Math.Round(hoursWithSuffix * 60));
        }

        if (text.Contains(':'))
        {
            string[] parts = text.Split(':', 2, StringSplitOptions.TrimEntries);
            if (parts.Length == 2 &&
                int.TryParse(parts[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out int hoursPart) &&
                int.TryParse(parts[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out int minutesPart))
            {
                return Math.Max(0, (hoursPart * 60) + minutesPart);
            }
        }

        if (double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out double hours))
        {
            return Math.Max(0, (int)Math.Round(hours * 60));
        }

        return 0;
    }

    private static int ParseNonNegativeInteger(string input)
    {
        string text = new(input.Where(char.IsDigit).ToArray());
        return int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out int parsedValue)
            ? Math.Max(0, parsedValue)
            : 0;
    }

    private static string NormalizeDurationText(string input)
    {
        int minutes = ParsePlannedMinutes(input);
        return minutes > 0 ? FormatDuration(minutes) : input.Trim();
    }

    private static bool TryParseChineseDuration(string text, out int minutes)
    {
        minutes = 0;
        bool parsedAny = false;
        string remainingText = text.Replace(" ", string.Empty, StringComparison.Ordinal);

        int hourMarker = remainingText.IndexOf("小时", StringComparison.Ordinal);
        if (hourMarker >= 0)
        {
            string hourText = remainingText[..hourMarker];
            if (!double.TryParse(hourText, NumberStyles.Float, CultureInfo.InvariantCulture, out double hours))
            {
                return false;
            }

            minutes += Math.Max(0, (int)Math.Round(hours * 60));
            parsedAny = true;
            remainingText = remainingText[(hourMarker + 2)..];
        }

        int minuteMarker = remainingText.IndexOf("分钟", StringComparison.Ordinal);
        if (minuteMarker >= 0)
        {
            string minuteText = remainingText[..minuteMarker];
            if (!double.TryParse(minuteText, NumberStyles.Float, CultureInfo.InvariantCulture, out double parsedMinutes))
            {
                return false;
            }

            minutes += Math.Max(0, (int)Math.Round(parsedMinutes));
            parsedAny = true;
        }

        return parsedAny;
    }

    private static string FormatCounterMinutes(int minutes)
    {
        int hours = Math.Max(0, minutes) / 60;
        int remainingMinutes = Math.Max(0, minutes) % 60;
        return $"{hours:00}:{remainingMinutes:00}";
    }

    private static string FormatElapsedDuration(int minutes)
    {
        return minutes > 0 ? FormatDuration(minutes) : "0分钟";
    }

    private static string GetTaskDisplayName(PlanTask task, int index)
    {
        string taskName = task.Name.Trim();
        return string.IsNullOrWhiteSpace(taskName)
            ? $"{UNNAMED_TASK_PREFIX}{index + 1:00}"
            : taskName;
    }

    private static string FormatCompactDuration(int minutes)
    {
        if (minutes <= 0)
        {
            return string.Empty;
        }

        int hours = minutes / 60;
        int remainingMinutes = minutes % 60;
        if (hours > 0 && remainingMinutes > 0)
        {
            return $"{hours}h{remainingMinutes}m";
        }

        if (hours > 0)
        {
            return $"{hours}h";
        }

        return $"{remainingMinutes}m";
    }

    internal static string FormatDuration(int minutes)
    {
        if (minutes <= 0)
        {
            return string.Empty;
        }

        int hours = minutes / 60;
        int remainingMinutes = minutes % 60;
        if (hours > 0 && remainingMinutes > 0)
        {
            return $"{hours}小时{remainingMinutes}分钟";
        }

        if (hours > 0)
        {
            return $"{hours}小时";
        }

        return $"{remainingMinutes}分钟";
    }

    private static DateTime GetBusinessDate(DateTime now)
    {
        DateTime date = now.Date;
        return now.TimeOfDay < DailyRefreshTime ? date.AddDays(-1) : date;
    }

    private static string GetDateKey(DateTime date)
    {
        return date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
    }

    private static bool IsFromInteractiveElement(DependencyObject? source)
    {
        while (source != null)
        {
            if (source is ButtonBase or TextBoxBase)
            {
                return true;
            }

            source = source is Visual
                ? VisualTreeHelper.GetParent(source)
                : LogicalTreeHelper.GetParent(source);
        }

        return false;
    }

    private static Brush CreateBrush(string color)
    {
        SolidColorBrush brush = new((Color)ColorConverter.ConvertFromString(color)!);
        brush.Freeze();
        return brush;
    }
}

public sealed class DailyEntry
{
    public string Plan { get; set; } = string.Empty;

    public string Summary { get; set; } = string.Empty;

    public int TotalPlannedMinutes { get; set; }

    public int ActualMinutes { get; set; }

    public List<PlanTask> Tasks { get; set; } = new();
}

public sealed class LongTermTask : INotifyPropertyChanged
{
    private const int CurrentSchemaVersion = 3;
    private const string ProgressTaskType = "Progress";
    private const string RecurringTaskType = "Recurring";
    private const string PercentProgressMode = "Percent";
    private const string CountProgressMode = "Count";
    private const string CountdownTimerMode = "Countdown";
    private const string CountUpTimerMode = "CountUp";
    private const int AllWeekdaysMask = 127;

    private string _id = string.Empty;
    private string _name = string.Empty;
    private string _note = string.Empty;
    private string _taskType = ProgressTaskType;
    private string _progressMode = PercentProgressMode;
    private string _timerMode = CountdownTimerMode;
    private string _progressUnit = "题";
    private int _currentValue;
    private int _targetValue;
    private int _completionIncrement = 1;
    private int _defaultPlannedHours;
    private int _defaultPlannedMinutes;
    private int _weekdayMask = AllWeekdaysMask;
    private bool _isNoteEditing;

    public event PropertyChangedEventHandler? PropertyChanged;

    public int SchemaVersion { get; set; }

    [JsonPropertyName("Progress")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? LegacyProgress { get; set; }

    public string Id
    {
        get => _id;
        set => _id = value ?? string.Empty;
    }

    public string Name
    {
        get => _name;
        set
        {
            if (_name == value)
            {
                return;
            }

            _name = value ?? string.Empty;
            OnPropertyChanged();
            OnPropertyChanged(nameof(IsEmpty));
            OnPropertyChanged(nameof(CanAddToToday));
        }
    }

    public string Note
    {
        get => _note;
        set
        {
            if (_note == value)
            {
                return;
            }

            _note = value ?? string.Empty;
            OnPropertyChanged();
            OnPropertyChanged(nameof(IsEmpty));
        }
    }

    public string TaskType
    {
        get => _taskType;
        set
        {
            string nextValue = value == RecurringTaskType ? RecurringTaskType : ProgressTaskType;
            if (_taskType == nextValue)
            {
                return;
            }

            _taskType = nextValue;
            if (_taskType == RecurringTaskType && _weekdayMask == 0)
            {
                _weekdayMask = AllWeekdaysMask;
            }

            NotifyTaskTypeChanged();
        }
    }

    public string TimerMode
    {
        get => _timerMode;
        set
        {
            string nextValue = value == CountUpTimerMode ? CountUpTimerMode : CountdownTimerMode;
            if (_timerMode == nextValue)
            {
                return;
            }

            _timerMode = nextValue;
            NotifyTimerModeChanged();
        }
    }

    public string ProgressMode
    {
        get => _progressMode;
        set
        {
            string nextValue = value == CountProgressMode ? CountProgressMode : PercentProgressMode;
            if (_progressMode == nextValue)
            {
                return;
            }

            _progressMode = nextValue;
            if (_progressMode == PercentProgressMode)
            {
                _targetValue = 100;
                _currentValue = Math.Clamp(_currentValue, 0, 100);
            }
            else if (_targetValue <= 0)
            {
                _targetValue = 1;
            }

            NotifyProgressPresentationChanged();
        }
    }

    public string ProgressUnit
    {
        get => _progressUnit;
        set
        {
            string nextValue = string.IsNullOrWhiteSpace(value) ? "题" : value.Trim();
            if (_progressUnit == nextValue)
            {
                return;
            }

            _progressUnit = nextValue;
            OnPropertyChanged();
            OnPropertyChanged(nameof(ProgressDisplay));
        }
    }

    public int CurrentValue
    {
        get => _currentValue;
        set
        {
            int nextValue = Math.Max(0, value);
            if (ProgressMode == PercentProgressMode)
            {
                nextValue = Math.Min(nextValue, 100);
            }

            if (_currentValue == nextValue)
            {
                return;
            }

            _currentValue = nextValue;
            OnPropertyChanged();
            OnPropertyChanged(nameof(CurrentValueText));
            OnPropertyChanged(nameof(ProgressDisplay));
        }
    }

    public int CompletionIncrement
    {
        get => _completionIncrement;
        set
        {
            int nextValue = Math.Max(1, value);
            if (_completionIncrement == nextValue)
            {
                return;
            }

            _completionIncrement = nextValue;
            OnPropertyChanged();
            OnPropertyChanged(nameof(CompletionIncrementText));
        }
    }

    public int TargetValue
    {
        get => _targetValue;
        set
        {
            int nextValue = ProgressMode == PercentProgressMode ? 100 : Math.Max(1, value);
            if (_targetValue == nextValue)
            {
                return;
            }

            _targetValue = nextValue;
            OnPropertyChanged();
            OnPropertyChanged(nameof(TargetValueText));
            OnPropertyChanged(nameof(ProgressDisplay));
        }
    }

    public int DefaultPlannedHours
    {
        get => _defaultPlannedHours;
        set => SetDefaultPlannedTime(value, _defaultPlannedMinutes);
    }

    public int DefaultPlannedMinutes
    {
        get => _defaultPlannedMinutes;
        set => SetDefaultPlannedTime(_defaultPlannedHours, value);
    }

    public int WeekdayMask
    {
        get => _weekdayMask;
        set
        {
            int nextValue = Math.Clamp(value, 0, AllWeekdaysMask);
            if (_weekdayMask == nextValue)
            {
                return;
            }

            _weekdayMask = nextValue;
            NotifyWeekdaysChanged();
        }
    }

    [JsonIgnore]
    public string CurrentValueText
    {
        get => CurrentValue.ToString(CultureInfo.InvariantCulture);
        set => CurrentValue = ParseNonNegativeInt(KeepDigits(value));
    }

    [JsonIgnore]
    public string CompletionIncrementText
    {
        get => CompletionIncrement.ToString(CultureInfo.InvariantCulture);
        set => CompletionIncrement = Math.Max(1, ParseNonNegativeInt(KeepDigits(value)));
    }

    [JsonIgnore]
    public string TargetValueText
    {
        get => TargetValue.ToString(CultureInfo.InvariantCulture);
        set => TargetValue = ParseNonNegativeInt(KeepDigits(value));
    }

    [JsonIgnore]
    public string DefaultPlannedHoursText
    {
        get => DefaultPlannedHours > 0 ? DefaultPlannedHours.ToString(CultureInfo.InvariantCulture) : string.Empty;
        set => DefaultPlannedHours = ParseNonNegativeInt(KeepDigits(value));
    }

    [JsonIgnore]
    public string DefaultPlannedMinutesText
    {
        get => DefaultPlannedMinutes > 0 ? DefaultPlannedMinutes.ToString(CultureInfo.InvariantCulture) : string.Empty;
        set => DefaultPlannedMinutes = ParseNonNegativeInt(KeepDigits(value));
    }

    [JsonIgnore]
    public int DefaultPlannedTotalMinutes => (DefaultPlannedHours * 60) + DefaultPlannedMinutes;

    [JsonIgnore]
    public string DefaultCountdownDisplay => MainWindow.FormatDuration(DefaultPlannedTotalMinutes);

    [JsonIgnore]
    public bool IsManualTask => TaskType == ProgressTaskType;

    [JsonIgnore]
    public bool IsRecurringTask => TaskType == RecurringTaskType;

    [JsonIgnore]
    public bool IsCountUpTimer => TimerMode == CountUpTimerMode;

    [JsonIgnore]
    public bool IsCountdownTimer => TimerMode == CountdownTimerMode;

    [JsonIgnore]
    public Visibility ProgressSettingsVisibility => Visibility.Visible;

    [JsonIgnore]
    public Visibility RecurringSettingsVisibility => IsRecurringTask ? Visibility.Visible : Visibility.Collapsed;

    [JsonIgnore]
    public Visibility CountProgressVisibility => ProgressMode == CountProgressMode ? Visibility.Visible : Visibility.Collapsed;

    [JsonIgnore]
    public Visibility PercentProgressVisibility => ProgressMode == PercentProgressMode ? Visibility.Visible : Visibility.Collapsed;

    [JsonIgnore]
    public Visibility CountdownSettingsVisibility => IsCountdownTimer ? Visibility.Visible : Visibility.Collapsed;

    [JsonIgnore]
    public Visibility CountUpHelpVisibility => IsCountUpTimer ? Visibility.Visible : Visibility.Collapsed;

    [JsonIgnore]
    public string ProgressDisplay
    {
        get
        {
            string progress = ProgressMode == PercentProgressMode
                ? $"{CurrentValue}%"
                : $"{CurrentValue}{ProgressUnit}";
            return IsRecurringTask ? $"{progress} · {WeekdaySummary}" : progress;
        }
    }

    [JsonIgnore]
    public string WeekdaySummary
    {
        get
        {
            if (WeekdayMask == AllWeekdaysMask)
            {
                return "每天";
            }

            string weekdays = string.Concat(
                IsMonday ? "一" : string.Empty,
                IsTuesday ? "二" : string.Empty,
                IsWednesday ? "三" : string.Empty,
                IsThursday ? "四" : string.Empty,
                IsFriday ? "五" : string.Empty,
                IsSaturday ? "六" : string.Empty,
                IsSunday ? "日" : string.Empty);
            return string.IsNullOrEmpty(weekdays) ? "未安排" : $"每周 {weekdays}";
        }
    }

    [JsonIgnore]
    public bool IsMonday { get => HasWeekday(1); set => SetWeekday(1, value); }

    [JsonIgnore]
    public bool IsTuesday { get => HasWeekday(2); set => SetWeekday(2, value); }

    [JsonIgnore]
    public bool IsWednesday { get => HasWeekday(4); set => SetWeekday(4, value); }

    [JsonIgnore]
    public bool IsThursday { get => HasWeekday(8); set => SetWeekday(8, value); }

    [JsonIgnore]
    public bool IsFriday { get => HasWeekday(16); set => SetWeekday(16, value); }

    [JsonIgnore]
    public bool IsSaturday { get => HasWeekday(32); set => SetWeekday(32, value); }

    [JsonIgnore]
    public bool IsSunday { get => HasWeekday(64); set => SetWeekday(64, value); }

    [JsonIgnore]
    public bool IsEmpty => string.IsNullOrWhiteSpace(Name) && string.IsNullOrWhiteSpace(Note);

    [JsonIgnore]
    public bool CanAddToToday => !string.IsNullOrWhiteSpace(Name);

    [JsonIgnore]
    public bool IsNoteReadOnly => !_isNoteEditing;

    public static LongTermTask CreateLongTermTask()
    {
        return new LongTermTask
        {
            SchemaVersion = CurrentSchemaVersion,
            Id = Guid.NewGuid().ToString("N"),
            TaskType = ProgressTaskType,
            ProgressMode = PercentProgressMode,
            TimerMode = CountdownTimerMode,
            TargetValue = 100,
            CompletionIncrement = 1,
            ProgressUnit = "题",
            WeekdayMask = AllWeekdaysMask
        };
    }

    public void Normalize()
    {
        bool isOriginalLegacyTask = SchemaVersion < 2;
        bool needsTimerModeMigration = SchemaVersion < CurrentSchemaVersion;
        Name ??= string.Empty;
        Note ??= string.Empty;

        if (isOriginalLegacyTask)
        {
            _taskType = ProgressTaskType;
            _progressMode = PercentProgressMode;
            _targetValue = 100;
            _currentValue = Math.Clamp(LegacyProgress ?? _currentValue, 0, 100);
            _progressUnit = "题";
            _weekdayMask = AllWeekdaysMask;
        }

        if (string.IsNullOrWhiteSpace(Id))
        {
            Id = Guid.NewGuid().ToString("N");
        }

        TaskType = TaskType;
        ProgressMode = ProgressMode;
        TimerMode = needsTimerModeMigration
            ? (IsRecurringTask ? CountUpTimerMode : CountdownTimerMode)
            : TimerMode;
        if (ProgressMode == PercentProgressMode)
        {
            TargetValue = 100;
        }
        else if (TargetValue <= 0)
        {
            TargetValue = 1;
        }

        CurrentValue = CurrentValue;
        CompletionIncrement = CompletionIncrement;
        ProgressUnit = ProgressUnit;
        SetDefaultPlannedTime(DefaultPlannedHours, DefaultPlannedMinutes);
        if (IsRecurringTask && WeekdayMask == 0)
        {
            WeekdayMask = AllWeekdaysMask;
        }

        LegacyProgress = null;
        SchemaVersion = CurrentSchemaVersion;
        EndNoteEdit();
        NotifyTaskTypeChanged();
        NotifyProgressPresentationChanged();
        NotifyTimerModeChanged();
        NotifyWeekdaysChanged();
    }

    public void NormalizeProgress()
    {
        Normalize();
    }

    public void AdjustProgress(int delta)
    {
        CurrentValue += delta;
    }

    public bool OccursOn(DateTime date)
    {
        int weekdayBit = date.DayOfWeek switch
        {
            DayOfWeek.Monday => 1,
            DayOfWeek.Tuesday => 2,
            DayOfWeek.Wednesday => 4,
            DayOfWeek.Thursday => 8,
            DayOfWeek.Friday => 16,
            DayOfWeek.Saturday => 32,
            _ => 64
        };
        return (WeekdayMask & weekdayBit) != 0;
    }

    public void BeginNoteEdit()
    {
        if (_isNoteEditing)
        {
            return;
        }

        _isNoteEditing = true;
        OnPropertyChanged(nameof(IsNoteReadOnly));
    }

    public void EndNoteEdit()
    {
        if (!_isNoteEditing)
        {
            return;
        }

        _isNoteEditing = false;
        OnPropertyChanged(nameof(IsNoteReadOnly));
    }

    private bool HasWeekday(int weekdayBit)
    {
        return (WeekdayMask & weekdayBit) != 0;
    }

    private void SetWeekday(int weekdayBit, bool isEnabled)
    {
        WeekdayMask = isEnabled ? WeekdayMask | weekdayBit : WeekdayMask & ~weekdayBit;
    }

    private void SetDefaultPlannedTime(int hours, int minutes)
    {
        int totalMinutes = (Math.Max(0, hours) * 60) + Math.Max(0, minutes);
        int nextHours = totalMinutes / 60;
        int nextMinutes = totalMinutes % 60;
        if (_defaultPlannedHours == nextHours && _defaultPlannedMinutes == nextMinutes)
        {
            return;
        }

        _defaultPlannedHours = nextHours;
        _defaultPlannedMinutes = nextMinutes;
        OnPropertyChanged(nameof(DefaultPlannedHours));
        OnPropertyChanged(nameof(DefaultPlannedMinutes));
        OnPropertyChanged(nameof(DefaultPlannedHoursText));
        OnPropertyChanged(nameof(DefaultPlannedMinutesText));
        OnPropertyChanged(nameof(DefaultPlannedTotalMinutes));
        OnPropertyChanged(nameof(DefaultCountdownDisplay));
        OnPropertyChanged(nameof(CanAddToToday));
    }

    private void NotifyTaskTypeChanged()
    {
        OnPropertyChanged(nameof(TaskType));
        OnPropertyChanged(nameof(IsManualTask));
        OnPropertyChanged(nameof(IsRecurringTask));
        OnPropertyChanged(nameof(ProgressSettingsVisibility));
        OnPropertyChanged(nameof(RecurringSettingsVisibility));
        OnPropertyChanged(nameof(CanAddToToday));
        OnPropertyChanged(nameof(ProgressDisplay));
    }

    private void NotifyTimerModeChanged()
    {
        OnPropertyChanged(nameof(TimerMode));
        OnPropertyChanged(nameof(IsCountUpTimer));
        OnPropertyChanged(nameof(IsCountdownTimer));
        OnPropertyChanged(nameof(CountdownSettingsVisibility));
        OnPropertyChanged(nameof(CountUpHelpVisibility));
    }

    private void NotifyProgressPresentationChanged()
    {
        OnPropertyChanged(nameof(ProgressMode));
        OnPropertyChanged(nameof(CountProgressVisibility));
        OnPropertyChanged(nameof(PercentProgressVisibility));
        OnPropertyChanged(nameof(CurrentValue));
        OnPropertyChanged(nameof(CurrentValueText));
        OnPropertyChanged(nameof(TargetValue));
        OnPropertyChanged(nameof(TargetValueText));
        OnPropertyChanged(nameof(CompletionIncrement));
        OnPropertyChanged(nameof(CompletionIncrementText));
        OnPropertyChanged(nameof(ProgressDisplay));
    }

    private void NotifyWeekdaysChanged()
    {
        OnPropertyChanged(nameof(WeekdayMask));
        OnPropertyChanged(nameof(IsMonday));
        OnPropertyChanged(nameof(IsTuesday));
        OnPropertyChanged(nameof(IsWednesday));
        OnPropertyChanged(nameof(IsThursday));
        OnPropertyChanged(nameof(IsFriday));
        OnPropertyChanged(nameof(IsSaturday));
        OnPropertyChanged(nameof(IsSunday));
        OnPropertyChanged(nameof(WeekdaySummary));
        OnPropertyChanged(nameof(ProgressDisplay));
    }

    private void OnPropertyChanged([CallerMemberName] string? propertyName = null)
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }

    private static int ParseNonNegativeInt(string value)
    {
        return int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out int parsedValue)
            ? Math.Max(0, parsedValue)
            : 0;
    }

    private static string KeepDigits(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return string.Empty;
        }

        StringBuilder builder = new();
        foreach (char character in value)
        {
            if (char.IsDigit(character))
            {
                builder.Append(character);
            }
        }

        return builder.ToString();
    }
}

public sealed class PlanTask : INotifyPropertyChanged
{
    private string _name = string.Empty;
    private string _plannedText = string.Empty;
    private int _plannedHours;
    private int _plannedMinutes;
    private string _plannedHoursText = string.Empty;
    private string _plannedMinutesText = string.Empty;
    private int _actualMinutes;
    private bool _isReadOnly;
    private bool _isManuallyCompleted;
    private bool _isSkipped;
    private string _timerMode = string.Empty;

    public event PropertyChangedEventHandler? PropertyChanged;

    public bool IsHidden { get; set; }

    public bool IsFreeTask { get; set; }

    public string RecurringTaskId { get; set; } = string.Empty;

    public string LongTermTaskId { get; set; } = string.Empty;

    public string TimerMode
    {
        get => _timerMode;
        set
        {
            string nextValue = value == "CountUp" ? "CountUp" : "Countdown";
            if (_timerMode == nextValue)
            {
                return;
            }

            _timerMode = nextValue;
            NotifyTimerModeChanged();
        }
    }

    public int ProgressIncrement { get; set; }

    public bool IsProgressCountApplied { get; set; }

    public bool IsManuallyCompleted
    {
        get => _isManuallyCompleted;
        set
        {
            if (_isManuallyCompleted == value)
            {
                return;
            }

            _isManuallyCompleted = value;
            NotifyCompletionChanged();
        }
    }

    public bool IsSkipped
    {
        get => _isSkipped;
        set
        {
            if (_isSkipped == value)
            {
                return;
            }

            _isSkipped = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(CanCompleteManually));
            OnPropertyChanged(nameof(ManualCompleteButtonVisibility));
        }
    }

    public string Name
    {
        get => _name;
        set
        {
            if (_name == value)
            {
                return;
            }

            _name = value ?? string.Empty;
            OnPropertyChanged();
            OnPropertyChanged(nameof(IsEmpty));
        }
    }

    public string PlannedText
    {
        get => PlannedTotalMinutes > 0 ? MainWindow.FormatDuration(PlannedTotalMinutes) : _plannedText;
        set
        {
            string newValue = value ?? string.Empty;
            if (_plannedText == newValue)
            {
                return;
            }

            _plannedText = newValue;
            if (PlannedTotalMinutes <= 0)
            {
                int parsedMinutes = MainWindow.ParsePlannedMinutes(_plannedText);
                if (parsedMinutes > 0)
                {
                    SetPlannedFromMinutes(parsedMinutes);
                }
            }

            NotifyPlannedChanged();
        }
    }

    public int PlannedHours
    {
        get => _plannedHours;
        set => SetPlannedTime(Math.Max(0, value), _plannedMinutes);
    }

    public int PlannedMinutes
    {
        get => _plannedMinutes;
        set => SetPlannedTime(_plannedHours, Math.Max(0, value));
    }

    public int ActualMinutes
    {
        get => _actualMinutes;
        set
        {
            if (_actualMinutes == value)
            {
                return;
            }

            _actualMinutes = Math.Max(0, value);
            OnPropertyChanged();
            OnPropertyChanged(nameof(IsCompleted));
            OnPropertyChanged(nameof(ActualDisplay));
            OnPropertyChanged(nameof(ActualForeground));
            OnPropertyChanged(nameof(ActualVisibility));
            OnPropertyChanged(nameof(CanTransferFreeTime));
            OnPropertyChanged(nameof(IsEmpty));
        }
    }

    [JsonIgnore]
    public bool IsReadOnly
    {
        get => _isReadOnly;
        set
        {
            if (_isReadOnly == value)
            {
                return;
            }

            _isReadOnly = value;

            OnPropertyChanged();
            OnPropertyChanged(nameof(IsInputReadOnly));
            OnPropertyChanged(nameof(CanEdit));
            OnPropertyChanged(nameof(CanTransferFreeTime));
            OnPropertyChanged(nameof(CanCompleteManually));
        }
    }

    [JsonIgnore]
    public bool IsInputReadOnly => IsReadOnly || IsFreeTask || IsRecurringTask;

    [JsonIgnore]
    public bool IsCountUpTimer => TimerMode == "CountUp";

    [JsonIgnore]
    public bool IsCountdownTimer => !IsCountUpTimer;

    [JsonIgnore]
    public Visibility PlannedTimeVisibility => IsCountdownTimer ? Visibility.Visible : Visibility.Collapsed;

    [JsonIgnore]
    public bool CanEdit => !IsReadOnly && !IsFreeTask;

    [JsonIgnore]
    public bool IsRecurringTask => !string.IsNullOrWhiteSpace(RecurringTaskId);

    [JsonIgnore]
    public bool CanCompleteManually => IsCountUpTimer && !IsSkipped && !IsReadOnly && !IsFreeTask;

    [JsonIgnore]
    public Visibility ManualCompleteButtonVisibility =>
        IsCountUpTimer && !IsSkipped && !IsFreeTask ? Visibility.Visible : Visibility.Collapsed;

    [JsonIgnore]
    public string ManualCompleteToolTip => IsManuallyCompleted ? "撤销完成" : "完成每日任务";

    [JsonIgnore]
    public bool CanTransferFreeTime => !IsReadOnly && IsFreeTask && ActualMinutes > 0;

    [JsonIgnore]
    public Visibility RemoveButtonVisibility => IsFreeTask ? Visibility.Collapsed : Visibility.Visible;

    [JsonIgnore]
    public Visibility TransferButtonVisibility => IsFreeTask ? Visibility.Visible : Visibility.Collapsed;

    [JsonIgnore]
    public string PlannedHoursText
    {
        get => _plannedHoursText;
        set
        {
            string cleanedValue = KeepDigits(value);
            if (_plannedHoursText == cleanedValue)
            {
                return;
            }

            _plannedHoursText = cleanedValue;
            _plannedHours = ParseNonNegativeInt(cleanedValue);
            UpdateLegacyPlannedText();
            NotifyPlannedChanged();
        }
    }

    [JsonIgnore]
    public string PlannedMinutesText
    {
        get => _plannedMinutesText;
        set
        {
            string cleanedValue = KeepDigits(value);
            if (_plannedMinutesText == cleanedValue)
            {
                return;
            }

            _plannedMinutesText = cleanedValue;
            _plannedMinutes = ParseNonNegativeInt(cleanedValue);
            UpdateLegacyPlannedText();
            NotifyPlannedChanged();
        }
    }

    [JsonIgnore]
    public int PlannedTotalMinutes => (_plannedHours * 60) + _plannedMinutes;

    [JsonIgnore]
    public bool IsCompleted =>
        IsManuallyCompleted ||
        (IsCountdownTimer && !IsFreeTask && PlannedTotalMinutes > 0 && ActualMinutes >= PlannedTotalMinutes);

    [JsonIgnore]
    public bool IsEmpty =>
        IsFreeTask
            ? ActualMinutes <= 0
            : string.IsNullOrWhiteSpace(Name) &&
              !IsRecurringTask &&
              PlannedTotalMinutes <= 0 &&
              string.IsNullOrWhiteSpace(_plannedText) &&
              ActualMinutes <= 0;

    [JsonIgnore]
    public string ActualDisplay
    {
        get
        {
            if (IsCompleted)
            {
                return "已完成";
            }

            if (ActualMinutes <= 0)
            {
                return string.Empty;
            }

            return $"已 {MainWindow.FormatDuration(ActualMinutes)}";
        }
    }

    [JsonIgnore]
    public string ActualForeground => IsCompleted ? "#2FB344" : "#8FA0AF";

    [JsonIgnore]
    public Visibility ActualVisibility => ActualMinutes > 0 || IsManuallyCompleted ? Visibility.Visible : Visibility.Collapsed;

    public void Normalize()
    {
        Name ??= string.Empty;
        PlannedText ??= string.Empty;
        RecurringTaskId ??= string.Empty;
        LongTermTaskId ??= string.Empty;
        if (string.IsNullOrWhiteSpace(_timerMode))
        {
            _timerMode = IsRecurringTask ? "CountUp" : "Countdown";
        }
        else
        {
            TimerMode = TimerMode;
        }
        ProgressIncrement = Math.Max(0, ProgressIncrement);

        if (IsFreeTask)
        {
            NormalizeFreeTask(MainWindow.FREE_STUDY_TASK_NAME);
            ActualMinutes = Math.Max(0, ActualMinutes);
            return;
        }

        if (PlannedTotalMinutes <= 0)
        {
            int parsedMinutes = MainWindow.ParsePlannedMinutes(_plannedText);
            if (parsedMinutes > 0)
            {
                SetPlannedFromMinutes(parsedMinutes);
            }
        }

        NormalizePlannedTime();
        ActualMinutes = Math.Max(0, ActualMinutes);
        NotifyTimerModeChanged();
    }

    public void ToggleManualCompletion()
    {
        if (CanCompleteManually)
        {
            IsManuallyCompleted = !IsManuallyCompleted;
        }
    }

    public void SkipOccurrence()
    {
        if (IsRecurringTask)
        {
            IsSkipped = true;
        }
    }

    public void NormalizePlannedTime()
    {
        SetPlannedFromMinutes(PlannedTotalMinutes);
    }

    public void NormalizeFreeTask(string name)
    {
        IsFreeTask = true;
        IsHidden = false;
        Name = name;
        SetPlannedFromMinutes(0);
        OnPropertyChanged(nameof(IsInputReadOnly));
        OnPropertyChanged(nameof(CanEdit));
        OnPropertyChanged(nameof(CanTransferFreeTime));
        OnPropertyChanged(nameof(RemoveButtonVisibility));
        OnPropertyChanged(nameof(TransferButtonVisibility));
    }

    private void OnPropertyChanged([CallerMemberName] string? propertyName = null)
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }

    private void SetPlannedTime(int hours, int minutes)
    {
        _plannedHours = Math.Max(0, hours);
        _plannedMinutes = Math.Max(0, minutes);
        _plannedHoursText = _plannedHours > 0 ? _plannedHours.ToString(CultureInfo.InvariantCulture) : string.Empty;
        _plannedMinutesText = _plannedMinutes > 0 ? _plannedMinutes.ToString(CultureInfo.InvariantCulture) : string.Empty;
        UpdateLegacyPlannedText();
        NotifyPlannedChanged();
    }

    private void SetPlannedFromMinutes(int totalMinutes)
    {
        int safeTotalMinutes = Math.Max(0, totalMinutes);
        _plannedHours = safeTotalMinutes / 60;
        _plannedMinutes = safeTotalMinutes % 60;
        _plannedHoursText = _plannedHours > 0 ? _plannedHours.ToString(CultureInfo.InvariantCulture) : string.Empty;
        _plannedMinutesText = _plannedMinutes > 0 ? _plannedMinutes.ToString(CultureInfo.InvariantCulture) : string.Empty;
        UpdateLegacyPlannedText();
        NotifyPlannedChanged();
    }

    private void UpdateLegacyPlannedText()
    {
        _plannedText = PlannedTotalMinutes > 0 ? MainWindow.FormatDuration(PlannedTotalMinutes) : string.Empty;
    }

    private void NotifyTimerModeChanged()
    {
        OnPropertyChanged(nameof(TimerMode));
        OnPropertyChanged(nameof(IsCountUpTimer));
        OnPropertyChanged(nameof(IsCountdownTimer));
        OnPropertyChanged(nameof(PlannedTimeVisibility));
        OnPropertyChanged(nameof(IsCompleted));
        OnPropertyChanged(nameof(CanCompleteManually));
        OnPropertyChanged(nameof(ManualCompleteButtonVisibility));
        OnPropertyChanged(nameof(ManualCompleteToolTip));
        OnPropertyChanged(nameof(ActualDisplay));
        OnPropertyChanged(nameof(ActualForeground));
        OnPropertyChanged(nameof(ActualVisibility));
    }

    private void NotifyPlannedChanged()
    {
        OnPropertyChanged(nameof(PlannedText));
        OnPropertyChanged(nameof(PlannedHours));
        OnPropertyChanged(nameof(PlannedMinutes));
        OnPropertyChanged(nameof(PlannedHoursText));
        OnPropertyChanged(nameof(PlannedMinutesText));
        OnPropertyChanged(nameof(PlannedTotalMinutes));
        OnPropertyChanged(nameof(IsCompleted));
        OnPropertyChanged(nameof(ActualDisplay));
        OnPropertyChanged(nameof(ActualForeground));
        OnPropertyChanged(nameof(ActualVisibility));
        OnPropertyChanged(nameof(IsEmpty));
    }

    private void NotifyCompletionChanged()
    {
        OnPropertyChanged(nameof(IsManuallyCompleted));
        OnPropertyChanged(nameof(IsCompleted));
        OnPropertyChanged(nameof(CanCompleteManually));
        OnPropertyChanged(nameof(ManualCompleteButtonVisibility));
        OnPropertyChanged(nameof(ManualCompleteToolTip));
        OnPropertyChanged(nameof(ActualDisplay));
        OnPropertyChanged(nameof(ActualForeground));
        OnPropertyChanged(nameof(ActualVisibility));
        OnPropertyChanged(nameof(IsEmpty));
    }

    private static int ParseNonNegativeInt(string value)
    {
        return int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out int parsedValue)
            ? Math.Max(0, parsedValue)
            : 0;
    }

    private static string KeepDigits(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return string.Empty;
        }

        StringBuilder builder = new();
        foreach (char character in value)
        {
            if (char.IsDigit(character))
            {
                builder.Append(character);
            }
        }

        return builder.ToString();
    }
}

public sealed class StatisticsBar
{
    public string Label { get; init; } = string.Empty;

    public string Value { get; init; } = string.Empty;

    public double Height { get; init; }
}

public sealed class CalendarDayViewModel
{
    public DateTime Date { get; init; }

    public string DayText { get; init; } = string.Empty;

    public string TimeText { get; init; } = string.Empty;

    public Visibility TimeVisibility { get; init; } = Visibility.Collapsed;

    public Brush Background { get; init; } = Brushes.Transparent;

    public Brush BorderBrush { get; init; } = Brushes.Transparent;

    public Brush Foreground { get; init; } = Brushes.White;

    public Brush TimeForeground { get; init; } = Brushes.White;

    public FontWeight FontWeight { get; init; } = FontWeights.Normal;

    public Brush DotBrush { get; init; } = Brushes.White;

    public Visibility DotVisibility { get; init; } = Visibility.Collapsed;
}
