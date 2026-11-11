using System.Collections.ObjectModel;
using Juniro.App.Models;
using Juniro.App.Services;
using Juniro.App.Views;

namespace Juniro.App.ViewModels;

/// <summary>
/// Détail d'une stratégie. Deux cas (paramètres de navigation) :
///   family + league : stratégie simulée -> bilan saison par saison du dernier backtest ;
///   paper           : stratégie en paris fictifs -> courbe du ROI et liste des paris.
/// </summary>
public sealed class StrategyDetailViewModel : BaseViewModel, IQueryAttributable
{
    private readonly DataService _data;
    private string? _family, _league, _paper;
    private string _title = "Stratégie", _subtitle = "", _summary = "";
    private IList<ChartPoint> _chart = Array.Empty<ChartPoint>();

    public StrategyDetailViewModel(DataService data)
    {
        _data = data;
        OpenMatchCommand = new AsyncCommand(p => p is SignalRow s
            ? GoToAsync(nameof(MatchDetailPage), new Dictionary<string, object> { ["matchId"] = s.MatchId })
            : Task.CompletedTask);
    }

    /// <summary>Ouvre la fiche du match d'un pari fictif (paramètre : SignalRow).</summary>
    public System.Windows.Input.ICommand OpenMatchCommand { get; }

    public string Title { get => _title; set => SetProperty(ref _title, value); }
    public string Subtitle { get => _subtitle; set => SetProperty(ref _subtitle, value); }
    public string Summary { get => _summary; set => SetProperty(ref _summary, value); }
    public IList<ChartPoint> ChartPoints { get => _chart; set => SetProperty(ref _chart, value); }
    public bool IsPaper => _paper != null;
    public bool IsSimulated => _paper == null;
    public ObservableCollection<StrategyReport> Seasons { get; } = new();
    public ObservableCollection<SignalRow> Signals { get; } = new();

    public void ApplyQueryAttributes(IDictionary<string, object> query)
    {
        _family = query.TryGetValue("family", out var f) ? f as string : null;
        _league = query.TryGetValue("league", out var l) ? l as string : null;
        _paper = query.TryGetValue("paper", out var p) ? p as string : null;
        OnPropertyChanged(nameof(IsPaper));
        OnPropertyChanged(nameof(IsSimulated));
    }

    public Task OnAppearingAsync() => RunAsync(async () =>
    {
        if (_paper != null) await LoadPaperAsync(_paper);
        else if (_family != null && _league != null) await LoadSimulatedAsync(_family, _league);
    });

    private async Task LoadSimulatedAsync(string family, string league)
    {
        Title = BetKeys.Strategy(family);
        Subtitle = $"{league} · stratégie simulée (dernier backtest)";
        var rows = await _data.GetStrategySeasonsAsync(family, league);
        Seasons.Clear();
        foreach (var r in rows) Seasons.Add(r);
        var test = rows.FirstOrDefault(r => r.Season == "Test");
        var total = rows.FirstOrDefault(r => r.Season == "Total");
        string verdict = Palette.VerdictOf(test?.RoiPct, test?.Bets ?? 0, minBets: 1);
        Summary = test == null
            ? "Pas de résultat sur la période de test."
            : $"{verdict} sur la période de test : {test.RoiText} sur {test.Bets} paris ({Fmt.Signed(test.Profit, 1, " u")})." +
              (total == null ? "" : $" Toutes saisons : {total.RoiText}.") +
              " La variante a été choisie sur les saisons de réglage : seul le test compte.";
    }

    private async Task LoadPaperAsync(string strategy)
    {
        Title = BetKeys.Strategy(strategy);
        var rows = await _data.GetSignalsForStrategyAsync(strategy);
        Subtitle = $"{rows.FirstOrDefault()?.League ?? ""} · paris fictifs, seuils figés";
        Signals.Clear();
        foreach (var r in rows) Signals.Add(r);

        var settled = rows.Where(r => r.Result is "WIN" or "LOSE" or "PUSH").OrderBy(r => r.KickoffUtc).ToList();
        double stake = 0, profit = 0;
        var pts = new List<ChartPoint>();
        foreach (var sig in settled)
        {
            stake += sig.Stake ?? 0;
            profit += sig.Profit ?? 0;
            pts.Add(new ChartPoint(sig.KickoffUtc.ToLocalTime().DateTime, stake > 0 ? 100 * profit / stake : 0));
        }
        ChartPoints = pts;
        double? roi = stake > 0 ? 100 * profit / stake : null;
        int pending = rows.Count - settled.Count;
        Summary = settled.Count == 0
            ? $"Aucun pari réglé pour l'instant ({pending} en attente)."
            : $"{settled.Count} paris réglés · ROI {Fmt.Signed(roi, 1, " %")} · bénéfice {Fmt.Signed(profit, 2, " u")}" +
              (pending > 0 ? $" · {pending} en attente" : "") +
              $". Verdict : {Palette.VerdictOf(roi, settled.Count)}.";
    }
}
