using Android.App;
using Android.Runtime;

namespace Juniro.App;

/// <summary>Application Android : démarre MAUI (généré par le modèle .NET MAUI).</summary>
[Application]
public class MainApplication : MauiApplication
{
    public MainApplication(IntPtr handle, JniHandleOwnership ownership)
        : base(handle, ownership)
    {
    }

    protected override MauiApp CreateMauiApp() => MauiProgram.CreateMauiApp();
}
