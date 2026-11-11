using System.Collections.ObjectModel;
using System.Windows.Input;
using Juniro.App.Models;
using Juniro.App.Services;

namespace Juniro.App.ViewModels;

/// <summary>
/// Onglet « Classements », deux vues :
///   Ligues : classement de la ligue choisie (général, domicile, extérieur, 5 derniers matchs),
///            zones européennes / montée / relégation, forme récente ;
///   Elo    : dernier Elo de chaque équipe, filtres pays et division, recherche, tri
///            (Elo, tendance sur 10 matchs, nom).
/// </summary>
public sealed class StandingsViewModel : BaseViewModel
{
    private const string AllCountries = "__all";

    private readonly DataService _data;
    private readonly Dictionary<string, List<StandingRow>> _cache = new();
    private List<LeagueInfo> _leagues = new();
    private List<EloRow> _elo = new();
    private string _view = "leagues";
    private string _league = "Ligue 1";
    private string _scope = "all";
    private string _country = AllCountries;
    private string _division = "1";
    private string _sort = "elo";
    private string _search = "";
    private string _seasonText = "";
    private bool _isRefreshing;
    private bool _loaded;

    public StandingsViewModel(DataService data)
    {
        _data = data;
        RefreshCommand = new AsyncCommand(async () => { _cache.Clear(); _elo.Clear(); await LoadAsync(); IsRefreshing = false; });

        var pickView = new AsyncCommand(p => PickViewAsync(p as string));
        Views = new() { new("Ligues", "leagues", pickView), new("Elo", "elo", pickView) };
        var pickScope = new AsyncCommand(p => PickScopeAsync(p as string));
        Scopes = new() { new("Général", "all", pickScope), new("Domicile", "home", pickScope), new("Extérieur", "away", pickScope), new("5 derniers", "last5", pickScope) };
        var pickDivision = new AsyncCommand(p => { PickDivision(p as string); return Task.CompletedTask; });
        Divisions = new() { new("Toutes", "all", pickDivision), new("D1", "1", pickDivision), new("D2", "2", pickDivision) };
        var pickSort = new AsyncCommand(p => { PickSort(p as string); return Task.CompletedTask; });
        Sorts = new() { new("Elo", "elo", pickSort), new("Tendance", "trend", pickSort), new("Nom", "name", pickSort) };
        _pickLeague = new AsyncCommand(p => PickLeagueAsync(p as string));
        _pickCountry = new AsyncCommand(p => { PickCountry(p as string); return Task.CompletedTask; });

        foreach (var c in Views) c.IsSelected = c.Value == _view;
        foreach (var c in Scopes) c.IsSelected = c.Value == _scope;
        foreach (var c in Divisions) c.IsSelected = c.Value == _division;
        foreach (var c in Sorts) c.IsSelected = c.Value == _sort;
    }

    private readonly ICommand _pickLeague;
    private readonly ICommand _pickCountry;

    public ICommand RefreshCommand { get; }
    public ObservableCollection<FilterChip> Views { get; }
    public ObservableCollection<FilterChip> Scopes { get; }
    public ObservableCollection<FilterChip> Divisions { get; }
    public ObservableCollection<FilterChip> Sorts { get; }
    public ObservableCollection<FilterChip> LeagueChips { get; } = new();
    public ObservableCollection<FilterChip> CountryChips { get; } = new();
    public ObservableCollection<StandingRow> Rows { get; } = new();
    public ObservableCollection<ZoneLegend> Legend { get; } = new();
    public ObservableCollection<EloRow> EloRows { get; } = new();

    public bool IsLeaguesView => _view == "leagues";
    public bool IsEloView => _view == "elo";
    public bool IsRefreshing { get => _isRefreshing; set => SetProperty(ref _isRefreshing, value); }
    public string SeasonText { get => _seasonText; set => SetProperty(ref _seasonText, value); }
    public bool HasLegend => Legend.Count > 0;
    public string EloCountText => $"{EloRows.Count} équipes";

    /// <summary>Recherche d'une équipe (vue Elo), appliquée à chaque lettre tapée.</summary>
    public string Search
    {
        get => _search;
        set { if (SetProperty(ref _search, value ?? "")) ApplyElo(); }
    }

    public async Task OnAppearingAsync()
    {
        if (_loaded) return;
        _loaded = true;
        await LoadAsync();
    }

