using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Microsoft.Extensions.DependencyInjection;
using SetupWizardCore.Services;
using SetupWizardWindows.ViewModels;
using SetupWizardWindows.Views;

namespace SetupWizardWindows;

public partial class App : Application
{
    public override void Initialize()
    {
        AvaloniaXamlLoader.Load(this);
    }

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            // Configure DI
            var services = new ServiceCollection();
            ConfigureServices(services);
            var serviceProvider = services.BuildServiceProvider();

            desktop.MainWindow = new MainWindow
            {
                DataContext = serviceProvider.GetRequiredService<MainViewModel>()
            };
        }

        base.OnFrameworkInitializationCompleted();
    }

    private static void ConfigureServices(IServiceCollection services)
    {
        // Register services
        services.AddSingleton<SetupStateManager>();
        services.AddSingleton<GitHubClient>();
        services.AddSingleton<DependencyChecker>();
        services.AddSingleton<SetupWizardLogger>(_ => SetupWizardLogger.Instance);
        services.AddTransient<ModelDownloader>();
        services.AddTransient<PatcherOrchestrator>();

        // Register DotNetInstaller with its dependency
        services.AddTransient<DotNetInstaller>(sp =>
            new DotNetInstaller(sp.GetRequiredService<SetupStateManager>()));

        // Register ViewModel
        services.AddTransient<MainViewModel>();
    }
}
