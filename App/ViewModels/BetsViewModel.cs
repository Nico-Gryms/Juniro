using System.Collections.ObjectModel;
using System.Windows.Input;
using Juniro.App.Models;
using Juniro.App.Services;
using Juniro.App.Views;

namespace Juniro.App.ViewModels;

/// <summary>
/// Onglet « Mes paris » :
///   - source : nos paris saisis, ou les paris fictifs des stratégies ;
///   - récapitulatif : la veille, les 3 derniers jours, le total (bénéfice, ROI, gagnés / perdus) ;
///   - bilan détaillé sur la période choisie, courbe du ROI ou du bénéfice cumulé,
///     répartition par stratégie et par ligue ;
///   - historique jour par jour.
/// Filtres : qui (moi / partenaire / les deux), période, ligue, stratégie.
/// Les paris s'ajoutent depuis la fiche d'un match (bouton « J'ai parié »).
/// </summary>
public sealed class BetsViewModel : BaseViewModel
{
    public const string All = "Toutes";

    private readonly DataService _data;
    private List<BetLine> _mine = new();
    private List<BetLine> _paper = new();
    private string _source = "user";
    private string _owner = "me";
    private string _period = "all";
    private string _curve = "roi";
    private string _league = All;
    private string _strategy = All;
    private bool _isRefreshing;
    private BetLine? _selectedLine;
    private IList<ChartPoint> _chartPoints = Array.Empty<ChartPoint>();
    private string _chartSuffix = " %";
    private string _betsText = "–", _stakesText = "–", _profitText = "–", _roiText = "–", _hitText = "–", _oddText = "–", _pendingText = "";
    private Color _profitColor = Palette.TextMuted;
    private string _verdict = "";
    private Color _verdictColor = Palette.TextMuted;
    private RecapCard? _yesterday, _last3, _total;

    public BetsViewModel(DataService data)
    {
        _data = data;
        RefreshCommand = new AsyncCommand(async () => { await LoadAsync(); IsRefreshing = false; });
        var source = new AsyncCommand(p => { Pick(Sources, p as string, v => _source = v); OnPropertyChanged(nameof(IsUserSource)); return Task.CompletedTask; });
        var owner = new AsyncCommand(p => { Pick(Owners, p as string, v => _owner = v); return Task.CompletedTask; });
        var period = new AsyncCommand(p => { Pick(Periods, p as string, v => _period = v); return Task.CompletedTask; });
        var curve = new AsyncCommand(p => { Pick(Curves, p as string, v => _curve = v); return Task.CompletedTask; });
        Sources = new() { new("Nos paris", "user", source), new("Paris fictifs", "paper", source) };
        Owners = new() { new("Moi", "me", owner), new("Partenaire", "other", owner), new("Les deux", "all", owner) };
        Periods = new() { new("Tout", "all", period), new("Saison", "season", period), new("30 jours", "30", period), new("7 jours", "7", period) };
        Curves = new() { new("ROI %", "roi", curve), new("Bénéfice", "profit", curve) };
        foreach (var c in Sources) c.IsSelected = c.Value == _source;
        foreach (var c in Owners) c.IsSelected = c.Value == _owner;
        foreach (var c in Periods) c.IsSelected = c.Value == _period;
        foreach (var c in Curves) c.IsSelected = c.Value == _curve;
    }

    public ObservableCollection<FilterChip> Sources { get; }
    public ObservableCollection<FilterChip> Owners { get; }
    public ObservableCollection<FilterChip> Periods { get; }
    public ObservableCollection<FilterChip> Curves { get; }
    public ObservableCollection<string> Leagues { get; } = new() { All };
    public ObservableCollection<string> Strategies { get; } = new() { All };
    public ObservableCollection<BetDay> Days { get; } = new();
    public ObservableCollection<BreakdownRow> ByStrategy { get; } = new();
    public ObservableCollection<BreakdownRow> ByLeague { get; } = new();
    public ICommand RefreshCommand { get; }