    private Task LoadAsync() => RunAsync(async () =>
    {
        if (_leagues.Count == 0)
        {
            _leagues = (await _data.GetLeaguesAsync())
                .OrderBy(l => Leagues.SortKey(l.League)).ThenBy(l => l.League).ToList();
            LeagueChips.Clear();
            foreach (var l in _leagues)
                LeagueChips.Add(new FilterChip(l.League, l.League, _pickLeague) { IsSelected = l.League == _league });
            if (_leagues.Count > 0 && _leagues.All(l => l.League != _league))
            {
                _league = _leagues[0].League;
                foreach (var c in LeagueChips) c.IsSelected = c.Value == _league;
            }
            CountryChips.Clear();
            CountryChips.Add(new FilterChip("Tous", AllCountries, _pickCountry) { IsSelected = true });
            foreach (var country in _leagues.Select(l => l.Country).Where(c => !string.IsNullOrEmpty(c)).Distinct())
                CountryChips.Add(new FilterChip(country!, country!, _pickCountry));
        }
        if (IsLeaguesView) await LoadTableAsync();
        else await LoadEloAsync();
    });

    private async Task LoadTableAsync()
    {
        string key = $"{_league}|{_scope}";
        if (!_cache.TryGetValue(key, out var rows))
        {
            rows = await _data.GetStandingsAsync(_league, _scope);
            _cache[key] = rows;
        }
        var legend = _scope == "all" ? Leagues.ApplyZones(_league, rows) : new List<ZoneLegend>();
        if (_scope != "all") foreach (var r in rows) r.ZoneColor = null;

        Rows.Clear();
        foreach (var r in rows) Rows.Add(r);
        Legend.Clear();
        foreach (var z in legend) Legend.Add(z);
        OnPropertyChanged(nameof(HasLegend));

        var info = _leagues.FirstOrDefault(l => l.League == _league);
        string scopeText = _scope switch { "home" => "à domicile", "away" => "à l'extérieur", "last5" => "sur les 5 derniers matchs", _ => "général" };
        SeasonText = $"{_league} · saison {info?.Season ?? "en cours"} · classement {scopeText}";
    }

    private async Task LoadEloAsync()
    {
        if (_elo.Count == 0) _elo = await _data.GetEloAsync();
        ApplyElo();
    }

    private Task PickViewAsync(string? value)
    {
        if (value == null) return Task.CompletedTask;
        _view = value;
        foreach (var c in Views) c.IsSelected = c.Value == value;
        OnPropertyChanged(nameof(IsLeaguesView));
        OnPropertyChanged(nameof(IsEloView));
        return RunAsync(() => IsLeaguesView ? LoadTableAsync() : LoadEloAsync());
    }

    private Task PickLeagueAsync(string? value)
    {
        if (value == null) return Task.CompletedTask;
        _league = value;
        foreach (var c in LeagueChips) c.IsSelected = c.Value == value;
        return RunAsync(LoadTableAsync);
    }

    private Task PickScopeAsync(string? value)
    {
        if (value == null) return Task.CompletedTask;
        _scope = value;
        foreach (var c in Scopes) c.IsSelected = c.Value == value;
        return RunAsync(LoadTableAsync);
    }

    private void PickCountry(string? value)
    {
        if (value == null) return;
        _country = value;
        foreach (var c in CountryChips) c.IsSelected = c.Value == value;
        ApplyElo();
    }

    private void PickDivision(string? value)
    {
        if (value == null) return;
        _division = value;
        foreach (var c in Divisions) c.IsSelected = c.Value == value;
        ApplyElo();
    }

    private void PickSort(string? value)
    {
        if (value == null) return;
        _sort = value;
        foreach (var c in Sorts) c.IsSelected = c.Value == value;
        ApplyElo();
    }

    private void ApplyElo()
    {
        IEnumerable<EloRow> q = _elo;
        if (_country != AllCountries) q = q.Where(e => e.Country == _country);
        if (_division != "all") q = q.Where(e => e.Tier?.ToString() == _division);
        if (!string.IsNullOrWhiteSpace(_search))
            q = q.Where(e => e.Team.Contains(_search.Trim(), StringComparison.OrdinalIgnoreCase));
        q = _sort switch
        {
            "trend" => q.OrderByDescending(e => e.Delta10 ?? double.MinValue),
            "name" => q.OrderBy(e => e.Team, StringComparer.Create(Fmt.Fr, true)),
            _ => q.OrderByDescending(e => e.Elo)
        };
        var list = q.ToList();
        for (int i = 0; i < list.Count; i++) list[i].Position = i + 1;
        EloRows.Clear();
        foreach (var e in list) EloRows.Add(e);
        OnPropertyChanged(nameof(EloCountText));
    }
}
