using System.ComponentModel;
using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Navigation;
using System.Windows.Shapes;
using System.Windows.Threading;

namespace MWPFProject_Timer;

/// <summary>
/// Interaction logic for MainWindow.xaml
/// </summary>
public partial class MainWindow : Window
{
    private const int INTERVAL_TIME = 1;
    private const string COUNT_FILE = "minute_count.txt";

    private DispatcherTimer? _minuteTimer = null;
    private int _minuteCount = 0;
    private double _speedFactor = 1.0;

    public MainWindow()
    {
        InitializeComponent();
        InitializeTimer();

        LoadSavedCount();

        if (MinuteCounter != null)
        {
            MinuteCounter.Text = TimeSpan.FromMinutes(_minuteCount).ToString(@"hh\:mm");
        }
    }
    private void InitializeTimer()
    {
        _minuteTimer = new DispatcherTimer();
        UpdateTimerInterval();
        _minuteTimer.Tick += MinuteTimer_Tick;
    }

    private void MinuteTimer_Tick(object? sender, EventArgs e)
    {
        _minuteCount++;

        TimeSpan time = TimeSpan.FromMinutes(_minuteCount);
        MinuteCounter.Text = time.ToString(@"hh\:mm");

        SaveCurrentCount();
    }

    private void StartBtn_Click(object sender, RoutedEventArgs e)
    {
        if (_minuteTimer != null && !_minuteTimer.IsEnabled)
        {
            if (double.TryParse(FactorBox.Text, out double speed) && speed > 0)
            {
                _speedFactor = speed;
            }
            else
            {
                _speedFactor = 1;
                FactorBox.Text = "1";
            }

            UpdateTimerInterval();
            SpeedIndicator.Text = $"x{_speedFactor}";

            _minuteTimer.Start();
            Indicator.Background = Brushes.Green;
        }
    }

    private void StopBtn_Click(object sender, RoutedEventArgs e)
    {
        if (_minuteTimer != null && _minuteTimer.IsEnabled)
        {
            _minuteTimer.Stop();
            Indicator.Background = Brushes.Red;

            SaveCurrentCount();
        }
    }

    private void ResetBtn_Click(object sender, RoutedEventArgs e)
    {
        _minuteTimer?.Stop();
        _minuteCount = 0;
        MinuteCounter.Text = "00:00";
        Indicator.Background = Brushes.White;

        if (File.Exists(COUNT_FILE))
        {
            File.Delete(COUNT_FILE);
        }
    }

    private void Window_MouseDown(object? sender, MouseButtonEventArgs e)
    {
        if (e.LeftButton == MouseButtonState.Pressed &&
            e.ClickCount == 1)
        {
            this.DragMove();
        }
    }

    private void LoadSavedCount()
    {
        if (File.Exists(COUNT_FILE))
        {
            try
            {
                string content = File.ReadAllText(COUNT_FILE);
                if (int.TryParse(content, out int savedCount))
                {
                    _minuteCount = savedCount;
                }
            }
            catch
            {
                _minuteCount = 0;
            }
        }
    }

    private void SaveCurrentCount()
    {
        try
        {
            File.WriteAllText(COUNT_FILE, _minuteCount.ToString());
        }
        catch { }
    }

    private void UpdateTimerInterval()
    {
        if (_minuteTimer == null) return;

        double intervalMinutes = INTERVAL_TIME / _speedFactor;
        _minuteTimer.Interval = TimeSpan.FromMinutes(intervalMinutes);
    }
}