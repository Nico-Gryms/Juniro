using System.Text.Json.Serialization;
using Juniro.App.Services;

namespace Juniro.App.Models;

/// <summary>Une ligue suivie et sa saison en cours (vue Supabase v_leagues).</summary>
public sealed class LeagueInfo
{
    public string League { get; set; } = "";
    public string? Country { get; set; }
    public int? Tier { get; set; }
    public string? Season { get; set; }
}

/// <summary>Moyennes de la saison en cours d'une ligue (vue Supabase v_league_stats).</summary>
public sealed class LeagueStats
{
    public string League { get; set; } = "";
    public string? Season { get; set; }
    public int Matches { get; set; }
    public double? AvgGoals { get; set; }
    [JsonPropertyName("pct_over25")] public double? PctOver25 { get; set; }
    public double? PctBtts { get; set; }
    public double? PctHomeWin { get; set; }
}

/// <summary>Une ligne de classement (fonction Supabase league_table).</summary>
public sealed class StandingRow
{
    public int Rank { get; set; }
    public string Team { get; set; } = "";
    public int Played { get; set; }
    public int Won { get; set; }
    public int Drawn { get; set; }
    public int Lost { get; set; }
    public int Gf { get; set; }
    public int Ga { get; set; }
    public int Gd { get; set; }
    public int Pts { get; set; }
    public string Form { get; set; } = "";

    // ---- affichage (zone et surlignage remplis par l'écran)
    [JsonIgnore] public Color? ZoneColor { get; set; }
    [JsonIgnore] public bool IsHighlighted { get; set; }
    [JsonIgnore] public Color RankBackground => ZoneColor ?? Palette.CardAlt;
    [JsonIgnore] public Color RankForeground => ZoneColor != null ? Palette.OnAccent : Palette.TextSoft;
    [JsonIgnore] public Color RowBackground => IsHighlighted ? Palette.AccentSoft : Colors.Transparent;
    [JsonIgnore] public string GdText => Gd > 0 ? $"+{Gd}" : Gd < 0 ? $"−{-Gd}" : "0";
    [JsonIgnore] public string RecordText => $"{Won}-{Drawn}-{Lost}";
    [JsonIgnore] public List<FormDot> FormDots => Form.Select(c => new FormDot(c)).ToList();
}

/// <summary>Une pastille de forme : V (victoire), N (nul), D (défaite).</summary>
public sealed class FormDot
{
    public FormDot(char letter)
    {
        Letter = letter.ToString();
        Color = letter switch { 'V' => Palette.Win, 'N' => Palette.Draw, _ => Palette.Lose };
    }

    public string Letter { get; }
    public Color Color { get; }
}

/// <summary>Une entrée de la légende des zones (Ligue des champions, relégation...).</summary>
public sealed record ZoneLegend(string Name, Color Color);

/// <summary>Dernier Elo connu d'une équipe (vue Supabase v_elo).</summary>
public sealed class EloRow
{
    public string Team { get; set; } = "";
    public string League { get; set; } = "";
    public string? Country { get; set; }
    public int? Tier { get; set; }
    public double Elo { get; set; }
    public DateTimeOffset? EloDate { get; set; }
    public double[]? History { get; set; }
    [JsonPropertyName("delta10")] public double? Delta10 { get; set; }
    public int RankAll { get; set; }

    // ---- affichage
    [JsonIgnore] public int Position { get; set; }
    [JsonIgnore] public string PositionText => Position.ToString();
    [JsonIgnore] public string EloText => Fmt.Num(Elo, 0);
    [JsonIgnore] public string SubText => $"{League} · {Country}";
    [JsonIgnore] public string DeltaText => Delta10 is null ? "–" : (Delta10 >= 0 ? "▲ +" : "▼ −") + Math.Abs(Delta10.Value).ToString("F0", Fmt.Fr);
    [JsonIgnore] public Color DeltaColor => Palette.ForSign(Delta10);
    [JsonIgnore] public IList<double> Points => History ?? Array.Empty<double>();
}
