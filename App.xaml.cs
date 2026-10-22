using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Diagnostics;
using System.Runtime.InteropServices;

namespace Sharm;

public partial class App : Application
{
    private Mutex? instanceMutex;
    private bool ownsMutex;
    [DllImport("user32.dll")] private static extern bool SetForegroundWindow(IntPtr window);
    [DllImport("user32.dll")] private static extern bool ShowWindow(IntPtr window, int command);
    public static string ProfilePath { get; } = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Sharm");
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        if (!e.Args.Contains("--self-test"))
        {
            instanceMutex = new Mutex(true, "Local\\Sharm.Browser.Instance", out ownsMutex);
            if (!ownsMutex)
            {
                var current = Environment.ProcessId;
                var existing = Process.GetProcessesByName("Sharm").FirstOrDefault(p => p.Id != current && p.MainWindowHandle != IntPtr.Zero);
                if (existing is not null) { ShowWindow(existing.MainWindowHandle, 9); SetForegroundWindow(existing.MainWindowHandle); }
                Shutdown(); return;
            }
        }
        Directory.CreateDirectory(ProfilePath);
        DispatcherUnhandledException += (_, args) =>
        {
            File.AppendAllText(Path.Combine(ProfilePath, "errors.log"), $"{DateTimeOffset.Now:O} process={Environment.ProcessId} {args.Exception}\n");
            if (e.Args.Contains("--self-test"))
            {
                File.AppendAllText(Path.Combine(ProfilePath, "self-test.txt"), "\nFAIL unhandled: " + args.Exception.Message + "\n");
                args.Handled = true; Shutdown(1); return;
            }
            MessageBox.Show(args.Exception.Message, "Sharm", MessageBoxButton.OK, MessageBoxImage.Error);
            args.Handled = true;
        };
        new MainWindow(e.Args).Show();
    }
    protected override void OnExit(ExitEventArgs e)
    {
        if (ownsMutex) instanceMutex?.ReleaseMutex();
        instanceMutex?.Dispose();
        base.OnExit(e);
    }
}
