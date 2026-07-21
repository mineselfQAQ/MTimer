using System.Configuration;
using System.Data;
using System.Windows;

namespace MWPFProject_Timer;

/// <summary>
/// Interaction logic for App.xaml
/// </summary>
public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        if (UiVerificationRequest.IsRequested(e.Args))
        {
            ShutdownMode = ShutdownMode.OnExplicitShutdown;
            try
            {
                UiVerificationRequest request = UiVerificationRequest.Parse(e.Args);
                TimerDataPaths dataPaths = new(request.DataRoot);
                UiVerificationFixture.Write(dataPaths);

                MainWindow verificationWindow = new(
                    dataPaths,
                    UiVerificationFixture.BusinessDate,
                    startTimer: false);
                verificationWindow.RenderVerificationPng(
                    request.Scenario,
                    request.OutputPath);

                Shutdown(0);
            }
            catch
            {
                Shutdown(1);
            }

            return;
        }

        MainWindow mainWindow = new();
        MainWindow = mainWindow;
        mainWindow.Show();
    }
}

