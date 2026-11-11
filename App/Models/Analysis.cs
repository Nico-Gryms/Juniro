using System.Text.Json.Serialization;
using Juniro.App.Services;

namespace Juniro.App.Models;

/// <summary>Match terminé avec sa prédiction (vue Supabase v_prediction_results).</summary>
public sealed class PredictionResult
{
    public long MatchId { get; set; }
    public string League { get; set; } = "";
    public string? Season { get; set; }
    public DateTimeOffset KickoffUtc { get; set; }
    public string HomeTeam { get; set; } = "";
    public string AwayTeam { get; set; } = "";
    public int HomeGoals { get; set; }
    public int AwayGoals { get; set; }
    public double? ExpHomeGoals { get; set; }
    public double? ExpAwayGoals { get; set; }
    public double? ExpTotalGoals { get; set; }
    public string? MostLikelyScore { get; set; }
    public double? PHome { get; set; }
    public double? PDraw { get; set; }
    public double? PAway { get; set; }
    public double? PBttsYes { get; set; }
    [JsonPropertyName("p_over25")] public double? POver25 { get; set; }
    public double? MktPHome { get; set; }
    public double? MktPDraw { get; set; }
    public double? MktPAway { get; set; }
    public string? Actual { get; set; }
    public string? Predicted { get; set; }
    [JsonPropertyName("correct_1x2")] public bool? Correct1x2 { get; set; }
    public bool? CorrectBtts { get; set; }
    [JsonPropertyName("correct_over25")] public bool? CorrectOver25 { get; set; }
    public bool? CorrectScore { get; set; }

    [JsonIgnore] public string KickoffText => Fmt.Kickoff(KickoffUtc);
    [JsonIgnore] public string ScoreText => $"{HomeGoals} – {AwayGoals}";
    [JsonIgnore] public string PredictedText => Predicted switch { "H" => "1", "D" => "N", "A" => "2", _ => "?" };
    [JsonIgnore] public string ProbText => $"1 {Fmt.Pct(PHome)} · N {Fmt.Pct(PDraw)} · 2 {Fmt.Pct(PAway)}";
    [JsonIgnore] public string GoalsText => $"Attendus {Fmt.Num(ExpTotalGoals)} · réels {HomeGoals + AwayGoals}" +
                                            (string.IsNullOrEmpty(MostLikelyScore) ? "" : $" · score prévu {MostLikelyScore}");
    [JsonIgnore] public string Mark1x2 => Correct1x2 == true ? "✓" : "✗";
    [JsonIgnore] public Color Color1x2 => Correct1x2 == true ? Palette.Win : Palette.Lose;
    [JsonIgnore] public string MarkBtts => CorrectBtts == true ? "BTTS ✓" : "BTTS ✗";
    [JsonIgnore] public Color ColorBtts => CorrectBtts == true ? Palette.Win : Palette.TextDim;
    [JsonIgnore] public string MarkOver => CorrectOver25 == true ? "±2,5 ✓" : "±2,5 ✗";
    [JsonIgnore] public Color ColorOver => CorrectOver25 == true ? Palette.Win : Palette.TextDim;
    [JsonIgnore] public double BarHome => PHome ?? 0;
    [JsonIgnore] public double BarDraw => PDraw ?? 0;
    [JsonIgnore] public double BarAway => PAway ?? 0;
}

/// <summary>Indicateurs de précision par ligue (fonction Supabase prediction_kpis).</summary>
public sealed class PredictionKpi
{
    public string League { get; set; } = "";
    public int Matches { get; set; }
    [JsonPropertyName("pct_1x2")] public double? Pct1x2 { get; set; }
    public double? PctBtts { get; set; }
    [JsonPropertyName("pct_over25")] public double? PctOver25 { get; set; }
    public double? PctScore { get; set; }
    public double? LoglossModel { get; set; }
    public double? LoglossMarket { get; set; }
    public int MatchesMarket { get; set; }
    public double? AvgExpGoals { get; set; }
    public double? AvgGoals { get; set; }

