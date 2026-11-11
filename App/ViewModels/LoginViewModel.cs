using System.Windows.Input;
using Juniro.App.Services;

namespace Juniro.App.ViewModels;

/// <summary>
/// Connexion. À l'ouverture, reprend la session mémorisée (pas de mot de passe à retaper) ;
/// sinon e-mail + mot de passe du compte créé dans Supabase (Authentication > Users).
/// </summary>
public sealed class LoginViewModel : BaseViewModel
{
    private readonly SupabaseClient _api;
    private string _email = "";
    private string _password = "";
    private bool _isRestoring = true;

    public LoginViewModel(SupabaseClient api)
    {
        _api = api;
        SignInCommand = new AsyncCommand(SignInAsync);
        try { _email = Preferences.Default.Get("last_email", ""); } catch { }
    }

    public string Email { get => _email; set => SetProperty(ref _email, value); }
    public string Password { get => _password; set => SetProperty(ref _password, value); }

    /// <summary>Vrai pendant la reprise de la session mémorisée (le formulaire est masqué).</summary>
    public bool IsRestoring
    {
        get => _isRestoring;
        set { if (SetProperty(ref _isRestoring, value)) OnPropertyChanged(nameof(ShowForm)); }
    }

    public bool ShowForm => !IsRestoring;

    public ICommand SignInCommand { get; }

    /// <summary>Appelé à l'affichage de la page : reprise automatique de la session.</summary>
    public async Task OnAppearingAsync()
    {
        IsRestoring = true;
        bool ok = false;
        try { ok = await _api.RestoreSessionAsync(); } catch { ok = false; }
        if (ok)
        {
            MainThread.BeginInvokeOnMainThread(App.ShowShell);
            return;
        }
        IsRestoring = false;
    }

    private Task SignInAsync() => RunAsync(async () =>
    {
        if (string.IsNullOrWhiteSpace(Email) || string.IsNullOrWhiteSpace(Password))
        {
            Error = "Saisissez votre e-mail et votre mot de passe.";
            return;
        }
        await _api.SignInAsync(Email.Trim(), Password);
        try { Preferences.Default.Set("last_email", Email.Trim()); } catch { }
        Password = "";
        MainThread.BeginInvokeOnMainThread(App.ShowShell);
    });
}
