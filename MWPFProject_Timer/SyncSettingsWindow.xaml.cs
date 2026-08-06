using System.IO;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using MWPFProject_Timer.Sync;

namespace MWPFProject_Timer;

public partial class SyncSettingsWindow : Window
{
    internal SyncSettingsWindow(TimerSyncOptions options)
    {
        InitializeComponent();
        DeviceNameTextBox.Text = options.DeviceName;
    }

    internal TimerSyncOptions? SavedOptions { get; private set; }

    private void SaveButton_Click(object sender, RoutedEventArgs e)
    {
        if (!TimerSyncOptions.TryCreateFromUserInput(
                DeviceNameTextBox.Text,
                out TimerSyncOptions? options,
                out string errorMessage))
        {
            ValidationTextBlock.Text = errorMessage;
            ValidationTextBlock.Foreground = new SolidColorBrush(Color.FromRgb(255, 138, 128));
            return;
        }

        SavedOptions = options;
        DialogResult = true;
    }

    private void CancelButton_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
    }

    private void HeaderPanel_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ButtonState == MouseButtonState.Pressed)
        {
            DragMove();
        }
    }

    internal void RenderVerificationPng(string outputPath)
    {
        RootBorder.Measure(new Size(Width, Height));
        RootBorder.Arrange(new Rect(0, 0, Width, Height));
        RootBorder.UpdateLayout();

        RenderTargetBitmap bitmap = new(
            (int)Width,
            (int)Height,
            96,
            96,
            PixelFormats.Pbgra32);
        bitmap.Render(RootBorder);

        string? outputDirectory = Path.GetDirectoryName(outputPath);
        if (string.IsNullOrWhiteSpace(outputDirectory))
        {
            throw new InvalidOperationException("验证截图必须指定输出目录。");
        }

        Directory.CreateDirectory(outputDirectory);
        PngBitmapEncoder encoder = new();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using FileStream stream = File.Create(outputPath);
        encoder.Save(stream);
    }
}
