using System.Text.Json.Serialization;
using System.Windows.Input;
using Juniro.App.Services;

namespace Juniro.App.Models;

// Les noms de propriétés sont convertis automatiquement en snake_case (MatchId -> match_id).
// Les propriétés contenant des chiffres portent leur nom exact ([JsonPropertyName]).

/// <summary>
/// Un match avec sa prédiction, toutes ses cotes et l'Elo des équipes (vue Supabase v_matches).
/// State : FINISHED (score connu), PLAYED (joué, score à la prochaine copie), LIVE (commencé
/// il y a moins de 2 h), UPCOMING, POSTPONED.
/// </summary>
public sealed class MatchItem
{
    public long MatchId { get; set; }
    public string League { get; set; } = "";
    public string? Country { get; set; }
    public int? Tier { get; set; }
    public string? Season { get; set; }
    public DateTimeOffset KickoffUtc { get; set; }
    public int? RoundNumber { get; set; }
    public string Status { get; set; } = "";
    public string HomeTeam { get; set; } = "";
    public string AwayTeam { get; set; } = "";
    public int? HomeGoals { get; set; }
    public int? AwayGoals { get; set; }
    public double? HomeXg { get; set; }
    public double? AwayXg { get; set; }
    public string State { get; set; } = "UPCOMING";
    public string? Source { get; set; }
    public double? ExpHomeGoals { get; set; }
    public double? ExpAwayGoals { get; set; }
    public double? ExpTotalGoals { get; set; }
    public string? MostLikelyScore { get; set; }
    public double? PHome { get; set; }
    public double? PDraw { get; set; }
    public double? PAway { get; set; }
    public double? PBttsYes { get; set; }
    [JsonPropertyName("p_over15")] public double? POver15 { get; set; }
    [JsonPropertyName("p_over25")] public double? POver25 { get; set; }
    [JsonPropertyName("p_over35")] public double? POver35 { get; set; }
    public double? MktPHome { get; set; }
    public double? MktPDraw { get; set; }
    public double? MktPAway { get; set; }
    public double? MktPBttsYes { get; set; }
    [JsonPropertyName("mkt_p_over25")] public double? MktPOver25 { get; set; }
    public double? OddHome { get; set; }
    public double? OddDraw { get; set; }
    public double? OddAway { get; set; }
    [JsonPropertyName("odd_dc_1x")] public double? OddDc1X { get; set; }
    [JsonPropertyName("odd_dc_x2")] public double? OddDcX2 { get; set; }
    public double? OddDnbHome { get; set; }
    public double? OddDnbAway { get; set; }
    public double? OddBttsYes { get; set; }
    public double? OddBttsNo { get; set; }
    [JsonPropertyName("odd_over15")] public double? OddOver15 { get; set; }
    [JsonPropertyName("odd_under15")] public double? OddUnder15 { get; set; }
    [JsonPropertyName("odd_over25")] public double? OddOver25 { get; set; }
    [JsonPropertyName("odd_under25")] public double? OddUnder25 { get; set; }
    [JsonPropertyName("odd_over35")] public double? OddOver35 { get; set; }
    [JsonPropertyName("odd_under35")] public double? OddUnder35 { get; set; }
    public string? OddsLabel { get; set; }
    public double? HomeElo { get; set; }
    public double? AwayElo { get; set; }
    public int? HomeRank { get; set; }
    public int? AwayRank { get; set; }
    public int SignalCount { get; set; }
    public string? Strategies { get; set; }

    // ------------------------------------------------------------------ état
    [JsonIgnore] public bool IsFinished => State == "FINISHED";
    [JsonIgnore] public bool IsPlayed => State is "PLAYED" or "LIVE";
    [JsonIgnore] public bool IsUpcoming => State == "UPCOMING";
    [JsonIgnore] public bool IsOver => IsFinished || IsPlayed;
    [JsonIgnore] public bool ShowTeamInfo => !IsFinished;
    [JsonIgnore] public bool HasPrediction => PHome != null;
    [JsonIgnore] public DateTime KickoffLocal => KickoffUtc.ToLocalTime().DateTime;
    [JsonIgnore] public string TimeText => KickoffUtc.ToLocalTime().ToString("HH:mm");
    [JsonIgnore] public string KickoffText => Fmt.Kickoff(KickoffUtc);
    [JsonIgnore] public string StateText => State switch
    {
        "FINISHED" => "Terminé",
        "PLAYED" => "Joué · score demain",
        "LIVE" => "En cours",
        "POSTPONED" => "Reporté",
        _ => TimeText
    };
    [JsonIgnore] public Color StateColor => State switch
    {
        "LIVE" => Palette.Accent,
        "UPCOMING" => Palette.Text,
        _ => Palette.TextMuted
    };
    [JsonIgnore] public string ScoreText => IsFinished ? $"{HomeGoals} – {AwayGoals}" : "";
    [JsonIgnore] public string HomeGoalsText => IsFinished ? $"{HomeGoals}" : "";
    [JsonIgnore] public string AwayGoalsText => IsFinished ? $"{AwayGoals}" : "";

