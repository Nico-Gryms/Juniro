using Juniro.App.Views;

namespace Juniro.App;

/// <summary>Barre d'onglets et routes des pages de détail.</summary>
public partial class AppShell : Shell
{
    public AppShell()
    {
        InitializeComponent();
        Routing.RegisterRoute(nameof(MatchDetailPage), typeof(MatchDetailPage));
        Routing.RegisterRoute(nameof(BetEditPage), typeof(BetEditPage));
        Routing.RegisterRoute(nameof(StrategyDetailPage), typeof(StrategyDetailPage));
        Routing.RegisterRoute(nameof(ResultsPage), typeof(ResultsPage));
        Routing.RegisterRoute(nameof(SettingsPage), typeof(SettingsPage));
        Routing.RegisterRoute(nameof(LogsPage), typeof(LogsPage));
        Routing.RegisterRoute(nameof(RunLogsPage), typeof(RunLogsPage));
    }
}
