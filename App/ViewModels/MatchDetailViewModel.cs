using System.Collections.ObjectModel;
using System.Windows.Input;
using Juniro.App.Models;
using Juniro.App.Services;
using Juniro.App.Views;

namespace Juniro.App.ViewModels;

/// <summary>
/// Fiche d'un match, en quatre onglets :
///   Cotes   : les 16 paris par marché (probabilité du modèle, cote juste, cote, valeur),
///             les paris fictifs des stratégies et nos paris sur ce match ;
///   Indices : critères favorables / défavorables de chaque pari et son score ;
///   Stats   : statistiques du match (s'il est terminé) et comparaison avant le match ;
///   Forme   : 5 derniers matchs de chaque équipe.
/// Bouton « J'ai parié ». Paramètre de navigation : matchId.
/// </summary>
public sealed class MatchDetailViewModel : BaseViewModel, IQueryAttributable
{
    private readonly DataService _data;
    private long _matchId;
    private MatchItem? _match;
    private string _tab = "odds";
    private string _title = "Match";
    private string _leagueLine = "", _kickoffLine = "";
    private string _centerTop = "", _centerMain = "", _centerSub = "";
    private string _oddsLabel = "";
    private BetIndex? _focus;
    private bool _hasPrediction;

    public MatchDetailViewModel(DataService data)
    {
        _data = data;
        AddBetCommand = new AsyncCommand(AddBetAsync);
        RefreshCommand = new AsyncCommand(LoadAsync);
        OpenBetCommand = new AsyncCommand(p => p is UserBetRow b
            ? GoToAsync(nameof(BetEditPage), new Dictionary<string, object> { ["betId"] = b.Id })
            : Task.CompletedTask);
        FocusIndexCommand = new AsyncCommand(p => { if (p is BetIndex x) Focus = x; return Task.CompletedTask; });
        var pick = new AsyncCommand(p => { PickTab(p as string); return Task.CompletedTask; });
        Tabs = new() { new("Cotes", "odds", pick), new("Indices", "index", pick), new("Stats", "stats", pick), new("Forme", "form", pick) };
        foreach (var t in Tabs) t.IsSelected = t.Value == _tab;
    }

    public ICommand AddBetCommand { get; }
    public ICommand RefreshCommand { get; }
    /// <summary>Ouvre un de nos paris sur ce match (paramètre : UserBetRow).</summary>
    public ICommand OpenBetCommand { get; }
    /// <summary>Affiche en détail l'indice d'un pari (paramètre : BetIndex).</summary>
    public ICommand FocusIndexCommand { get; }

    public ObservableCollection<FilterChip> Tabs { get; }
    public ObservableCollection<MarketGroup> Markets { get; } = new();
    public ObservableCollection<BetIndex> Indices { get; } = new();
    public ObservableCollection<CompareRow> MatchFacts { get; } = new();
    public ObservableCollection<CompareRow> PreStats { get; } = new();
    public ObservableCollection<FormLine> HomeForm { get; } = new();
    public ObservableCollection<FormLine> AwayForm { get; } = new();
    public ObservableCollection<SignalRow> Signals { get; } = new();
    public ObservableCollection<UserBetRow> Bets { get; } = new();

    // ------------------------------------------------------------------ onglets
    public bool IsOddsTab => _tab == "odds";
    public bool IsIndexTab => _tab == "index";
    public bool IsStatsTab => _tab == "stats";
    public bool IsFormTab => _tab == "form";