    // Plus/Moins 1,5 et 3,5 (ajout du 10/11/2026) : % bien prévus et perte log modèle / marché
    [JsonPropertyName("pct_over15")] public double? PctOver15 { get; set; }
    [JsonPropertyName("pct_over35")] public double? PctOver35 { get; set; }
    [JsonPropertyName("ll_over15_model")] public double? LlOver15Model { get; set; }
    [JsonPropertyName("ll_over15_market")] public double? LlOver15Market { get; set; }
    [JsonPropertyName("ll_over25_model")] public double? LlOver25Model { get; set; }
    [JsonPropertyName("ll_over25_market")] public double? LlOver25Market { get; set; }
    [JsonPropertyName("ll_over35_model")] public double? LlOver35Model { get; set; }
    [JsonPropertyName("ll_over35_market")] public double? LlOver35Market { get; set; }

    [JsonIgnore] public string PctOver15Text => Fmt.Pct100(PctOver15);
    [JsonIgnore] public string PctOver35Text => Fmt.Pct100(PctOver35);
    [JsonIgnore] public string LlOver15Text => LossPair(LlOver15Model, LlOver15Market);
    [JsonIgnore] public string LlOver25Text => LossPair(LlOver25Model, LlOver25Market);
    [JsonIgnore] public string LlOver35Text => LossPair(LlOver35Model, LlOver35Market);
    [JsonIgnore] public Color LlOver15Color => LossColor(LlOver15Model, LlOver15Market);
    [JsonIgnore] public Color LlOver25Color => LossColor(LlOver25Model, LlOver25Market);
    [JsonIgnore] public Color LlOver35Color => LossColor(LlOver35Model, LlOver35Market);

    private static string LossPair(double? model, double? market) =>
        model is null || market is null ? "pas de cotes" : $"{Fmt.Num(model, 4)} / {Fmt.Num(market, 4)}";
    private static Color LossColor(double? model, double? market) =>
        model is null || market is null ? Palette.TextMuted : model < market ? Palette.Win : Palette.Even;

    [JsonIgnore] public string Pct1x2Text => Fmt.Pct100(Pct1x2);
    [JsonIgnore] public string PctBttsText => Fmt.Pct100(PctBtts);
    [JsonIgnore] public string PctOverText => Fmt.Pct100(PctOver25);
    [JsonIgnore] public string PctScoreText => Fmt.Pct100(PctScore);
    [JsonIgnore] public string GoalsText => $"{Fmt.Num(AvgExpGoals, 2)} / {Fmt.Num(AvgGoals, 2)}";
    [JsonIgnore] public double? Gap => LoglossModel is null || LoglossMarket is null ? null : LoglossModel - LoglossMarket;
    [JsonIgnore] public string LoglossText => LoglossModel is null ? "–" : $"{Fmt.Num(LoglossModel, 4)} / {Fmt.Num(LoglossMarket, 4)}";
    [JsonIgnore] public string VsMarketText => Gap is null ? "Pas de cotes" : Gap < 0 ? "Modèle meilleur que le marché" : "Marché meilleur que le modèle";
    [JsonIgnore] public Color VsMarketColor => Gap is null ? Palette.TextMuted : Gap < 0 ? Palette.Win : Palette.Even;
    [JsonIgnore] public string MatchesText => $"{Matches} matchs";
}

/// <summary>Bilan d'une stratégie simulée dans une ligue (vue Supabase v_strategy_overview).</summary>
public sealed class StrategyOverview
{
    public string Family { get; set; } = "";
    public string League { get; set; } = "";
    public string? StrategyName { get; set; }
    public double? RoiTest { get; set; }
    public int? BetsTest { get; set; }
    public double? ProfitTest { get; set; }
    public double? RoiTotal { get; set; }
    public int? BetsTotal { get; set; }
    public int SeasonsPositive { get; set; }
    public int Seasons { get; set; }
    public string Verdict { get; set; } = "";

    [JsonIgnore] public string Title => BetKeys.Strategy(Family);
    [JsonIgnore] public Color VerdictColor => Palette.ForVerdict(Verdict);
    [JsonIgnore] public string RoiTestText => Fmt.Signed(RoiTest, 1, " %");
    [JsonIgnore] public Color RoiTestColor => Palette.ForSign(RoiTest);
    [JsonIgnore] public string DetailText =>
        $"Test : {BetsTest ?? 0} paris, {Fmt.Signed(ProfitTest, 1, " u")} · total {Fmt.Signed(RoiTotal, 1, " %")} · {SeasonsPositive}/{Seasons} saisons +";
}

