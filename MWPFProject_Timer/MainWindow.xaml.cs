using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.IO;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
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

    private DispatcherTimer? _minuteTimer;
    private int _minuteCount;
    private double _speedFactor = 1.0;
    private DateTime _currentBusinessDate;
    private DateTime _displayMonth;
    private DateTime _selectedDate;
    private int _currentTaskIndex;
    private bool _isLoadingEntry = true;
    private bool _isUpdatingTotalPlan;

    public MainWindow()
    {
        _currentBusinessDate = GetBusinessDate(DateTime.Now);
        _displayMonth = new DateTime(_currentBusinessDate.Year, _currentBusinessDate.Month, 1);
        _selectedDate = _currentBusinessDate;

        InitializeComponent();
        PlanTaskItems.ItemsSource = _selectedTasks;
        InitializeTimer();

        LoadDailyEntries();
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
        bool shouldStopForCompletedTask = false;
        if (activeTask != null)
        {
            activeTask.ActualMinutes++;
            shouldStopForCompletedTask = IsTaskCompleted(activeTask);
        }

        UpdateCounterDisplay();
        SaveCurrentCount();
        SaveDailyEntries();
        RenderCalendar();
        RefreshCurrentTaskDisplay();

        if (shouldStopForCompletedTask)
        {
            _minuteTimer?.Stop();
            SetIndicator(StoppedBrush);
        }
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
                RefreshCurrentTaskDisplay();
                return;
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

    private void ResetBtn_Click(object sender, RoutedEventArgs e)
    {
        _minuteTimer?.Stop();
        RefreshBusinessDateIfNeeded();

        DailyEntry? entry = GetEntry(_currentBusinessDate, create: false);
        if (entry != null)
        {
            entry.ActualMinutes = 0;
            foreach (PlanTask task in entry.Tasks)
            {
                task.ActualMinutes = 0;
            }
        }

        _minuteCount = 0;
        UpdateCounterDisplay();
        SetIndicator(IdleBrush);
        RenderCalendar();
        RefreshCurrentTaskDisplay();
        SaveDailyEntries();

        if (File.Exists(COUNT_FILE))
        {
            File.Delete(COUNT_FILE);
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
            sender is not Button { Tag: PlanTask task })
        {
            return;
        }

        _selectedTasks.Remove(task);
        SaveSelectedEntry();
        RenderCalendar();
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
            task.IsReadOnly)
        {
            return;
        }

        textBox.GetBindingExpression(TextBox.TextProperty)?.UpdateSource();
        task.NormalizePlannedTime();

        SaveSelectedEntry();
        RenderCalendar();
        RefreshCurrentTaskDisplay();
    }

    private void EntryTextChanged(object sender, TextChangedEventArgs e)
    {
        if (_isLoadingEntry)
        {
            return;
        }

        SaveSelectedEntry();
        RenderCalendar();
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
        SaveCurrentCount();
        SaveDailyEntries();
    }

    private void SetExpanded(bool expanded)
    {
        SaveSelectedEntry();

        CompactPanel.Visibility = expanded ? Visibility.Collapsed : Visibility.Visible;
        ExpandedPanel.Visibility = expanded ? Visibility.Visible : Visibility.Collapsed;
        RootBorder.Background = expanded ? ExpandedRootBackground : CompactRootBackground;

        SetFixedWindowSize(
            expanded ? EXPANDED_WIDTH : COMPACT_WIDTH,
            expanded ? EXPANDED_HEIGHT : COMPACT_HEIGHT);

        if (expanded)
        {
            CenterWindowInWorkArea();
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

    private void CenterWindowInWorkArea()
    {
        Rect workArea = SystemParameters.WorkArea;
        Left = workArea.Left + ((workArea.Width - Width) / 2);
        Top = workArea.Top + ((workArea.Height - Height) / 2);
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
        foreach (PlanTask task in entry.Tasks.Where(task => !task.IsHidden))
        {
            task.IsReadOnly = !planEditable;
            _selectedTasks.Add(task);
        }

        SetTotalPlanInputs(GetEffectivePlannedMinutes(entry));
        SummaryBox.Text = entry.Summary;
        SelectedDateTitle.Text = _selectedDate.ToString("yyyy年M月d日 dddd", ZhCn);

        AddTaskBtn.IsEnabled = planEditable;
        TotalHoursBox.IsReadOnly = !planEditable;
        TotalMinutesBox.IsReadOnly = !planEditable;
        TotalHoursBox.Opacity = planEditable ? 1 : 0.68;
        TotalMinutesBox.Opacity = planEditable ? 1 : 0.68;
        bool summaryEditable = IsSummaryEditable(_selectedDate);
        SummaryBox.IsReadOnly = !summaryEditable;
        SummaryBox.Opacity = summaryEditable ? 1 : 0.68;

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
            .Where(task => !task.IsEmpty)
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
        entry.Summary = SummaryBox.Text;

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
            CurrentTaskName.Text = "未设置";
            CurrentTaskState.Text = "无计划";
            CurrentTaskState.Foreground = RemainingBrush;
            TaskUpBtn.IsEnabled = false;
            TaskDownBtn.IsEnabled = false;
            return;
        }

        if (_currentTaskIndex >= tasks.Count)
        {
            _currentTaskIndex = tasks.Count - 1;
        }

        PlanTask task = tasks[_currentTaskIndex];
        CurrentTaskName.Text = task.Name.Trim();
        RefreshCurrentTaskState(task);
        TaskUpBtn.IsEnabled = tasks.Count > 1;
        TaskDownBtn.IsEnabled = tasks.Count > 1;
    }

    private void RefreshCurrentTaskState(PlanTask task)
    {
        int plannedMinutes = task.PlannedTotalMinutes;
        if (plannedMinutes <= 0)
        {
            CurrentTaskState.Text = "未设置时长";
            CurrentTaskState.Foreground = RemainingBrush;
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
        return entry?.Tasks
            .Where(task => !string.IsNullOrWhiteSpace(task.Name))
            .ToList() ?? new List<PlanTask>();
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

        return FormatCompactHours(displayMinutes);
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

    private bool IsSummaryEditable(DateTime date)
    {
        return date.Date == _currentBusinessDate.Date;
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
                .Where(task => !task.IsHidden)
                .Sum(task => task.PlannedTotalMinutes);
        }

        EnsureHiddenTask(entry, entry.Tasks.FirstOrDefault(task => task.IsHidden));
        entry.Summary ??= string.Empty;
        entry.Plan ??= string.Empty;
        return entry;
    }

    private static bool IsEntryEmpty(DailyEntry entry)
    {
        return entry.ActualMinutes <= 0 &&
               entry.TotalPlannedMinutes <= 0 &&
               string.IsNullOrWhiteSpace(entry.Summary) &&
               entry.Tasks.All(task => task.IsEmpty);
    }

    private static string BuildLegacyPlan(IEnumerable<PlanTask> tasks)
    {
        return string.Join(
            Environment.NewLine,
            tasks.Where(task => !task.IsHidden).Select(task =>
                string.IsNullOrWhiteSpace(task.PlannedText)
                    ? task.Name.Trim()
                    : $"{task.Name.Trim()} {task.PlannedText.Trim()}"));
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
            .Where(task => !task.IsHidden)
            .Sum(task => task.PlannedTotalMinutes);
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
            Name = "预留时间",
            IsHidden = true,
            ActualMinutes = existingHiddenTask?.ActualMinutes ?? 0
        };
        hiddenTask.PlannedHours = hiddenMinutes / 60;
        hiddenTask.PlannedMinutes = hiddenMinutes % 60;
        entry.Tasks.Add(hiddenTask);
    }

    private static bool IsTaskCompleted(PlanTask? task)
    {
        return task != null &&
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

    private static string FormatCompactHours(int minutes)
    {
        if (minutes <= 0)
        {
            return string.Empty;
        }

        double hours = Math.Round(minutes / 60.0, 2);
        return hours.ToString("0.##", CultureInfo.InvariantCulture);
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
            OnPropertyChanged(nameof(ActualDisplay));
            OnPropertyChanged(nameof(ActualVisibility));
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
            OnPropertyChanged(nameof(CanEdit));
        }
    }

    [JsonIgnore]
    public bool CanEdit => !IsReadOnly;

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
    public bool IsEmpty =>
        string.IsNullOrWhiteSpace(Name) &&
        PlannedTotalMinutes <= 0 &&
        string.IsNullOrWhiteSpace(_plannedText) &&
        ActualMinutes <= 0;

    [JsonIgnore]
    public string ActualDisplay => ActualMinutes > 0 ? $"已 {MainWindow.FormatDuration(ActualMinutes)}" : string.Empty;

    [JsonIgnore]
    public Visibility ActualVisibility => ActualMinutes > 0 ? Visibility.Visible : Visibility.Collapsed;

    public void Normalize()
    {
        Name ??= string.Empty;
        PlannedText ??= string.Empty;

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
