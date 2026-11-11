using System.Collections.ObjectModel;
using System.Windows.Input;
using Juniro.App.Models;
using Juniro.App.Services;
using Juniro.App.Views;

namespace Juniro.App.ViewModels;

/// <summary>
/// Onglet « Matchs » : les matchs d'une journée choisie dans le bandeau de dates (3 jours
/// avant, 4 jours après aujourd'hui), répartis en Terminés / Joués (score à la prochaine
/// copie) / À venir. Filtres : Tous / Joués / À venir, ligue, « paris proposés ».
/// Chaque carte montre les cotes 1 N 2, la prédiction, la meilleure valeur et le meilleur indice.
/// </summary>
public sealed class UpcomingViewModel : BaseViewModel
{
    private const string AllLeagues = "__all";
    private const string SignalsOnly = "__signals";

    private readonly DataService _data;
    private readonly Dictionary<DateTime, List<MatchItem>> _cache = new();
    private Dictionary<string, LeagueStats> _leagueStats = new();
    private List<MatchItem> _day = new();
    private DateTime _date = DateTime.Today;
    private string _status = "all";
    private string _league = AllLeagues;
    private bool _onlySignals;
    private bool _isRefreshing;
    private string _syncText = "";
    private string _dayTitle = "";
    private string _emptyText = "";
    private MatchItem? _selectedMatch;
    private ServiceHealth _health = new();
    private bool _loaded;

    public UpcomingViewModel(DataService data)
    {
        _data = data;
        RefreshCommand = new AsyncCommand(async () => { _cache.Clear(); await LoadDayAsync(); IsRefreshing = false; });
        var pickDay = new AsyncCommand(p => p is DateTime d ? SelectDayAsync(d) : Task.CompletedTask);
        for (int i = -3; i <= 4; i++) Days.Add(new DayChip(DateTime.Today.AddDays(i), pickDay));
        foreach (var d in Days) d.IsSelected = d.Date == _date;

        var pickStatus = new AsyncCommand(p => { PickStatus(p as string); return Task.CompletedTask; });
        Statuses = new() { new("Tous", "all", pickStatus), new("Joués", "played", pickStatus), new("À venir", "upcoming", pickStatus) };
        foreach (var c in Statuses) c.IsSelected = c.Value == _status;
        _pickLeague = new AsyncCommand(p => { PickLeague(p as string); return Task.CompletedTask; });
        OpenLogsCommand = new AsyncCommand(() => GoToAsync(nameof(LogsPage)));
    }

    /// <summary>Ouvre le journal du service (toucher l'indicateur de l'en-tête).</summary>
    public ICommand OpenLogsCommand { get; }

    /// <summary>État du dernier scrap et de la dernière copie, affiché dans l'en-tête.</summary>
    public ServiceHealth Health { get => _health; set => SetProperty(ref _health, value); }

    private readonly ICommand _pickLeague;

    public ObservableCollection<DayChip> Days { get; } = new();
    public ObservableCollection<FilterChip> Statuses { get; }
    public ObservableCollection<FilterChip> LeagueChips { get; } = new();
    public ObservableCollection<MatchSection> Sections { get; } = new();
    public ICommand RefreshCommand { get; }

    public bool IsRefreshing { get => _isRefreshing; set => SetProperty(ref _isRefreshing, value); }
    public string SyncText { get => _syncText; set => SetProperty(ref _syncText, value); }
    public string DayTitle { get => _dayTitle; set => SetProperty(ref _dayTitle, value); }
    public string EmptyText { get => _emptyText; set => SetProperty(ref _emptyText, value); }

    /// <summary>Toucher un match ouvre sa fiche (puis la sélection est effacée).</summary>
    public MatchItem? SelectedMatch
    {
        get => _selectedMatch;
        set
        {
            if (!SetProperty(ref _selectedMatch, value) || value == null) return;
            var id = value.MatchId;
            SelectedMatch = null;
            _ = GoToAsync(nameof(MatchDetailPage), new Dictionary<string, object> { ["matchId"] = id });
        }
    }

    public async Task OnAppearingAsync()
    {
        if (_loaded) return;
        _loaded = true;
        await LoadDayAsync();
    }