    // ------------------------------------------------------------------ équipes
    [JsonIgnore] public string HomeShort => Fmt.Initials(HomeTeam);
    [JsonIgnore] public string AwayShort => Fmt.Initials(AwayTeam);
    [JsonIgnore] public string HomeInfo => TeamInfo(HomeRank, HomeElo);
    [JsonIgnore] public string AwayInfo => TeamInfo(AwayRank, AwayElo);
    [JsonIgnore] public string LeagueText => (League ?? "").ToUpperInvariant();
    [JsonIgnore] public Color LeagueColor => Leagues.ColorOf(League);

    private static string TeamInfo(int? rank, double? elo)
    {
        var parts = new List<string>();
        if (rank != null) parts.Add(rank == 1 ? "1er" : $"{rank}e");
        if (elo != null) parts.Add($"Elo {Fmt.Num(elo, 0)}");
        return string.Join(" · ", parts);
    }

    // ------------------------------------------------------------------ prédiction
    [JsonIgnore] public double BarHome => PHome ?? 0;
    [JsonIgnore] public double BarDraw => PDraw ?? 0;
    [JsonIgnore] public double BarAway => PAway ?? 0;
    [JsonIgnore] public string PHomeText => Fmt.Pct(PHome);
    [JsonIgnore] public string PDrawText => Fmt.Pct(PDraw);
    [JsonIgnore] public string PAwayText => Fmt.Pct(PAway);
    [JsonIgnore] public string ExpGoalsText => $"{Fmt.Num(ExpHomeGoals)} – {Fmt.Num(ExpAwayGoals)}";

    /// <summary>Pronostic 1N2 du modèle : la sélection la plus probable.</summary>
    [JsonIgnore] public string PredictedKey =>
        PHome >= PDraw && PHome >= PAway ? "1X2_HOME" : PAway >= PDraw ? "1X2_AWAY" : "1X2_DRAW";
    [JsonIgnore] public string PronoText => !HasPrediction ? "" :
        $"Prono {BetKeys.Short(PredictedKey)} · {Fmt.Pct(Math.Max(PHome ?? 0, Math.Max(PDraw ?? 0, PAway ?? 0)))}";

    /// <summary>Pronostic juste (true), faux (false), ou pas encore de score (null).</summary>
    [JsonIgnore] public bool? PronoOk => !IsFinished || !HasPrediction ? null :
        Settle.Result(PredictedKey, HomeGoals, AwayGoals) == "WIN";
    [JsonIgnore] public string PronoMark => PronoOk switch { true => "✓", false => "✕", _ => "" };
    [JsonIgnore] public Color PronoColor => PronoOk switch { true => Palette.Win, false => Palette.Lose, _ => Palette.TextSoft };
    [JsonIgnore] public Color PronoBackground => PronoOk switch { true => Palette.WinSoft, false => Palette.LoseSoft, _ => Palette.CardAlt };

    /// <summary>Les 16 paris de ce match avec probabilités, cotes et valeur.</summary>
    public List<SelectionRow> Selections() => SelectionRow.Build(HomeTeam, AwayTeam,
        PHome, PDraw, PAway, PBttsYes, POver15, POver25, POver35,
        MktPHome, MktPDraw, MktPAway, MktPBttsYes, MktPOver25,
        OddHome, OddDraw, OddAway, OddBttsYes, OddBttsNo, OddOver25, OddUnder25,
        OddDc1X, OddDcX2, OddDnbHome, OddDnbAway, OddOver15, OddUnder15, OddOver35, OddUnder35);

    /// <summary>Le pari où le modèle voit le plus de valeur (probabilité × cote − 1), s'il y en a.</summary>
    [JsonIgnore] public SelectionRow? BestValue => _bestValue ??= Selections().Where(s => s.Edge > 0).OrderByDescending(s => s.Edge).FirstOrDefault();
    private SelectionRow? _bestValue;
    [JsonIgnore] public bool HasValue => BestValue != null && !IsOver;
    [JsonIgnore] public string ValueText => BestValue is { } b ? $"Valeur {BetKeys.Short(b.Key)} · {Fmt.Signed(b.Edge * 100, 1, " %")}" : "";

    /// <summary>Les trois cotes 1 / N / 2 de la carte ; la sélection 1N2 avec le plus de valeur est surlignée.</summary>
    [JsonIgnore] public List<OddTile> Tiles => _tiles ??= BuildTiles();
    private List<OddTile>? _tiles;
    [JsonIgnore] public bool HasOdds => OddHome != null;

    private List<OddTile> BuildTiles()
    {
        var sel = Selections();
        var best = sel.Take(3).Where(s => s.Edge > 0).OrderByDescending(s => s.Edge).FirstOrDefault();
        return sel.Take(3).Select((s, i) => new OddTile(i == 0 ? "1" : i == 1 ? "N" : "2", s.Model, s.Odd, s == best && !IsOver)).ToList();
    }