    public bool IsUserSource => _source == "user";
    public string SelectedLeague { get => _league; set { if (SetProperty(ref _league, value ?? All)) Apply(); } }
    public string SelectedStrategy { get => _strategy; set { if (SetProperty(ref _strategy, value ?? All)) Apply(); } }
    public bool IsRefreshing { get => _isRefreshing; set => SetProperty(ref _isRefreshing, value); }
    public IList<ChartPoint> ChartPoints { get => _chartPoints; set => SetProperty(ref _chartPoints, value); }
    public string ChartSuffix { get => _chartSuffix; set => SetProperty(ref _chartSuffix, value); }
    public string BetsText { get => _betsText; set => SetProperty(ref _betsText, value); }
    public string PendingText { get => _pendingText; set => SetProperty(ref _pendingText, value); }
    public string StakesText { get => _stakesText; set => SetProperty(ref _stakesText, value); }
    public string ProfitText { get => _profitText; set => SetProperty(ref _profitText, value); }
    public Color ProfitColor { get => _profitColor; set => SetProperty(ref _profitColor, value); }
    public string RoiText { get => _roiText; set => SetProperty(ref _roiText, value); }
    public string HitText { get => _hitText; set => SetProperty(ref _hitText, value); }
    public string OddText { get => _oddText; set => SetProperty(ref _oddText, value); }
    public string Verdict { get => _verdict; set => SetProperty(ref _verdict, value); }
    public Color VerdictColor { get => _verdictColor; set => SetProperty(ref _verdictColor, value); }
    public RecapCard? Yesterday { get => _yesterday; set => SetProperty(ref _yesterday, value); }
    public RecapCard? Last3 { get => _last3; set => SetProperty(ref _last3, value); }
    public RecapCard? Total { get => _total; set => SetProperty(ref _total, value); }
    public bool HasNoStrategyBreakdown => ByStrategy.Count == 0;

    /// <summary>Toucher un pari : le nôtre s'ouvre pour modification, un pari fictif ouvre la fiche du match.</summary>
    public BetLine? SelectedLine
    {
        get => _selectedLine;
        set
        {
            if (!SetProperty(ref _selectedLine, value) || value == null) return;
            var line = value;
            SelectedLine = null;
            _ = line.BetId is Guid id
                ? GoToAsync(nameof(BetEditPage), new Dictionary<string, object> { ["betId"] = id })
                : GoToAsync(nameof(MatchDetailPage), new Dictionary<string, object> { ["matchId"] = line.MatchId });
        }
    }

    public Task OnAppearingAsync() => LoadAsync();

    private Task LoadAsync() => RunAsync(async () =>
    {
        var mineTask = _data.GetBetsAsync();
        var paperTask = _data.GetAllSignalsAsync();
        await Task.WhenAll(mineTask, paperTask);
        _mine = mineTask.Result.Select(b => BetLine.From(b)).ToList();
        _paper = paperTask.Result.Select(s => BetLine.From(s)).ToList();
        RefillFilters();
        Apply();
    });

    private List<BetLine> Source => _source == "user" ? _mine : _paper;

    private void RefillFilters()
    {
        Refill(Leagues, Source.Select(b => b.League), ref _league, nameof(SelectedLeague));
        Refill(Strategies, Source.Select(b => b.StrategyText), ref _strategy, nameof(SelectedStrategy));
    }

    private void Refill(ObservableCollection<string> target, IEnumerable<string> values, ref string selected, string propertyName)
    {
        var keep = selected;
        target.Clear();
        target.Add(All);
        foreach (var value in values.Where(v => !string.IsNullOrEmpty(v)).Distinct().OrderBy(x => x)) target.Add(value);
        selected = target.Contains(keep) ? keep : All;
        OnPropertyChanged(propertyName);
    }

    private void Pick(ObservableCollection<FilterChip> chips, string? value, Action<string> set)
    {
        if (value == null) return;
        bool sourceChanged = ReferenceEquals(chips, Sources) && value != _source;
        set(value);
        foreach (var c in chips) c.IsSelected = c.Value == value;
        if (sourceChanged) RefillFilters();
        Apply();
    }

