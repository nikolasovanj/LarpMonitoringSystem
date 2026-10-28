using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Monitor.Operator.Api;
using Monitor.Operator.ViewModels;
using Monitor.Operator.Views;

namespace Monitor.Operator;

public partial class App : Application
{
    public override void Initialize() => AvaloniaXamlLoader.Load(this);

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            // Base URL: first command-line argument, then MONITOR_API, then the compose API port.
            var args = desktop.Args ?? Array.Empty<string>();
            var baseUrl = args.FirstOrDefault()
                          ?? Environment.GetEnvironmentVariable("MONITOR_API")
                          ?? "http://localhost:8080";

            var api = new MonitorApi(baseUrl);
            var live = new LiveClient(baseUrl);
            var vm = new MainViewModel(api, live, baseUrl);

            var window = new MainWindow { DataContext = vm };
            window.Opened += async (_, _) => await vm.StartAsync();
            window.Closing += async (_, _) => await vm.StopAsync();
            desktop.MainWindow = window;
        }

        base.OnFrameworkInitializationCompleted();
    }
}
