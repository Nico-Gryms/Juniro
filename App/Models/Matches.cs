using System.Text.Json.Serialization;
using Juniro.App.Services;

namespace Juniro.App.Models;

// Les noms de propriétés sont convertis automatiquement en snake_case (MatchId -> match_id).
// Les propriétés contenant des chiffres portent leur nom exact ([JsonPropertyName]).

/// <summary>Un pari possible sur un match : probabilités, cote juste, cote, valeur.</summary>
public sealed class SelectionRow
{
    public string Key { get; init; } = "";
    public string Label { get; init; } = "";
    public double? Model { get; init; }
    public double? Market { get; init; }
    public double? Odd { get; init; }

    public double? FairOdd => Model is > 0 ? 1 / Model : null;
    public double? Edge => Model is not null && Odd is not null ? Model * Odd - 1 : null;

    public string ModelText => Fmt.Pct(Model);
    public string MarketText => Fmt.Pct(Market);
    public string FairOddText => Fmt.Odd(FairOdd);
    public string OddText => Fmt.Odd(Odd);
    public string EdgeText => Edge is null ? "–" : Fmt.Signed(Edge * 100, 0, " %");
    public Color EdgeColor => Edge is null ? Palette.TextDim : Edge > 0 ? Palette.Win : Palette.TextMuted;
    public bool IsValue => Edge > 0;
    /// <summary>Libellé court pour le tableau des cotes (le marché est déjà le titre).</summary>
    public string ShortLabel { get; init; } = "";
    // Mise en forme de la ligne du tableau des cotes : fond et pastille de valeur
    public Color RowBackground => IsValue ? Palette.AccentSoft : Colors.Transparent;
    public Color EdgeBackground => IsValue ? Palette.Accent : Colors.Transparent;
    public Color EdgeForeground => IsValue ? Palette.OnAccent : Edge is null ? Palette.TextDim : Palette.TextMuted;
    public string PickerText => Odd is null ? Label : $"{Label}  @ {Fmt.Odd(Odd)}";

    /// <summary>Texte affiché dans la liste déroulante de saisie d'un pari.</summary>
    public override string ToString() => PickerText;

    /// <summary>
    /// Les 16 paris d'un match. Probabilité du marché : celle fournie par le moteur (1X2, BTTS,
    /// +2,5), sinon déduite des deux cotes opposées, marge retirée (±1,5, ±3,5, remboursé si nul).
    /// Les cotes de double chance, remboursé si nul, ±1,5 et ±3,5 sont facultatives.
    /// </summary>
    public static List<SelectionRow> Build(string home, string away,
        double? pH, double? pD, double? pA, double? pBtts, double? pO15, double? pO25, double? pO35,
        double? mH, double? mD, double? mA, double? mBtts, double? mO25,
        double? oH, double? oD, double? oA, double? oBy, double? oBn, double? oO25, double? oU25,
        double? oDc1X = null, double? oDcX2 = null, double? oDnbH = null, double? oDnbA = null,
        double? oO15 = null, double? oU15 = null, double? oO35 = null, double? oU35 = null)
    {
        static double? Not(double? p) => p is null ? null : 1 - p;
        static double? Dnb(double? win, double? draw) => win is null || draw is null || draw >= 1 ? null : win / (1 - draw);
        // Probabilité du marché d'après deux cotes opposées (marge retirée)
        static double? Pair(double? odd, double? opposite) =>
            odd is > 1 && opposite is > 1 ? (1 / odd) / (1 / odd + 1 / opposite) : null;
        double? mDnbH = Dnb(mH, mD) ?? Pair(oDnbH, oDnbA);
        double? mO15 = Pair(oO15, oU15), mO35 = Pair(oO35, oU35);
        var rows = new List<SelectionRow>
        {
            new() { Key = "1X2_HOME", Label = BetKeys.Label("1X2_HOME", home, away), Model = pH, Market = mH, Odd = oH },
            new() { Key = "1X2_DRAW", Label = BetKeys.Label("1X2_DRAW", home, away), Model = pD, Market = mD, Odd = oD },
            new() { Key = "1X2_AWAY", Label = BetKeys.Label("1X2_AWAY", home, away), Model = pA, Market = mA, Odd = oA },
            new() { Key = "DC_1X", Label = BetKeys.Label("DC_1X", home, away), Model = pH + pD, Market = mH + mD, Odd = oDc1X },
            new() { Key = "DC_X2", Label = BetKeys.Label("DC_X2", home, away), Model = pD + pA, Market = mD + mA, Odd = oDcX2 },
            new() { Key = "DC_12", Label = BetKeys.Label("DC_12", home, away), Model = pH + pA, Market = mH + mA },
            // Remboursé si nul : probabilité de gagner une fois le nul écarté
            new() { Key = "DNB_HOME", Label = BetKeys.Label("DNB_HOME", home, away), Model = Dnb(pH, pD), Market = mDnbH, Odd = oDnbH },
            new() { Key = "DNB_AWAY", Label = BetKeys.Label("DNB_AWAY", home, away), Model = Dnb(pA, pD), Market = Not(mDnbH), Odd = oDnbA },
            new() { Key = "BTTS_YES", Label = BetKeys.Label("BTTS_YES"), Model = pBtts, Market = mBtts, Odd = oBy },
            new() { Key = "BTTS_NO", Label = BetKeys.Label("BTTS_NO"), Model = Not(pBtts), Market = Not(mBtts), Odd = oBn },
            new() { Key = "OU15_OVER", Label = BetKeys.Label("OU15_OVER"), Model = pO15, Market = mO15, Odd = oO15 },
            new() { Key = "OU15_UNDER", Label = BetKeys.Label("OU15_UNDER"), Model = Not(pO15), Market = Not(mO15), Odd = oU15 },
            new() { Key = "OU25_OVER", Label = BetKeys.Label("OU25_OVER"), Model = pO25, Market = mO25, Odd = oO25 },
            new() { Key = "OU25_UNDER", Label = BetKeys.Label("OU25_UNDER"), Model = Not(pO25), Market = Not(mO25), Odd = oU25 },
            new() { Key = "OU35_OVER", Label = BetKeys.Label("OU35_OVER"), Model = pO35, Market = mO35, Odd = oO35 },
            new() { Key = "OU35_UNDER", Label = BetKeys.Label("OU35_UNDER"), Model = Not(pO35), Market = Not(mO35), Odd = oU35 },
        };
        var named = rows.Select(r => new SelectionRow
        {
            Key = r.Key, Label = r.Label, ShortLabel = BetKeys.InMarket(r.Key, home, away),
            Model = r.Model, Market = r.Market, Odd = r.Odd
        }).ToList();
        return named;
    }
}