/// <summary>Une saison d'une stratégie simulée (table Supabase strategy_reports).</summary>
public sealed class StrategyReport
{
    public string Family { get; set; } = "";
    public string League { get; set; } = "";
    public string Season { get; set; } = "";
    public string? StrategyName { get; set; }
    public string? Period { get; set; }
    public int? Bets { get; set; }
    public double? Profit { get; set; }
    public double? RoiPct { get; set; }
    public double? HitPct { get; set; }
    public double? AvgOdd { get; set; }
    public int? SortOrder { get; set; }

    [JsonIgnore] public string RoiText => Fmt.Signed(RoiPct, 1, " %");
    [JsonIgnore] public Color RoiColor => Palette.ForSign(RoiPct);
    [JsonIgnore] public string DetailText => $"{Bets ?? 0} paris · {Fmt.Signed(Profit, 1, " u")} · réussite {Fmt.Pct100(HitPct)} · cote {Fmt.Odd(AvgOdd)}";
    [JsonIgnore] public string PeriodText => string.IsNullOrEmpty(Period) ? "" : Period;
    /// <summary>Largeur relative de la barre (ROI de −30 % à +30 %).</summary>
    [JsonIgnore] public double BarWidth => Math.Min(Math.Abs(RoiPct ?? 0), 30) / 30 * 140 + 2;
}

/// <summary>Bilan des paris fictifs d'une stratégie active (vue Supabase v_paper_summary).</summary>
public sealed class PaperSummary
{
    public string Strategy { get; set; } = "";
    public string? League { get; set; }
    public int Bets { get; set; }
    public int Pending { get; set; }
    public int Won { get; set; }
    public int Lost { get; set; }
    public double? Stakes { get; set; }
    public double? Profit { get; set; }
    public double? RoiPct { get; set; }
    public string Verdict { get; set; } = "";

    [JsonIgnore] public string Title => BetKeys.Strategy(Strategy);
    [JsonIgnore] public Color VerdictColor => Palette.ForVerdict(Verdict);
    [JsonIgnore] public string RoiText => Fmt.Signed(RoiPct, 1, " %");
    [JsonIgnore] public Color RoiColor => Palette.ForSign(RoiPct);
    [JsonIgnore] public string DetailText => $"{Won} gagnés · {Lost} perdus · {Pending} en attente · {Fmt.Signed(Profit, 2, " u")}";
}

/// <summary>Pari fictif d'une stratégie (vue Supabase v_signals).</summary>
public sealed class SignalRow
{
    public long MatchId { get; set; }
    public string Strategy { get; set; } = "";
    public string Market { get; set; } = "";
    public string Selection { get; set; } = "";
    public double? LineValue { get; set; }
    public double? Probability { get; set; }
    public double? Odd { get; set; }
    public string? OddLabel { get; set; }
    public double? Stake { get; set; }
    public string? Result { get; set; }
    public double? Profit { get; set; }
    public string League { get; set; } = "";
    public DateTimeOffset KickoffUtc { get; set; }
    public string HomeTeam { get; set; } = "";
    public string AwayTeam { get; set; } = "";
    public int? HomeGoals { get; set; }
    public int? AwayGoals { get; set; }
    public string? MatchStatus { get; set; }

    [JsonIgnore] public string BetKey => BetKeys.FromMarket(Market, Selection);
    [JsonIgnore] public string BetText => $"{BetKeys.Label(BetKey, HomeTeam, AwayTeam)} @ {Fmt.Odd(Odd)}";
    [JsonIgnore] public string StrategyText => BetKeys.Strategy(Strategy);
    [JsonIgnore] public string MatchText => $"{HomeTeam} – {AwayTeam}";
    [JsonIgnore] public string KickoffText => Fmt.Kickoff(KickoffUtc);
    [JsonIgnore] public string ResultText => Result switch
    {
        "WIN" => $"Gagné {Fmt.Signed(Profit, 2, " u")}",
        "LOSE" => $"Perdu {Fmt.Signed(Profit, 2, " u")}",
        "PUSH" => "Remboursé",
        _ => $"En attente · mise {Fmt.Num(Stake, 2)}"
    };
    [JsonIgnore] public Color ResultColor => Palette.ForResult(Result);
    [JsonIgnore] public string ScoreText => HomeGoals is null ? "" : $"{HomeGoals} – {AwayGoals}";
}

