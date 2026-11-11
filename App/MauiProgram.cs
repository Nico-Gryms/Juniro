using Juniro.App.Services;
using Juniro.App.ViewModels;
using Juniro.App.Views;
using Microsoft.Extensions.Logging;

namespace Juniro.App;

/// <summary>
/// Démarrage : enregistre les services (accès Supabase), les écrans et leurs ViewModels
/// dans l'injection de dépendances. Shell crée les pages à partir de ce conteneur.
/// </summary>
public static class MauiProgram
{
    public static MauiApp CreateMauiApp()
    {
        var builder = MauiApp.CreateBuilder();
        builder.UseMauiApp<App>();

        // Polices (Resources/Fonts, licence SIL OFL : Resources/Fonts-Inter-OFL.txt)
        builder.ConfigureFonts(fonts =>
        {
            fonts.AddFont("Inter-Regular.otf", "Inter");
            fonts.AddFont("Inter-Medium.otf", "InterMedium");
            fonts.AddFont("Inter-SemiBold.otf", "InterSemiBold");
            fonts.AddFont("Inter-Bold.otf", "InterBold");
            fonts.AddFont("InterDisplay-Bold.otf", "Display");
            fonts.AddFont("InterDisplay-ExtraBold.otf", "DisplayHeavy");
        });

        // Services : un seul client Supabase (il garde la session) et l'accès aux données
        builder.Services.AddSingleton<SessionStore>();
        builder.Services.AddSingleton<SupabaseClient>();
        builder.Services.AddSingleton<DataService>();

        // Écrans
        builder.Services.AddTransient<AppShell>();
        builder.Services.AddTransient<LoginPage>();
        builder.Services.AddTransient<LoginViewModel>();
        builder.Services.AddTransient<UpcomingPage>();
        builder.Services.AddTransient<UpcomingViewModel>();
        builder.Services.AddTransient<MatchDetailPage>();
        builder.Services.AddTransient<MatchDetailViewModel>();
        builder.Services.AddTransient<StandingsPage>();
        builder.Services.AddTransient<StandingsViewModel>();
        builder.Services.AddTransient<LogsPage>();
        builder.Services.AddTransient<LogsViewModel>();
        builder.Services.AddTransient<RunLogsPage>();
        builder.Services.AddTransient<RunLogsViewModel>();
        builder.Services.AddTransient<MorePage>();
        builder.Services.AddTransient<MoreViewModel>();
        builder.Services.AddTransient<ResultsPage>();
        builder.Services.AddTransient<ResultsViewModel>();
        builder.Services.AddTransient<StrategiesPage>();
        builder.Services.AddTransient<StrategiesViewModel>();
        builder.Services.AddTransient<StrategyDetailPage>();
        builder.Services.AddTransient<StrategyDetailViewModel>();
        builder.Services.AddTransient<BetsPage>();
        builder.Services.AddTransient<BetsViewModel>();
        builder.Services.AddTransient<BetEditPage>();
        builder.Services.AddTransient<BetEditViewModel>();
        builder.Services.AddTransient<SettingsPage>();
        builder.Services.AddTransient<SettingsViewModel>();

#if DEBUG
        builder.Logging.AddDebug();
#endif
        return builder.Build();
    }
}
