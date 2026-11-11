using System.Windows.Input;
using Juniro.App.Services;

namespace Juniro.App.ViewModels;

/// <summary>Onglet « Réglages » : compte connecté, nom affiché, dernière mise à jour des données, déconnexion.</summary>
public sealed class SettingsViewModel : BaseViewModel
{
    private readonly DataService _data;
    private readonly SupabaseClient _api;
    private string _displayName = "";
    private string _syncText = "";
    private string _partnerText = "";
    private string _message = "";

    public SettingsViewModel(DataService data, SupabaseClient api)
    {
        _data = data;
        _api = api;
        SaveNameCommand = new AsyncCommand(SaveNameAsync);
        SignOutCommand = new AsyncCommand(SignOutAsync);
    }

    public ICommand SaveNameCommand { get; }
    public ICommand SignOutCommand { get; }

    public string Email => _api.Email ?? "";
    public string DisplayName { get => _displayName; set => SetProperty(ref _displayName, value); }
    public string SyncText { get => _syncText; set => SetProperty(ref _syncText, value); }
    public string PartnerText { get => _partnerText; set => SetProperty(ref _partnerText, value); }
    public string Message { get => _message; set => SetProperty(ref _message, value); }
    public string Version => $"Juniro {AppInfo.Current.VersionString} · {AppConfig.SupabaseUrl.Replace("https://", "")}";

    public Task OnAppearingAsync() => RunAsync(async () =>
    {
        OnPropertyChanged(nameof(Email));
        var profiles = await _data.GetProfilesAsync();
        var me = profiles.FirstOrDefault(p => p.Id == _api.UserId);
        DisplayName = me?.DisplayName ?? "";
        var others = profiles.Where(p => p.Id != _api.UserId).Select(p => p.DisplayName).ToList();
        PartnerText = others.Count == 0 ? "Aucun autre compte pour l'instant." : $"Partenaire : {string.Join(", ", others)}";
        SyncText = await _data.GetSyncTextAsync();
    });

    private Task SaveNameAsync() => RunAsync(async () =>
    {
        if (string.IsNullOrWhiteSpace(DisplayName)) { Error = "Le nom ne peut pas être vide."; return; }
        await _data.UpdateDisplayNameAsync(DisplayName.Trim());
        Message = "Nom enregistré.";
    });

    private async Task SignOutAsync()
    {
        await _api.SignOutAsync();
        App.ShowLogin();
    }
}