/// <summary>Dernier envoi de données vers Supabase (table sync_log).</summary>
public sealed class SyncInfo
{
    public long Id { get; set; }
    public DateTimeOffset StartedAt { get; set; }
    public DateTimeOffset? FinishedAt { get; set; }
    public string? Status { get; set; }
    /// <summary>Lignes envoyées par table : {"matches": 16196, ...}.</summary>
    public Dictionary<string, int>? Details { get; set; }

    [JsonIgnore] public string DateText => FinishedAt is { } f ? $"{Fmt.DayLong(f.ToLocalTime().DateTime)} · {f.ToLocalTime():HH:mm}" : "Jamais";
    [JsonIgnore] public string StatusText => Status == "Success" ? "OK" : Status ?? "?";
    [JsonIgnore] public Color StatusColor => Status == "Success" ? Palette.Win : Palette.Even;
    [JsonIgnore] public Color StatusBackground => Status == "Success" ? Palette.WinSoft : Palette.CardAlt;
    [JsonIgnore] public string DetailsText => Details == null || Details.Count == 0 ? "" :
        string.Join(" · ", Details.Select(kv => $"{kv.Key} {kv.Value.ToString("N0", Fmt.Fr)}"));
    [JsonIgnore] public string DurationText => FinishedAt is { } f ? Journal.Duration((int)(f - StartedAt).TotalSeconds) : "";
}

/// <summary>Profil d'un utilisateur (table profiles).</summary>
public sealed class Profile
{
    public Guid Id { get; set; }
    public string DisplayName { get; set; } = "";
}

/// <summary>Bouton de filtre (« Aujourd'hui », « Moi »...) avec son état et sa commande.</summary>
public sealed class FilterChip : Juniro.App.ViewModels.ObservableObject
{
    private bool _isSelected;

    private string _label;

    public FilterChip(string label, string value, System.Windows.Input.ICommand command)
    {
        _label = label;
        Value = value;
        Command = command;
    }

    /// <summary>Texte affiché (modifiable : « Joués 4 » quand le nombre change).</summary>
    public string Label { get => _label; set => SetProperty(ref _label, value); }
    public string Value { get; }
    public System.Windows.Input.ICommand Command { get; }

    public bool IsSelected
    {
        get => _isSelected;
        set
        {
            if (SetProperty(ref _isSelected, value))
            {
                OnPropertyChanged(nameof(Background));
                OnPropertyChanged(nameof(Foreground));
                OnPropertyChanged(nameof(Stroke));
                OnPropertyChanged(nameof(SegBackground));
                OnPropertyChanged(nameof(SegForeground));
            }
        }
    }

    // Puce arrondie (ChipTemplate) : accent quand elle est choisie
    public Color Background => IsSelected ? Palette.Accent : Palette.Card;
    public Color Foreground => IsSelected ? Palette.OnAccent : Palette.TextSoft;
    public Brush Stroke => new SolidColorBrush(IsSelected ? Palette.Accent : Palette.Line);

    // Case d'un contrôle segmenté (SegmentTemplate) : fond plus clair quand elle est choisie
    public Color SegBackground => IsSelected ? Palette.SegmentOn : Colors.Transparent;
    public Color SegForeground => IsSelected ? Palette.Text : Palette.TextMuted;
}

/// <summary>Une ligne de répartition (par ligue, par stratégie) dans le bilan des paris.</summary>
public sealed class BreakdownRow
{
    public string Name { get; init; } = "";
    public int Bets { get; init; }
    public double Stakes { get; init; }
    public double Profit { get; init; }
    public double? Roi => Stakes > 0 ? 100 * Profit / Stakes : null;

    public string RoiText => Fmt.Signed(Roi, 1, " %");
    public Color RoiColor => Palette.ForSign(Roi);
    public string DetailText => $"{Bets} paris · {Fmt.Signed(Profit, 2, " u")}";
}

/// <summary>Un point d'une courbe (date, valeur).</summary>
public readonly record struct ChartPoint(DateTime X, double Y);