    // ------------------------------------------------------------------ en-tête
    public string Title { get => _title; set => SetProperty(ref _title, value); }
    public string LeagueLine { get => _leagueLine; set => SetProperty(ref _leagueLine, value); }
    public string KickoffLine { get => _kickoffLine; set => SetProperty(ref _kickoffLine, value); }
    public string CenterTop { get => _centerTop; set => SetProperty(ref _centerTop, value); }
    public string CenterMain { get => _centerMain; set => SetProperty(ref _centerMain, value); }
    public string CenterSub { get => _centerSub; set => SetProperty(ref _centerSub, value); }
    public string HomeTeam => _match?.HomeTeam ?? "";
    public string AwayTeam => _match?.AwayTeam ?? "";
    public string HomeShort => _match?.HomeShort ?? "";
    public string AwayShort => _match?.AwayShort ?? "";
    public string HomeInfo => _match?.HomeInfo ?? "";
    public string AwayInfo => _match?.AwayInfo ?? "";
    public double PHome => _match?.PHome ?? 0;
    public double PDraw => _match?.PDraw ?? 0;
    public double PAway => _match?.PAway ?? 0;
    public string PHomeLabel => _match == null ? "" : $"{_match.HomeShort} {Fmt.Pct(_match.PHome)}";
    public string PDrawLabel => _match == null ? "" : $"Nul {Fmt.Pct(_match.PDraw)}";
    public string PAwayLabel => _match == null ? "" : $"{_match.AwayShort} {Fmt.Pct(_match.PAway)}";
    public bool HasPrediction { get => _hasPrediction; set => SetProperty(ref _hasPrediction, value); }
    public string OddsLabel { get => _oddsLabel; set => SetProperty(ref _oddsLabel, value); }

    // ------------------------------------------------------------------ indices
    /// <summary>Le pari affiché en détail (le meilleur au départ, ou celui touché dans la liste).</summary>
    public BetIndex? Focus
    {
        get => _focus;
        set
        {
            if (!SetProperty(ref _focus, value)) return;
            OnPropertyChanged(nameof(HasFocus));
            OnPropertyChanged(nameof(FocusTitle));
            OnPropertyChanged(nameof(FocusCaption));
        }
    }
    public bool HasFocus => Focus != null;
    public string FocusTitle => Focus?.Label ?? "";
    public string FocusCaption => Focus == null ? "" : Focus == Indices.FirstOrDefault() ? "Meilleur indice" : "Pari choisi";
    public bool HasNoIndex => !HasFocus;

    // ------------------------------------------------------------------ stats et forme
    public bool HasMatchFacts => MatchFacts.Count > 0;
    public bool HasPreStats => PreStats.Count > 0;
    public bool HasNoStats => !HasMatchFacts && !HasPreStats;
    public bool HasHomeForm => HomeForm.Count > 0;
    public bool HasAwayForm => AwayForm.Count > 0;
    public bool HasNoHomeForm => HomeForm.Count == 0;
    public bool HasNoAwayForm => AwayForm.Count == 0;
    public bool HasSignals => Signals.Count > 0;
    public bool HasBets => Bets.Count > 0;

    public void ApplyQueryAttributes(IDictionary<string, object> query)
    {
        if (query.TryGetValue("matchId", out var v) && v is long id)
            _matchId = id;
    }

    public Task OnAppearingAsync() => LoadAsync();

    private void PickTab(string? value)
    {
        if (value == null) return;
        _tab = value;
        foreach (var t in Tabs) t.IsSelected = t.Value == value;
        OnPropertyChanged(nameof(IsOddsTab));
        OnPropertyChanged(nameof(IsIndexTab));
        OnPropertyChanged(nameof(IsStatsTab));
        OnPropertyChanged(nameof(IsFormTab));
    }

