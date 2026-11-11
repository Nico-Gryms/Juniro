using System.Collections.ObjectModel;
using System.Windows.Input;
using Juniro.App.Models;
using Juniro.App.Services;
using Juniro.App.Views;

namespace Juniro.App.ViewModels;

/// <summary>
/// Onglet « Stratégies » :
///   - paris fictifs en cours (stratégies actives, suivies match après match) ;
///   - stratégies simulées du dernier backtest, ligue par ligue, avec un verdict sur la période
///     de test : Gagnante (ROI ≥ +3 %), Équilibrée, Perdante (ROI ≤ −3 %).
/// </summary>
public sealed class StrategiesViewModel : BaseViewModel
{
    public const string AllLeagues = "Toutes les ligues";

    private readonly DataService _data;
    private List<StrategyOverview> _all = new();
    private string _verdict = "all";
    private string _league = AllLeagues;
    private bool _isRefreshing;
    private StrategyOverview? _selected;
    private int _nWin, _nEven, _nLose;
    private bool _loaded;

    public StrategiesViewModel(DataService data)
    {
        _data = data;
        RefreshCommand = new AsyncCommand(async () => { await LoadAsync(); IsRefreshing = false; });
        OpenPaperCommand = new AsyncCommand(p => p is PaperSummary s
            ? GoToAsync(nameof(StrategyDetailPage), new Dictionary<string, object> { ["paper"] = s.Strategy })
            : Task.CompletedTask);
        var pick = new AsyncCommand(p =>
        {
            if (p is string v) { _verdict = v; foreach (var c in Verdicts) c.IsSelected = c.Value == v; Apply(); }
            return Task.CompletedTask;
        });
        Verdicts = new()
        {
            new("Toutes", "all", pick), new("Gagnantes", "Gagnante", pick),
            new("Équilibrées", "Équilibrée", pick), new("Perdantes", "Perdante", pick)
        };
        foreach (var c in Verdicts) c.IsSelected = c.Value == _verdict;
    }

    public ObservableCollection<FilterChip> Verdicts { get; }
    public ObservableCollection<string> Leagues { get; } = new() { AllLeagues };
    public ObservableCollection<StrategyOverview> Items { get; } = new();
    public ObservableCollection<PaperSummary> Paper { get; } = new();
    public ICommand RefreshCommand { get; }

    /// <summary>Ouvre le suivi d'une stratégie en paris fictifs (paramètre : PaperSummary).</summary>
    public ICommand OpenPaperCommand { get; }

    public bool IsRefreshing { get => _isRefreshing; set => SetProperty(ref _isRefreshing, value); }
    public int WinCount { get => _nWin; set => SetProperty(ref _nWin, value); }
    public int EvenCount { get => _nEven; set => SetProperty(ref _nEven, value); }
    public int LoseCount { get => _nLose; set => SetProperty(ref _nLose, value); }
    public bool HasPaper => Paper.Count > 0;
    public bool HasNoPaper => _loaded && Paper.Count == 0;

    public string SelectedLeague { get => _league; set { if (SetProperty(ref _league, value ?? AllLeagues)) Apply(); } }

    public StrategyOverview? SelectedItem
    {
        get => _selected;
        set
        {
            if (!SetProperty(ref _selected, value) || value == null) return;
            var item = value;
            SelectedItem = null;
            _ = GoToAsync(nameof(StrategyDetailPage), new Dictionary<string, object>
            {
                ["family"] = item.Family, ["league"] = item.League
            });
        }
    }

    public Task OnAppearingAsync() => _loaded ? Task.CompletedTask : LoadAsync();

    private Task LoadAsync() => RunAsync(async () =>
    {
        var paperTask = _data.GetPaperSummaryAsync();
        var overviewTask = _data.GetStrategyOverviewAsync();
        await Task.WhenAll(paperTask, overviewTask);
        _loaded = true;

        Paper.Clear();
        foreach (var p in paperTask.Result) Paper.Add(p);
        OnPropertyChanged(nameof(HasPaper));
        OnPropertyChanged(nameof(HasNoPaper));

        _all = overviewTask.Result;
        var keep = _league;
        Leagues.Clear();
        Leagues.Add(AllLeagues);
        foreach (var l in _all.Select(x => x.League).Distinct().OrderBy(x => x)) Leagues.Add(l);
        _league = Leagues.Contains(keep) ? keep : AllLeagues;
        OnPropertyChanged(nameof(SelectedLeague));
        Apply();
    });

    private void Apply()
    {
        IEnumerable<StrategyOverview> q = _all;
        if (_league != AllLeagues) q = q.Where(x => x.League == _league);
        var inLeague = q.ToList();
        WinCount = inLeague.Count(x => x.Verdict == "Gagnante");
        EvenCount = inLeague.Count(x => x.Verdict == "Équilibrée");
        LoseCount = inLeague.Count(x => x.Verdict == "Perdante");
        if (_verdict != "all") q = q.Where(x => x.Verdict == _verdict);
        Items.Clear();
        foreach (var item in q.OrderByDescending(x => x.RoiTest ?? double.MinValue)) Items.Add(item);
    }
}
