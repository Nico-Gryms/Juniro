using Microsoft.UI.Xaml;

namespace Juniro.App.WinUI;

/// <summary>Point d'entrée Windows (généré par le modèle .NET MAUI, rien à modifier).</summary>
public partial class App : MauiWinUIApplication
{
    public App()
    {
        this.InitializeComponent();
    }

    protected override MauiApp CreateMauiApp() => MauiProgram.CreateMauiApp();
}