    private Task LoadAsync() => RunAsync(async () =>
    {
        if (_matchId == 0) return;
        var itemTask = _data.GetMatchItemAsync(_matchId);
        var rowTask = _data.GetMatchAsync(_matchId);
        var statsTask = _data.GetMatchStatsAsync(_matchId);
        var sigTask = _data.GetSignalsForMatchAsync(_matchId);
        var betsTask = _data.GetBetsForMatchAsync(_matchId);
        var leagueTask = _data.GetLeagueStatsAsync();
        await Task.WhenAll(itemTask, rowTask, statsTask, sigTask, betsTask, leagueTask);

        var m = _match = itemTask.Result;
        if (m == null)
        {
            Error = "Match introuvable dans Supabase.";
            return;
        }
        var st = statsTask.Result;
        var league = leagueTask.Result.FirstOrDefault(l => l.League == m.League);

        // En-tête
        Title = $"{m.HomeTeam} – {m.AwayTeam}";
        LeagueLine = m.RoundNumber is int r ? $"{m.League} · J{r}" : m.League;
        KickoffLine = $"{Fmt.DayLong(m.KickoffLocal)} · {m.TimeText}";
        HasPrediction = m.HasPrediction;
        if (m.IsFinished)
        {
            CenterTop = "Score final";
            CenterMain = m.ScoreText;
            CenterSub = m.HasPrediction ? $"Prévu {m.MostLikelyScore ?? "–"} · {m.PronoText.Replace("Prono ", "prono ")} {m.PronoMark}" : "";
        }
        else
        {
            CenterTop = m.IsPlayed ? m.StateText : "Score probable";
            CenterMain = string.IsNullOrEmpty(m.MostLikelyScore) ? "vs" : m.MostLikelyScore.Replace("-", " – ");
            CenterSub = m.HasPrediction ? $"Buts attendus {m.ExpGoalsText}" : "Pas de prédiction";
        }
        foreach (var name in new[] { nameof(HomeTeam), nameof(AwayTeam), nameof(HomeShort), nameof(AwayShort), nameof(HomeInfo),
                     nameof(AwayInfo), nameof(PHome), nameof(PDraw), nameof(PAway), nameof(PHomeLabel), nameof(PDrawLabel), nameof(PAwayLabel) })
            OnPropertyChanged(name);

        // Cotes, par marché
        var sel = m.Selections().Where(s => s.Model != null || s.Odd != null).ToList();
        Markets.Clear();
        void AddMarket(string name, params string[] keys)
        {
            var rows = sel.Where(s => keys.Contains(s.Key)).ToList();
            if (rows.Count > 0) Markets.Add(new MarketGroup(name, rows));
        }
        AddMarket("Résultat 1 N 2", "1X2_HOME", "1X2_DRAW", "1X2_AWAY");
        AddMarket("Double chance", "DC_1X", "DC_X2", "DC_12");
        AddMarket("Remboursé si nul", "DNB_HOME", "DNB_AWAY");
        AddMarket("Les deux équipes marquent", "BTTS_YES", "BTTS_NO");
        AddMarket("Buts ± 1,5", "OU15_OVER", "OU15_UNDER");
        AddMarket("Buts ± 2,5", "OU25_OVER", "OU25_UNDER");
        AddMarket("Buts ± 3,5", "OU35_OVER", "OU35_UNDER");
        OddsLabel = string.IsNullOrEmpty(m.OddsLabel) ? "Pas de cote relevée" : $"Dernières cotes relevées : {m.OddsLabel}";

        // Indices
        Indices.Clear();
        if (m.HasPrediction)
            foreach (var x in IndexEngine.Compute(m, st, league)) Indices.Add(x);
        Focus = Indices.FirstOrDefault();
        OnPropertyChanged(nameof(HasNoIndex));

        // Statistiques du match terminé
        MatchFacts.Clear();
        var row = rowTask.Result;
        if (row != null && row.IsFinished)
        {
            if (row.HomeXg != null) MatchFacts.Add(new("xG", Fmt.Num(row.HomeXg, 2), Fmt.Num(row.AwayXg, 2), row.HomeXg, row.AwayXg));
            if (row.HomePossession != null) MatchFacts.Add(new("Possession", $"{Fmt.Num(row.HomePossession, 0)} %", $"{Fmt.Num(row.AwayPossession, 0)} %", row.HomePossession, row.AwayPossession));
            if (row.HomeShots != null) MatchFacts.Add(new("Tirs", $"{row.HomeShots}", $"{row.AwayShots}", row.HomeShots, row.AwayShots));
            if (row.HomeSot != null) MatchFacts.Add(new("Tirs cadrés", $"{row.HomeSot}", $"{row.AwaySot}", row.HomeSot, row.AwaySot));
            if (row.HomeCorners != null) MatchFacts.Add(new("Corners", $"{row.HomeCorners}", $"{row.AwayCorners}", row.HomeCorners, row.AwayCorners));
        }

        // Comparaison avant le match
        PreStats.Clear();
        if (st != null) foreach (var c in Compare(st)) PreStats.Add(c);

        Signals.Clear();
        foreach (var s in sigTask.Result) Signals.Add(s);
        Bets.Clear();
        foreach (var b in betsTask.Result) Bets.Add(b);

        // Forme : 5 derniers matchs de chaque équipe avant ce match
        var homeForm = _data.GetTeamFormAsync(m.HomeTeam, m.KickoffUtc);
        var awayForm = _data.GetTeamFormAsync(m.AwayTeam, m.KickoffUtc);
        await Task.WhenAll(homeForm, awayForm);
        HomeForm.Clear();
        foreach (var f in homeForm.Result) HomeForm.Add(new FormLine(f, m.HomeTeam));
        AwayForm.Clear();
        foreach (var f in awayForm.Result) AwayForm.Add(new FormLine(f, m.AwayTeam));

        foreach (var name in new[] { nameof(HasMatchFacts), nameof(HasPreStats), nameof(HasNoStats), nameof(HasHomeForm),
                     nameof(HasAwayForm), nameof(HasNoHomeForm), nameof(HasNoAwayForm), nameof(HasSignals), nameof(HasBets) })
            OnPropertyChanged(name);
    });

