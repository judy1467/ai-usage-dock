using System;
using System.IO;
using System.Threading;
using System.Windows;

namespace AiUsageDock.App;

/// <summary>
/// Interaction logic for App.xaml
/// </summary>
public partial class App : Application
{
    private static readonly string LogPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "AiUsageDock",
        "startup.log");

    private Mutex? _instanceMutex;

    protected override void OnStartup(StartupEventArgs e)
    {
        // Single instance: an already running dock must not be duplicated.
        _instanceMutex = new Mutex(initiallyOwned: true, @"Local\AiUsageDock.SingleInstance", out var createdNew);
        if (!createdNew)
        {
            MessageBox.Show(
                "AI Usage Dock이 이미 실행 중입니다.\n작업표시줄 오른쪽의 독을 확인하세요.",
                "AI Usage Dock",
                MessageBoxButton.OK,
                MessageBoxImage.Information);
            Shutdown(0);
            return;
        }

        DispatcherUnhandledException += (_, exArgs) =>
        {
            LogException(exArgs.Exception);
            exArgs.Handled = true;
        };

        AppDomain.CurrentDomain.UnhandledException += (_, exArgs) =>
        {
            if (exArgs.ExceptionObject is Exception ex)
            {
                LogException(ex);
            }
        };

        base.OnStartup(e);

        var window = new MainWindow();
        MainWindow = window;
        window.Show();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _instanceMutex?.Dispose();
        _instanceMutex = null;
        base.OnExit(e);
    }

    private static void LogException(Exception ex)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(LogPath)!);
            File.AppendAllText(LogPath, $"[{DateTime.Now:O}] {ex}{Environment.NewLine}{Environment.NewLine}");
        }
        catch
        {
            // Never let logging failures crash the app.
        }
    }
}
