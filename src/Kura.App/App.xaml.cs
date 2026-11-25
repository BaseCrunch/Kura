using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using Kura.App.Services;
using Kura.App.ViewModels;
using Kura.App.Views;

namespace Kura.App;

public partial class App : Application
{
    private AppServices? _services;
    private readonly Dictionary<ContextMenu, IDisposable> _menuTokens = [];

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        DispatcherUnhandledException += OnDispatcherException;
        TaskScheduler.UnobservedTaskException += (_, args) => { Log(args.Exception); args.SetObserved(); };

        try
        {
            _services = new AppServices();
        }
        catch (Exception ex)
        {
            Log(ex);
            MessageBox.Show($"Kura couldn't start:\n\n{ex.Message}", "Kura", MessageBoxButton.OK, MessageBoxImage.Error);
            Shutdown(1);
            return;
        }

        // Any context menu counts as an overlay for the video airspace rule.
        EventManager.RegisterClassHandler(typeof(ContextMenu), ContextMenu.OpenedEvent, new RoutedEventHandler((s, _) =>
        {
            if (s is ContextMenu m && !_menuTokens.ContainsKey(m)) _menuTokens[m] = _services.Overlay.Enter();
        }));
        EventManager.RegisterClassHandler(typeof(ContextMenu), ContextMenu.ClosedEvent, new RoutedEventHandler((s, _) =>
        {
            if (s is ContextMenu m && _menuTokens.Remove(m, out var t)) t.Dispose();
        }));

        var main = new MainWindow { DataContext = new MainViewModel(_services) };
        MainWindow = main;
        main.Show();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _services?.Dispose();
        base.OnExit(e);
    }

    private void OnDispatcherException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        Log(e.Exception);
        e.Handled = true;
        MessageBox.Show($"Something went wrong:\n\n{e.Exception.Message}\n\nDetails were written to the log.", "Kura",
            MessageBoxButton.OK, MessageBoxImage.Warning);
    }

    internal static void Log(Exception ex)
    {
        try
        {
            var dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Kura", "logs");
            Directory.CreateDirectory(dir);
            File.AppendAllText(Path.Combine(dir, "errors.log"), $"[{DateTimeOffset.Now:O}] {ex}\n\n");
        }
        catch (IOException) { }
    }
}
