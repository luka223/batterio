using Batterio.Core;
using Batterio.Core.Abstractions;
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
        builder.Services.AddSingleton<IBatteryAdapterFactory, LogitechGProX2SuperstrikeAdapterFactory>();
        builder.Services.AddSingleton<IBatteryAdapterFactory, KeychronQ1HeAdapterFactory>();
        builder.Services.AddSingleton<IBatteryAdapterFactory, LogitechGProX2LightspeedAdapterFactory>();
        builder.Services.AddSingleton<AdapterRegistry>();
        builder.Services.AddSingleton<PollingCoordinator>();
        builder.Services.AddSingleton<WindowCoordinator>();
        builder.Services.AddSingleton<IWindowCoordinator>(services => services.GetRequiredService<WindowCoordinator>());
        builder.Services.AddSingleton<ITrayService, TrayService>();
        builder.Services.AddSingleton<BatteryApplication>();

#if DEBUG
        builder.Logging.AddDebug();
#endif

        return builder.Build();
    }
}