    /// <summary>Applique les filtres puis recalcule récapitulatifs, bilan, courbe, répartitions et historique.</summary>
    private void Apply()
    {
        // Filtres qui s'appliquent partout : qui, ligue, stratégie
        IEnumerable<BetLine> q = Source;
        if (_source == "user")
        {
            var me = _data.CurrentUserId;
            if (_owner == "me") q = q.Where(b => b.UserId == me);
            else if (_owner == "other") q = q.Where(b => b.UserId != me);
        }
        if (_league != All) q = q.Where(b => b.League == _league);
        if (_strategy != All) q = q.Where(b => b.StrategyText == _strategy);
        var scoped = q.ToList();

        // Récapitulatif par date de match : hier, 3 derniers jours, total
        var today = DateTime.Today;
        var yesterday = today.AddDays(-1);
        Yesterday = new RecapCard("Hier", yesterday.ToString("ddd d MMM", Fmt.Fr), scoped.Where(b => b.Day == yesterday).ToList());
        Last3 = new RecapCard("3 derniers jours", $"du {today.AddDays(-3):dd/MM} au {yesterday:dd/MM}",
            scoped.Where(b => b.Day >= today.AddDays(-3) && b.Day <= yesterday).ToList());
        Total = new RecapCard("Total", "depuis le début", scoped);

        // Période du bilan détaillé et de l'historique
        var now = DateTimeOffset.Now;
        DateTimeOffset? from = _period switch
        {
            "7" => now.AddDays(-7),
            "30" => now.AddDays(-30),
            // Saison : depuis le 1er juillet
            "season" => new DateTimeOffset(new DateTime(now.Month >= 7 ? now.Year : now.Year - 1, 7, 1)),
            _ => null
        };
        var list = from == null ? scoped : scoped.Where(b => b.KickoffUtc >= from).ToList();

        // Historique, jour par jour (le plus récent en haut)
        Days.Clear();
        foreach (var g in list.OrderByDescending(b => b.KickoffUtc).ThenByDescending(b => b.PlacedAt).GroupBy(b => b.Day))
            Days.Add(new BetDay(g.Key, g));

        // Bilan (paris réglés seulement pour le ROI)
        var settled = list.Where(b => b.IsSettled).OrderBy(b => b.KickoffUtc).ThenBy(b => b.PlacedAt).ToList();
        var decided = settled.Where(b => b.IsDecided).ToList();
        double stakes = settled.Where(b => b.Result != "VOID").Sum(b => b.Stake);
        double profit = settled.Sum(b => b.Profit ?? 0);
        double? roi = stakes > 0 ? 100 * profit / stakes : null;
        int pending = list.Count - settled.Count;

        BetsText = list.Count.ToString();
        PendingText = pending == 0 ? "" : $"dont {pending} en attente";
        StakesText = Fmt.Num(stakes, 2);
        ProfitText = Fmt.Signed(profit, 2);
        ProfitColor = Palette.ForSign(profit);
        RoiText = Fmt.Signed(roi, 1, " %");
        HitText = decided.Count == 0 ? "–" : Fmt.Pct((double)decided.Count(b => b.Result == "WIN") / decided.Count, 1);
        OddText = decided.Count == 0 ? "–" : Fmt.Odd(decided.Average(b => b.Odd));
        Verdict = Palette.VerdictOf(roi, decided.Count);
        VerdictColor = Palette.ForVerdict(Verdict);

        // Courbe : ROI cumulé (bénéfice / mises) ou bénéfice cumulé, pari après pari
        var points = new List<ChartPoint>();
        double cumStake = 0, cumProfit = 0;
        foreach (var bet in settled)
        {
            if (bet.Result != "VOID") cumStake += bet.Stake;
            cumProfit += bet.Profit ?? 0;
            double y = _curve == "roi" ? (cumStake > 0 ? 100 * cumProfit / cumStake : 0) : cumProfit;
            points.Add(new ChartPoint(bet.KickoffUtc.ToLocalTime().DateTime, y));
        }
        ChartSuffix = _curve == "roi" ? " %" : "";
        ChartPoints = points;

        // Répartitions
        Fill(ByStrategy, settled.GroupBy(b => b.StrategyText));
        Fill(ByLeague, settled.GroupBy(b => b.League));
        OnPropertyChanged(nameof(HasNoStrategyBreakdown));
    }

    private static void Fill(ObservableCollection<BreakdownRow> target, IEnumerable<IGrouping<string, BetLine>> groups)
    {
        target.Clear();
        foreach (var row in groups
                     .Select(g => new BreakdownRow
                     {
                         Name = g.Key,
                         Bets = g.Count(),
                         Stakes = g.Where(b => b.Result != "VOID").Sum(b => b.Stake),
                         Profit = g.Sum(b => b.Profit ?? 0)
                     })
                     .OrderByDescending(r => r.Profit))
            target.Add(row);
    }
}