/// <summary>Un match (table Supabase matches).</summary>
public sealed class MatchRow
{
    public long MatchId { get; set; }
    public string League { get; set; } = "";
    public string? Season { get; set; }
    public DateTimeOffset KickoffUtc { get; set; }
    public string Status { get; set; } = "";
    public string HomeTeam { get; set; } = "";
    public string AwayTeam { get; set; } = "";
    public int? HomeGoals { get; set; }
    public int? AwayGoals { get; set; }
    public double? HomeXg { get; set; }
    public double? AwayXg { get; set; }
    public int? HomeShots { get; set; }
    public int? AwayShots { get; set; }
    public int? HomeSot { get; set; }
    public int? AwaySot { get; set; }
    public int? HomeCorners { get; set; }
    public int? AwayCorners { get; set; }
    public double? HomePossession { get; set; }
    public double? AwayPossession { get; set; }

    [JsonIgnore] public bool IsFinished => Status == "Finished" && HomeGoals != null && AwayGoals != null;
    [JsonIgnore] public string ScoreText => IsFinished ? $"{HomeGoals} – {AwayGoals}" : "vs";
}

/// <summary>Prédiction d'un match (table Supabase predictions).</summary>
public sealed class PredictionRow
{
    public long MatchId { get; set; }
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
    public double? OddBttsYes { get; set; }
    public double? OddBttsNo { get; set; }
    [JsonPropertyName("odd_over25")] public double? OddOver25 { get; set; }
    [JsonPropertyName("odd_under25")] public double? OddUnder25 { get; set; }
    [JsonPropertyName("odd_dc_1x")] public double? OddDc1X { get; set; }
    [JsonPropertyName("odd_dc_x2")] public double? OddDcX2 { get; set; }
    public double? OddDnbHome { get; set; }
    public double? OddDnbAway { get; set; }
    [JsonPropertyName("odd_over15")] public double? OddOver15 { get; set; }
    [JsonPropertyName("odd_under15")] public double? OddUnder15 { get; set; }
    [JsonPropertyName("odd_over35")] public double? OddOver35 { get; set; }
    [JsonPropertyName("odd_under35")] public double? OddUnder35 { get; set; }
    public string? OddsLabel { get; set; }

