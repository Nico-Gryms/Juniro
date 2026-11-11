namespace Juniro.App.Services;

/// <summary>
/// Couleurs utilisées dans le code (verdicts, résultats, zones de classement, courbes).
/// Mêmes valeurs que Resources/Styles/Colors.xaml : modifier les deux ensemble.
/// </summary>
public static class Palette
{
    public static readonly Color Bg = Color.FromArgb("#0A0E17");
    public static readonly Color Surface = Color.FromArgb("#0F1522");
    public static readonly Color Card = Color.FromArgb("#141C2B");
    public static readonly Color CardAlt = Color.FromArgb("#1A2436");
    public static readonly Color Line = Color.FromArgb("#243049");
    public static readonly Color LineSoft = Color.FromArgb("#1E2940");
    public static readonly Color SegmentOn = Color.FromArgb("#243049");
    public static readonly Color Text = Color.FromArgb("#EEF2F8");
    public static readonly Color TextSoft = Color.FromArgb("#C9D2E1");
    public static readonly Color TextMuted = Color.FromArgb("#9AA6BC");
    public static readonly Color TextDim = Color.FromArgb("#7D8AA2");
    public static readonly Color Accent = Color.FromArgb("#C8F04A");
    public static readonly Color AccentSoft = Color.FromArgb("#2A352F");
    public static readonly Color OnAccent = Color.FromArgb("#0A0E17");
    public static readonly Color Win = Color.FromArgb("#3DDC97");
    public static readonly Color WinSoft = Color.FromArgb("#1A373A");
    public static readonly Color Lose = Color.FromArgb("#FF6B6B");
    public static readonly Color LoseSoft = Color.FromArgb("#352734");
    public static readonly Color Even = Color.FromArgb("#FFC24A");
    public static readonly Color Home = Color.FromArgb("#5B9DFF");
    public static readonly Color Draw = Color.FromArgb("#8C97AB");
    public static readonly Color Away = Color.FromArgb("#FF9F43");

    // Zones de classement
    public static readonly Color ZoneChampions = Color.FromArgb("#5B9DFF");
    public static readonly Color ZoneChampionsQualif = Color.FromArgb("#9CC2FF");
    public static readonly Color ZoneEuropa = Color.FromArgb("#FF9F43");
    public static readonly Color ZoneConference = Color.FromArgb("#3DDC97");
    public static readonly Color ZonePromotion = Color.FromArgb("#C8F04A");
    public static readonly Color ZonePlayoff = Color.FromArgb("#FFC24A");
    public static readonly Color ZoneRelegation = Color.FromArgb("#FF6B6B");

    /// <summary>Couleur d'un verdict de stratégie (Gagnante / Équilibrée / Perdante / autre).</summary>
    public static Color ForVerdict(string? verdict) => verdict switch
    {
        "Gagnante" => Win,
        "Perdante" => Lose,
        "Équilibrée" => Even,
        _ => TextMuted
    };

    /// <summary>Couleur d'un résultat de pari (WIN / LOSE / PUSH / VOID / PENDING).</summary>
    public static Color ForResult(string? result) => result switch
    {
        "WIN" => Win,
        "LOSE" => Lose,
        "PUSH" or "VOID" => Even,
        _ => TextMuted
    };

    /// <summary>Vert si positif, rouge si négatif, gris si nul ou absent.</summary>
    public static Color ForSign(double? value) =>
        value is null || Math.Abs(value.Value) < 1e-9 ? TextMuted : value > 0 ? Win : Lose;

    /// <summary>Couleur d'un indice (part de critères favorables) : accent, orange ou rouge.</summary>
    public static Color ForScore(double ratio) => ratio >= 0.7 ? Accent : ratio >= 0.5 ? Even : Lose;

    /// <summary>Verdict d'un ROI (%) avec les seuils de AppConfig.</summary>
    public static string VerdictOf(double? roiPct, int bets, int minBets = 20)
    {
        if (roiPct is null || bets < minBets) return "Trop tôt";
        if (roiPct >= AppConfig.WinningRoi) return "Gagnante";
        if (roiPct <= AppConfig.LosingRoi) return "Perdante";
        return "Équilibrée";
    }
}