    // ------------------------------------------------------------------ stratégies et indice
    [JsonIgnore] public bool HasSignals => SignalCount > 0;
    [JsonIgnore] public string SignalsText => SignalCount == 0 ? "" : SignalCount == 1 ? "1 stratégie" : $"{SignalCount} stratégies";

    /// <summary>Meilleur indice du match (calculé par l'écran après lecture de la forme des équipes).</summary>
    [JsonIgnore] public BetIndex? BestIndex { get; set; }
    [JsonIgnore] public bool HasIndex => BestIndex != null;
    [JsonIgnore] public string IndexText => BestIndex is { } x ? $"Indice {x.ScoreText} · {BetKeys.Short(x.Key)}" : "";
    [JsonIgnore] public Color IndexColor => BestIndex?.ScoreColor ?? Palette.TextMuted;
}

/// <summary>Une cote de la carte d'un match (1, N ou 2).</summary>
public sealed class OddTile
{
    public OddTile(string label, double? probability, double? odd, bool isPick)
    {
        Label = label;
        Probability = probability;
        Odd = odd;
        IsPick = isPick;
    }

    public string Label { get; }
    public double? Probability { get; }
    public double? Odd { get; }
    public bool IsPick { get; }

    public string TopText => $"{Label} · {Fmt.Pct(Probability)}";
    public string OddText => Fmt.Odd(Odd);
    public Color Background => IsPick ? Palette.AccentSoft : Palette.CardAlt;
    public Brush Stroke => new SolidColorBrush(IsPick ? Palette.Accent : Palette.Line);
    public Color Foreground => IsPick ? Palette.Accent : Palette.Text;
}

/// <summary>Un groupe de matchs de la journée (« Terminés », « Joués », « À venir »).</summary>
public sealed class MatchSection : List<MatchItem>
{
    public string Title { get; }
    public string Subtitle { get; }
    public Color DotColor { get; }

    public MatchSection(string title, Color dotColor, IEnumerable<MatchItem> items) : base(items)
    {
        Title = title;
        DotColor = dotColor;
        Subtitle = Count == 1 ? "1 match" : $"{Count} matchs";
    }
}

/// <summary>Un jour du bandeau de dates de l'onglet Matchs.</summary>
public sealed class DayChip : Juniro.App.ViewModels.ObservableObject
{
    private bool _isSelected;

    public DayChip(DateTime date, ICommand command)
    {
        Date = date.Date;
        Command = command;
        var today = DateTime.Today;
        DayName = Date == today ? "Auj." : Date.ToString("ddd", Fmt.Fr).TrimEnd('.');
        DayName = char.ToUpper(DayName[0], Fmt.Fr) + DayName[1..];
        DayNumber = Date.Day.ToString();
    }

    public DateTime Date { get; }
    public string DayName { get; }
    public string DayNumber { get; }
    public ICommand Command { get; }

    public bool IsSelected
    {
        get => _isSelected;
        set
        {
            if (!SetProperty(ref _isSelected, value)) return;
            OnPropertyChanged(nameof(Background));
            OnPropertyChanged(nameof(Foreground));
            OnPropertyChanged(nameof(Stroke));
        }
    }

    public Color Background => IsSelected ? Palette.Accent : Palette.Card;
    public Color Foreground => IsSelected ? Palette.OnAccent : Palette.TextMuted;
    public Brush Stroke => new SolidColorBrush(IsSelected ? Palette.Accent : Palette.Line);
}

/// <summary>Règlement d'un pari d'après le score (même règle que la fonction Supabase settle_bet).</summary>
public static class Settle
{
    public static string Result(string key, int? hg, int? ag)
    {
        if (hg is not int h || ag is not int a) return "PENDING";
        return key switch
        {
            "1X2_HOME" => h > a ? "WIN" : "LOSE",
            "1X2_DRAW" => h == a ? "WIN" : "LOSE",
            "1X2_AWAY" => h < a ? "WIN" : "LOSE",
            "DNB_HOME" => h == a ? "PUSH" : h > a ? "WIN" : "LOSE",
            "DNB_AWAY" => h == a ? "PUSH" : h < a ? "WIN" : "LOSE",
            "DC_1X" => h >= a ? "WIN" : "LOSE",
            "DC_X2" => h <= a ? "WIN" : "LOSE",
            "DC_12" => h != a ? "WIN" : "LOSE",
            "BTTS_YES" => h > 0 && a > 0 ? "WIN" : "LOSE",
            "BTTS_NO" => h == 0 || a == 0 ? "WIN" : "LOSE",
            "OU15_OVER" => h + a > 1 ? "WIN" : "LOSE",
            "OU15_UNDER" => h + a < 2 ? "WIN" : "LOSE",
            "OU25_OVER" => h + a > 2 ? "WIN" : "LOSE",
            "OU25_UNDER" => h + a < 3 ? "WIN" : "LOSE",
            "OU35_OVER" => h + a > 3 ? "WIN" : "LOSE",
            "OU35_UNDER" => h + a < 4 ? "WIN" : "LOSE",
            _ => "PENDING"
        };
    }
}
