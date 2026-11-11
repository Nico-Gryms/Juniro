using System.Windows.Input;
using Juniro.App.Models;
using Juniro.App.Services;
using Juniro.App.Views;

namespace Juniro.App.ViewModels;

/// <summary>Onglet « Plus » : accès aux résultats du modèle et aux réglages, état des données.</summary>
public sealed class MoreViewModel : BaseViewModel
{
    private readonly DataService _data;
    private string _syncText = "";
    private ServiceHealth _health = new();

    public MoreViewModel(DataService data)
    {
        _data = data;
        OpenResultsCommand = new AsyncCommand(() => GoToAsync(nameof(ResultsPage)));
        OpenSettingsCommand = new AsyncCommand(() => GoToAsync(nameof(SettingsPage)));
        OpenLogsCommand = new AsyncCommand(() => GoToAsync(nameof(LogsPage)));
    }

    public ICommand OpenLogsCommand { get; }
    public ICommand OpenResultsCommand { get; }
    public ICommand OpenSettingsCommand { get; }
    public string SyncText { get => _syncText; set => SetProperty(ref _syncText, value); }
    public ServiceHealth Health { get => _health; set => SetProperty(ref _health, value); }
    public string Email => _data.CurrentEmail ?? "";
    public string Version => $"Juniro {AppInfo.Current.VersionString}";

    public Task OnAppearingAsync() => RunAsync(async () =>
    {
        OnPropertyChanged(nameof(Email));
        SyncText = await _data.GetSyncTextAsync();
        Health = await _data.GetHealthAsync();
    }, showBusy: false);
}
