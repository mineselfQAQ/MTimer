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
    private const double COMPACT_WIDTH = 230;
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

    private sealed record FreeTaskTransfer(PlanTask FreeTask, PlanTask TargetTask);

    public MainWindow()
    {
        _currentBusinessDate = GetBusinessDate(DateTime.Now);
        _displayMonth = new DateTime(_currentBusinessDate.Year, _currentBusinessDate.Month, 1);
        _selectedDate = _currentBusinessDate;

        InitializeComponent();
        PlanTaskItems.ItemsSource = _selectedTasks;
        LongTaskItems.ItemsSource = _longTermTasks;
        InitializeTimer();

        LoadDailyEntries();
        LoadLongTermTasks();
        ApplyLegacyCountToCurrentDate(LoadSavedCount());
        UpdateMinuteCountFromCurrentBusinessDate();
        UpdateCounterDisplay();
        SetIndicator(IdleBrush);
        RenderCalendar();
        LoadSelectedEntry();
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
        trackedTask.ActualMinutes++;
        EnsureSelectedTaskVisible(trackedTask);

        if (trackedTask.IsFreeTask || IsTaskCompleted(trackedTask))
        {
            SelectCurrentFreeTask();
        }

        UpdateCounterDisplay();
        SaveCurrentCount();
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

    private void ExpandBtn_Click(object sender, RoutedEventArgs e)
    {
        SetExpanded(true);
    }

    private void CollapseBtn_Click(object sender, RoutedEventArgs e)
    {
        SaveSelectedEntry();
        SetExpanded(false);
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
        RenderCalendar();
        LoadSelectedEntry();
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

        _selectedTasks.Remove(task);
        SaveSelectedEntry();
        RenderCalendar();
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
        _longTermTasks.Add(new LongTermTask());
    }

    private void RemoveLongTaskBtn_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: LongTermTask task })
        {
            return;
        }

        _longTermTasks.Remove(task);
        SaveLongTermTasks();
    }

    private void AddLongTaskToTodayBtn_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: LongTermTask task } ||
            string.IsNullOrWhiteSpace(task.Name))
        {
            return;
        }

        SaveSelectedEntry();

        DateTime targetDate = _currentBusinessDate.Date;
        DailyEntry entry = NormalizeEntry(GetEntry(targetDate, create: true)!);
        entry.Tasks.Add(new PlanTask
        {
            Name = task.Name.Trim()
        });
        entry.Plan = BuildLegacyPlan(entry.Tasks);

        _selectedDate = targetDate;
        _displayMonth = new DateTime(targetDate.Year, targetDate.Month, 1);
        SaveDailyEntries();
        RenderCalendar();
        LoadSelectedEntry();
        RefreshCurrentTaskDisplay();
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
        }

        SaveLongTermTasks();
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
            RenderCalendar();
            LoadSelectedEntry();
        }
        else
        {
            RefreshCurrentTaskDisplay();
        }
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
    }

    private void LoadSelectedEntry()
    {
        _isLoadingEntry = true;

        DailyEntry entry = GetEntry(_selectedDate, create: false) ?? new DailyEntry();
        NormalizeEntry(entry);

        _selectedTasks.Clear();
        bool planEditable = IsPlanEditable(_selectedDate);
        foreach (PlanTask task in entry.Tasks.Where(task => !task.IsHidden && !task.IsEmpty))
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

        List<PlanTask> visibleTasks = _selectedTasks
            .Where(task => !task.IsHidden && !task.IsEmpty)
            .ToList();
        int visiblePlannedMinutes = visibleTasks.Sum(task => task.PlannedTotalMinutes);
        int totalPlannedMinutes = ReadTotalPlanMinutes();
        if (totalPlannedMinutes <= 0 && visiblePlannedMinutes > 0)
        {
            totalPlannedMinutes = visiblePlannedMinutes;
            SetTotalPlanInputs(totalPlannedMinutes);
        }

        entry.TotalPlannedMinutes = totalPlannedMinutes;
        entry.Tasks = visibleTasks;
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
        RenderCalendar();
        LoadSelectedEntry();
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
    }

    private void RefreshCurrentTaskState(PlanTask task)
    {
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

    private bool HasEntry(DateTime date)
    {
        DailyEntry? entry = GetEntry(date, create: false);
        return entry != null && !IsEntryEmpty(entry);
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
               task.PlannedTotalMinutes > 0 &&
               task.ActualMinutes >= task.PlannedTotalMinutes;
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
    private string _name = string.Empty;
    private string _note = string.Empty;
    private bool _isNoteEditing;
    private int _progress;
    private string _progressText = "0";

    public event PropertyChangedEventHandler? PropertyChanged;

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

    public int Progress
    {
        get => _progress;
        set => SetProgress(value);
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

    [JsonIgnore]
    public string ProgressText
    {
        get => _progressText;
        set
        {
            string cleanedValue = KeepDigits(value);
            int progress = ClampProgress(ParseNonNegativeInt(cleanedValue));
            string nextText = string.IsNullOrWhiteSpace(cleanedValue)
                ? string.Empty
                : progress.ToString(CultureInfo.InvariantCulture);

            if (_progress == progress && _progressText == nextText)
            {
                return;
            }

            _progress = progress;
            _progressText = nextText;
            NotifyProgressChanged();
        }
    }

    [JsonIgnore]
    public bool IsEmpty =>
        string.IsNullOrWhiteSpace(Name) &&
        string.IsNullOrWhiteSpace(Note);

    [JsonIgnore]
    public bool CanAddToToday => !string.IsNullOrWhiteSpace(Name);

    [JsonIgnore]
    public bool IsNoteReadOnly => !_isNoteEditing;

    public void Normalize()
    {
        Name ??= string.Empty;
        Note ??= string.Empty;
        EndNoteEdit();
        SetProgress(Progress);
    }

    public void NormalizeProgress()
    {
        SetProgress(Progress);
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

    private void SetProgress(int progress)
    {
        int safeProgress = ClampProgress(progress);
        string nextText = safeProgress.ToString(CultureInfo.InvariantCulture);
        if (_progress == safeProgress && _progressText == nextText)
        {
            return;
        }

        _progress = safeProgress;
        _progressText = nextText;
        NotifyProgressChanged();
    }

    private void NotifyProgressChanged()
    {
        OnPropertyChanged(nameof(Progress));
        OnPropertyChanged(nameof(ProgressText));
    }

    private void OnPropertyChanged([CallerMemberName] string? propertyName = null)
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }

    private static int ClampProgress(int progress)
    {
        return Math.Clamp(progress, 0, 100);
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

    public event PropertyChangedEventHandler? PropertyChanged;

    public bool IsHidden { get; set; }

    public bool IsFreeTask { get; set; }

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
        }
    }

    [JsonIgnore]
    public bool IsInputReadOnly => IsReadOnly || IsFreeTask;

    [JsonIgnore]
    public bool CanEdit => !IsReadOnly && !IsFreeTask;

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
        !IsFreeTask &&
        PlannedTotalMinutes > 0 &&
        ActualMinutes >= PlannedTotalMinutes;

    [JsonIgnore]
    public bool IsEmpty =>
        IsFreeTask
            ? ActualMinutes <= 0
            : string.IsNullOrWhiteSpace(Name) &&
              PlannedTotalMinutes <= 0 &&
              string.IsNullOrWhiteSpace(_plannedText) &&
              ActualMinutes <= 0;

    [JsonIgnore]
    public string ActualDisplay
    {
        get
        {
            if (ActualMinutes <= 0)
            {
                return string.Empty;
            }

            return IsCompleted ? "已完成" : $"已 {MainWindow.FormatDuration(ActualMinutes)}";
        }
    }

    [JsonIgnore]
    public string ActualForeground => IsCompleted ? "#2FB344" : "#8FA0AF";

    [JsonIgnore]
    public Visibility ActualVisibility => ActualMinutes > 0 ? Visibility.Visible : Visibility.Collapsed;

    public void Normalize()
    {
        Name ??= string.Empty;
        PlannedText ??= string.Empty;

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
