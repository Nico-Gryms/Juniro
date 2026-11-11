using Juniro.App.Views;

namespace Juniro.App;

/// <summary>
/// Application : ouvre la fenêtre sur la page de connexion. Celle-ci reprend la session
/// enregistrée s'il y en a une, puis bascule sur les onglets (AppShell).
/// ShowShell / ShowLogin changent la page racine de la fenêtre (connexion, déconnexion).
/// </summary>
public partial class App : Application
{
    private readonly IServiceProvider _services;

    public App(IServiceProvider services)
    {
        InitializeComponent();
        _services = services;
        UserAppTheme = AppTheme.Dark;
    }

    protected override Window CreateWindow(IActivationState? activationState)
    {
        var window = new Window(_services.GetRequiredService<LoginPage>()) { Title = "Juniro" };
        return window;
    }

    /// <summary>Affiche les onglets de l'application (après connexion).</summary>
    public static void ShowShell()
    {
        var app = (App)Current!;
        app.Windows[0].Page = app._services.GetRequiredService<AppShell>();
    }

    /// <summary>Revient à la page de connexion (déconnexion, session expirée).</summary>
    public static void ShowLogin()
    {
        var app = (App)Current!;
        app.Windows[0].Page = app._services.GetRequiredService<LoginPage>();
    }
}
