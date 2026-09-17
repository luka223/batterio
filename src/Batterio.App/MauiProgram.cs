using Batterio.Core;
using Batterio.Core.Abstractions;
using Batterio.Core.Notifications;
using Batterio.App.Windows;
using Microsoft.Extensions.Logging;

namespace Batterio.App;

public static class MauiProgram
{
    public static MauiApp CreateMauiApp()
    {
        var builder = MauiApp.CreateBuilder();
        builder
            .UseMauiApp<App>()
            .ConfigureFonts(fonts => { });

        builder.Services.AddSingleton<IBatterioLog>(_ =>
            new RotatingFileLog(Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Batterio", "Logs")));
        builder.Services.AddSingleton<IProcessTerminator, EnvironmentProcessTerminator>();
        builder.Services.AddSingleton(StartupReadiness.Current ?? StartupReadiness.CaptureFromEnvironment());
        builder.Services.AddSingleton<IHidCollectionProvider, WindowsHidCollectionProvider>();
        builder.Services.AddSingleton<IBatteryAdapterFactory, LogitechGProX2SuperstrikeAdapterFactory>();
        builder.Services.AddSingleton<IBatteryAdapterFactory, LogitechGProX2LightspeedAdapterFactory>();
        builder.Services.AddSingleton<AdapterRegistry>();
        builder.Services.AddSingleton<IUserSettingsStore>(services => new JsonUserSettingsStore(
            new ConfigurationPathProvider().SettingsFilePath,
            services.GetRequiredService<IBatterioLog>()));
        builder.Services.AddSingleton<IDeviceDiscoveryService>(services => new DeviceDiscoveryService(
            BuiltInDevices.Catalog,
            services.GetRequiredService<AdapterRegistry>(),
            services.GetRequiredService<IHidCollectionProvider>(),
            services.GetRequiredService<IBatterioLog>()));
        builder.Services.AddSingleton<PollingCoordinator>();
        builder.Services.AddSingleton(WindowsToastActivation.Current);
        builder.Services.AddSingleton<IWindowsToastPlatform, WindowsToastPlatform>();
        builder.Services.AddSingleton<WindowsToastRegistrationService>();
        builder.Services.AddSingleton<IBatteryNotificationService, WindowsBatteryNotificationService>();
        builder.Services.AddSingleton<BatteryNotificationCoordinator>();
        builder.Services.AddSingleton<DeviceSelectionCoordinator>(services => new DeviceSelectionCoordinator(
            BuiltInDevices.Catalog,
            services.GetRequiredService<IUserSettingsStore>(),
            services.GetRequiredService<IDeviceDiscoveryService>(),
            services.GetRequiredService<PollingCoordinator>(),
            services.GetRequiredService<IBatterioLog>()));
        builder.Services.AddSingleton<IStartupRegistry, WindowsStartupRegistry>();
        builder.Services.AddSingleton<IStartupRegistrationService, WindowsStartupRegistrationService>();
        builder.Services.AddTransient<SettingsViewModel>();
        builder.Services.AddSingleton<WindowCoordinator>();
        builder.Services.AddSingleton<IWindowCoordinator>(services => services.GetRequiredService<WindowCoordinator>());
        builder.Services.AddSingleton<ITrayService, TrayService>();
        builder.Services.AddSingleton<BatteryApplication>(services =>
        {
            var application = ActivatorUtilities.CreateInstance<BatteryApplication>(services);
            application.ConfigureProcessTerminator(services.GetRequiredService<IProcessTerminator>());
            return application;
        });

#if DEBUG
        builder.Logging.AddDebug();
#endif

        return builder.Build();
    }
}