    private static IEnumerable<CompareRow> Compare(MatchStats s)
    {
        static string Rank(int? r) => r is null ? "–" : r == 1 ? "1er" : $"{r}e";
        yield return new("Classement", Rank(s.HomeRank), Rank(s.AwayRank), s.HomeRank, s.AwayRank, higherIsBetter: false);
        yield return new("Elo", Fmt.Num(s.HomeElo, 0), Fmt.Num(s.AwayElo, 0), s.HomeElo, s.AwayElo);
        yield return new("Évolution Elo (5 matchs)", Fmt.Signed(s.HomeEloTrend5, 0), Fmt.Signed(s.AwayEloTrend5, 0));
        yield return new("Points par match (saison)", Fmt.Num(s.HomePpg, 2), Fmt.Num(s.AwayPpg, 2), s.HomePpg, s.AwayPpg);
        yield return new("Points attendus par match", Fmt.Num(s.HomeXppg, 2), Fmt.Num(s.AwayXppg, 2), s.HomeXppg, s.AwayXppg);
        yield return new("Points par match (5 derniers)", Fmt.Num(s.HomePts5, 2), Fmt.Num(s.AwayPts5, 2), s.HomePts5, s.AwayPts5);
        yield return new("Buts marqués (5 derniers)", Fmt.Num(s.HomeGf5), Fmt.Num(s.AwayGf5), s.HomeGf5, s.AwayGf5);
        yield return new("Buts encaissés (5 derniers)", Fmt.Num(s.HomeGa5), Fmt.Num(s.AwayGa5), s.HomeGa5, s.AwayGa5, higherIsBetter: false);
        yield return new("xG créés (5 derniers)", Fmt.Num(s.HomeXgf5), Fmt.Num(s.AwayXgf5), s.HomeXgf5, s.AwayXgf5);
        yield return new("xG concédés (5 derniers)", Fmt.Num(s.HomeXga5), Fmt.Num(s.AwayXga5), s.HomeXga5, s.AwayXga5, higherIsBetter: false);
        yield return new("Les deux marquent (10 derniers)", Fmt.Pct(s.HomeBtts10), Fmt.Pct(s.AwayBtts10));
        yield return new("+2,5 buts (10 derniers)", Fmt.Pct(s.HomeOver2510), Fmt.Pct(s.AwayOver2510));
        yield return new("Jours de repos", Fmt.Num(s.HomeRestDays, 0), Fmt.Num(s.AwayRestDays, 0));
    }

    private Task AddBetAsync()
    {
        if (_match == null) return Task.CompletedTask;
        var selections = _match.HasPrediction
            ? _match.Selections()
            : BetKeys.All.Select(k => new SelectionRow { Key = k, Label = BetKeys.Label(k, _match.HomeTeam, _match.AwayTeam) }).ToList();
        var best = selections.Where(s => s.Edge > 0).OrderByDescending(s => s.Edge).FirstOrDefault();
        var signal = Signals.FirstOrDefault();
        var draft = new BetDraft
        {
            MatchId = _match.MatchId,
            HomeTeam = _match.HomeTeam,
            AwayTeam = _match.AwayTeam,
            League = _match.League,
            KickoffUtc = _match.KickoffUtc,
            Selections = selections,
            PreselectedKey = signal?.BetKey ?? Focus?.Key ?? best?.Key,
            Strategy = signal?.Strategy
        };
        return GoToAsync(nameof(BetEditPage), new Dictionary<string, object> { ["draft"] = draft });
    }
}