    private async Task SelectDayAsync(DateTime date)
    {
        _date = date.Date;
        foreach (var d in Days) d.IsSelected = d.Date == _date;
        await LoadDayAsync();
    }

    private Task LoadDayAsync() => RunAsync(async () =>
    {
        DayTitle = Fmt.DayLong(_date);
        if (_leagueStats.Count == 0)
            _leagueStats = (await _data.GetLeagueStatsAsync()).ToDictionary(l => l.League);

        if (!_cache.TryGetValue(_date, out var day))
        {
            day = await _data.GetMatchesForDayAsync(_date);
            // Forme des équipes : nécessaire pour l'indice de chaque match
            var stats = (await _data.GetMatchStatsForAsync(day.Where(m => m.HasPrediction).Select(m => m.MatchId)))
                .ToDictionary(s => s.MatchId);
            foreach (var m in day.Where(m => m.HasPrediction))
                m.BestIndex = IndexEngine.Best(m, stats.GetValueOrDefault(m.MatchId), _leagueStats.GetValueOrDefault(m.League));
            _cache[_date] = day;
        }
        _day = day;
        RebuildLeagueChips();
        Apply();
        SyncText = await _data.GetSyncTextAsync();
        try { Health = await _data.GetHealthAsync(); }
        catch (InvalidOperationException) { /* l'indicateur reste « inconnu » */ }
    });

    private void RebuildLeagueChips()
    {
        var leagues = _day.Select(m => m.League).Distinct().OrderBy(Leagues.SortKey).ThenBy(l => l).ToList();
        if (_league != AllLeagues && !leagues.Contains(_league)) _league = AllLeagues;
        LeagueChips.Clear();
        LeagueChips.Add(new FilterChip("Toutes", AllLeagues, _pickLeague) { IsSelected = _league == AllLeagues });
        LeagueChips.Add(new FilterChip("★ Paris proposés", SignalsOnly, _pickLeague) { IsSelected = _onlySignals });
        foreach (var l in leagues)
            LeagueChips.Add(new FilterChip(l, l, _pickLeague) { IsSelected = _league == l });
    }

    private void PickStatus(string? value)
    {
        if (value == null) return;
        _status = value;
        foreach (var c in Statuses) c.IsSelected = c.Value == value;
        Apply();
    }

    private void PickLeague(string? value)
    {
        if (value == null) return;
        if (value == SignalsOnly) _onlySignals = !_onlySignals;
        else _league = value;
        foreach (var c in LeagueChips)
            c.IsSelected = c.Value == SignalsOnly ? _onlySignals : c.Value == _league;
        Apply();
    }

    private void Apply()
    {
        IEnumerable<MatchItem> items = _day;
        if (_league != AllLeagues) items = items.Where(m => m.League == _league);
        if (_onlySignals) items = items.Where(m => m.HasSignals);
        var list = items.ToList();

        var finished = list.Where(m => m.IsFinished).ToList();
        var played = list.Where(m => m.IsPlayed).ToList();
        var upcoming = list.Where(m => m.IsUpcoming).ToList();
        var postponed = list.Where(m => m.State == "POSTPONED").ToList();

        Statuses[0].Label = $"Tous {list.Count}";
        Statuses[1].Label = $"Joués {finished.Count + played.Count}";
        Statuses[2].Label = $"À venir {upcoming.Count}";

        Sections.Clear();
        if (_status is "all" or "played")
        {
            if (finished.Count > 0) Sections.Add(new MatchSection("Terminés", Palette.TextMuted, finished));
            if (played.Count > 0) Sections.Add(new MatchSection("Joués · score avec la copie de demain", Palette.Even, played));
        }
        if ((_status is "all" or "upcoming") && upcoming.Count > 0)
            Sections.Add(new MatchSection("À venir", Palette.Accent, upcoming));
        if (_status == "all" && postponed.Count > 0)
            Sections.Add(new MatchSection("Reportés", Palette.TextDim, postponed));

        EmptyText = _day.Count == 0
            ? $"Aucun match le {Fmt.DayLong(_date).ToLowerInvariant()}."
            : "Aucun match pour ces filtres.";
    }
}
