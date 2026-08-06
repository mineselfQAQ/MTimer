using System.Configuration;
using System.Data;
using System.IO;
using System.Threading;
using System.Windows;

namespace MWPFProject_Timer;

/// <summary>
/// Interaction logic for App.xaml
/// </summary>
public partial class App : Application
{
    private static Mutex? _mainInstanceMutex;
    private static bool _ownsMainInstance;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        bool isUiVerification = UiVerificationRequest.IsRequested(e.Args);
        if (!isUiVerification)
        {
            _mainInstanceMutex = new Mutex(
                initiallyOwned: true,
                name: @"Local\MCodexCore.MTimer",
                createdNew: out _ownsMainInstance);

            if (!_ownsMainInstance)
            {
                Shutdown(0);
                return;
            }
        }

        if (isUiVerification)
        {
            ShutdownMode = ShutdownMode.OnExplicitShutdown;
            UiVerificationRequest? request = null;
            try
            {
                request = UiVerificationRequest.Parse(e.Args);
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
            catch (Exception exception)
            {
                if (request != null)
                {
                    File.WriteAllText(
                        Path.Combine(request.DataRoot, "ui-verification-error.txt"),
                        exception.ToString());
                }

                Shutdown(1);
            }

            return;
        }

        MainWindow mainWindow = new();
        MainWindow = mainWindow;
        mainWindow.Show();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        try
        {
            if (_ownsMainInstance)
            {
                _mainInstanceMutex?.ReleaseMutex();
            }
        }
        finally
        {
            _mainInstanceMutex?.Dispose();
            _mainInstanceMutex = null;
            _ownsMainInstance = false;
            base.OnExit(e);
        }
    }
}

