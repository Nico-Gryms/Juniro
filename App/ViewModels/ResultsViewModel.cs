using System.Collections.ObjectModel;
using System.Windows.Input;
using Juniro.App.Models;
using Juniro.App.Services;
using Juniro.App.Views;

namespace Juniro.App.ViewModels;

/// <summary>
/// Onglet « Résultats » : précision des prédictions (1X2, BTTS, ±2,5, score exact, perte
/// logarithmique face au marché) sur la période choisie, par ligue, et derniers matchs
/// prédits avec le résultat réel.
/// </summary>
public sealed class ResultsViewModel : BaseViewModel
{
    public const string AllLeagues = "Toutes ligues";   // même libellé que la fonction prediction_kpis

    private readonly DataService _data;
    private string _period = "30";
    private string _league = AllLeagues;
    private PredictionKpi? _kpi;
    private bool _isRefreshing;
    private PredictionResult? _selected;
    private List<PredictionKpi> _kpis = new();

    public ResultsViewModel(DataService data)
    {
        _data = data;
        RefreshCommand = new AsyncCommand(async () => { await LoadAsync(); IsRefreshing = false; });
        var pick = new AsyncCommand(async p =>
        {
            if (p is not string v) return;
            _period = v;
            foreach (var c in Periods) c.IsSelected = c.Value == v;
            await LoadAsync();
        });
        Periods = new()
        {
            new("7 jours", "7", pick), new("30 jours", "30", pick), new("Saison", "season", pick), new("Tout", "all", pick)
        };
        foreach (var c in Periods) c.IsSelected = c.Value == _period;
    }

    public ObservableCollection<FilterChip> Periods { get; }
    public ObservableCollection<string> Leagues { get; } = new() { AllLeagues };
    public ObservableCollection<PredictionKpi> LeagueKpis { get; } = new();
    public ObservableCollection<PredictionResult> Results { get; } = new();
    public ICommand RefreshCommand { get; }

    public PredictionKpi? Kpi { get => _kpi; set { if (SetProperty(ref _kpi, value)) OnPropertyChanged(nameof(HasKpi)); } }
    public bool HasKpi => Kpi != null && Kpi.Matches > 0;
    public bool IsRefreshing { get => _isRefreshing; set => SetProperty(ref _isRefreshing, value); }

    public string SelectedLeague
    {
        get => _league;
        set { if (SetProperty(ref _league, value ?? AllLeagues)) _ = LoadResultsAsync(); }
    }

    public PredictionResult? SelectedResult
    {
        get => _selected;
        set
        {
            if (!SetProperty(ref _selected, value) || value == null) return;
            var id = value.MatchId;
            SelectedResult = null;
            _ = GoToAsync(nameof(MatchDetailPage), new Dictionary<string, object> { ["matchId"] = id });
        }
    }

    private bool _loaded;

    public Task OnAppearingAsync() => _loaded ? Task.CompletedTask : LoadAsync();

    private DateTimeOffset? From()
    {
        var now = DateTimeOffset.Now;
        return _period switch
        {
            "7" => now.AddDays(-7),
            "30" => now.AddDays(-30),
            "season" => new DateTimeOffset(new DateTime(now.Month >= 7 ? now.Year : now.Year - 1, 7, 1)),
            _ => null
        };
    }

    private Task LoadAsync() => RunAsync(async () =>
    {
        _kpis = await _data.GetKpisAsync(From(), null);
        _loaded = true;
        var keep = _league;
        Leagues.Clear();
        foreach (var k in _kpis) Leagues.Add(k.League);
        if (Leagues.Count == 0) Leagues.Add(AllLeagues);
        _league = Leagues.Contains(keep) ? keep : Leagues[0];
        OnPropertyChanged(nameof(SelectedLeague));

        LeagueKpis.Clear();
        foreach (var kpi in _kpis.Where(k => k.League != AllLeagues)) LeagueKpis.Add(kpi);
        await LoadResultsCoreAsync();
    });

    private Task LoadResultsAsync() => RunAsync(LoadResultsCoreAsync);

    private async Task LoadResultsCoreAsync()
    {
        Kpi = _kpis.FirstOrDefault(k => k.League == _league);
        var rows = await _data.GetResultsAsync(From(), _league == AllLeagues ? null : _league, 150);
        Results.Clear();
        foreach (var r in rows) Results.Add(r);
    }
}