    public List<SelectionRow> Selections(string home, string away) => SelectionRow.Build(home, away,
        PHome, PDraw, PAway, PBttsYes, POver15, POver25, POver35,
        MktPHome, MktPDraw, MktPAway, MktPBttsYes, MktPOver25,
        OddHome, OddDraw, OddAway, OddBttsYes, OddBttsNo, OddOver25, OddUnder25,
        OddDc1X, OddDcX2, OddDnbHome, OddDnbAway, OddOver15, OddUnder15, OddOver35, OddUnder35);
}

/// <summary>Forme des deux équipes avant le match (table Supabase match_stats).</summary>
public sealed class MatchStats
{
    public long MatchId { get; set; }
    public double? HomeElo { get; set; }
    public double? AwayElo { get; set; }
    [JsonPropertyName("home_elo_trend5")] public double? HomeEloTrend5 { get; set; }
    [JsonPropertyName("away_elo_trend5")] public double? AwayEloTrend5 { get; set; }
    public int? HomeRank { get; set; }
    public int? AwayRank { get; set; }
    public int? HomePlayed { get; set; }
    public int? AwayPlayed { get; set; }
    public double? HomePpg { get; set; }
    public double? AwayPpg { get; set; }
    public double? HomeXppg { get; set; }
    public double? AwayXppg { get; set; }
    [JsonPropertyName("home_pts5")] public double? HomePts5 { get; set; }
    [JsonPropertyName("away_pts5")] public double? AwayPts5 { get; set; }
    [JsonPropertyName("home_gf5")] public double? HomeGf5 { get; set; }
    [JsonPropertyName("home_ga5")] public double? HomeGa5 { get; set; }
    [JsonPropertyName("away_gf5")] public double? AwayGf5 { get; set; }
    [JsonPropertyName("away_ga5")] public double? AwayGa5 { get; set; }
    [JsonPropertyName("home_xgf5")] public double? HomeXgf5 { get; set; }
    [JsonPropertyName("home_xga5")] public double? HomeXga5 { get; set; }
    [JsonPropertyName("away_xgf5")] public double? AwayXgf5 { get; set; }
    [JsonPropertyName("away_xga5")] public double? AwayXga5 { get; set; }
    [JsonPropertyName("home_btts10")] public double? HomeBtts10 { get; set; }
    [JsonPropertyName("away_btts10")] public double? AwayBtts10 { get; set; }
    [JsonPropertyName("home_over25_10")] public double? HomeOver2510 { get; set; }
    [JsonPropertyName("away_over25_10")] public double? AwayOver2510 { get; set; }
    public double? HomeRestDays { get; set; }
    public double? AwayRestDays { get; set; }

    /// <summary>Lignes « libellé / domicile / extérieur » pour la fiche du match.</summary>
    public List<StatRow> Rows() => new()
    {
        new("Classement", Rank(HomeRank), Rank(AwayRank)),
        new("Elo", Fmt.Num(HomeElo, 0), Fmt.Num(AwayElo, 0)),
        new("Évolution Elo (5 matchs)", Fmt.Signed(HomeEloTrend5, 0), Fmt.Signed(AwayEloTrend5, 0)),
        new("Points par match (saison)", Fmt.Num(HomePpg, 2), Fmt.Num(AwayPpg, 2)),
        new("Points attendus par match", Fmt.Num(HomeXppg, 2), Fmt.Num(AwayXppg, 2)),
        new("Points par match (5 derniers)", Fmt.Num(HomePts5, 2), Fmt.Num(AwayPts5, 2)),
        new("Buts marqués / encaissés (5)", $"{Fmt.Num(HomeGf5)} / {Fmt.Num(HomeGa5)}", $"{Fmt.Num(AwayGf5)} / {Fmt.Num(AwayGa5)}"),
        new("xG pour / contre (5)", $"{Fmt.Num(HomeXgf5)} / {Fmt.Num(HomeXga5)}", $"{Fmt.Num(AwayXgf5)} / {Fmt.Num(AwayXga5)}"),
        new("BTTS (10 derniers)", Fmt.Pct(HomeBtts10), Fmt.Pct(AwayBtts10)),
        new("+2,5 buts (10 derniers)", Fmt.Pct(HomeOver2510), Fmt.Pct(AwayOver2510)),
        new("Jours de repos", Fmt.Num(HomeRestDays, 0), Fmt.Num(AwayRestDays, 0)),
        new("Matchs joués (saison)", HomePlayed?.ToString() ?? "–", AwayPlayed?.ToString() ?? "–"),
    };

    private static string Rank(int? r) => r is null ? "–" : r == 1 ? "1er" : $"{r}e";
}

/// <summary>Une ligne de comparaison entre les deux équipes.</summary>
public sealed record StatRow(string Label, string Home, string Away);
