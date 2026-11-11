using Juniro.App.Services;

namespace Juniro.App.Models;

/// <summary>Un critère évalué pour un pari : favorable (✓) ou défavorable (✕).</summary>
public sealed class Criterion
{
    public Criterion(string title, string detail, bool ok)
    {
        Title = title;
        Detail = detail;
        Ok = ok;
    }

    public string Title { get; }
    public string Detail { get; }
    public bool Ok { get; }
    public bool HasDetail => !string.IsNullOrEmpty(Detail);

    public string Icon => Ok ? "✓" : "✕";
    public Color IconColor => Ok ? Palette.Win : Palette.Lose;
    public Color IconBackground => Ok ? Palette.WinSoft : Palette.LoseSoft;
}

/// <summary>
/// L'indice d'un pari : nombre de critères favorables sur les critères évalués
/// (modèle contre marché, valeur, forme, buts, xG, tendances, Elo...). Voir Services/IndexEngine.cs.
/// </summary>
public sealed class BetIndex
{
    public string Key { get; init; } = "";
    public string Label { get; init; } = "";
    public double? Model { get; init; }
    public double? Odd { get; init; }
    public double? Edge { get; init; }
    public List<Criterion> Criteria { get; init; } = new();

    public int Score => Criteria.Count(c => c.Ok);
    public int Total => Criteria.Count;
    public double Ratio => Total == 0 ? 0 : (double)Score / Total;

    public string ScoreText => $"{Score}/{Total}";
    public Color ScoreColor => Palette.ForScore(Ratio);
    public string OddText => Odd is null ? "pas de cote" : $"@ {Fmt.Odd(Odd)}";
    public string EdgeText => Edge is null ? "–" : Fmt.Signed(Edge * 100, 1, " %");
    public bool IsValue => Edge > 0;
    public Color EdgeBackground => IsValue ? Palette.Accent : Colors.Transparent;
    public Color EdgeForeground => IsValue ? Palette.OnAccent : Palette.TextDim;
    public string SummaryText =>
        $"Modèle {Fmt.Pct(Model)} · cote {(Odd is null ? "–" : Fmt.Odd(Odd))} · juste {Fmt.Odd(Model is > 0 ? 1 / Model : null)} · valeur {EdgeText}";
}

/// <summary>Un marché de la fiche match (« Résultat 1 N 2 », « Buts ± 2,5 ») et ses sélections.</summary>
public sealed class MarketGroup
{
    public MarketGroup(string name, IEnumerable<SelectionRow> rows)
    {
        Name = name;
        Rows = rows.ToList();
    }

    public string Name { get; }
    public List<SelectionRow> Rows { get; }
}

/// <summary>Une ligne « domicile / libellé / extérieur » avec une barre de comparaison.</summary>
public sealed class CompareRow
{
    public CompareRow(string label, string home, string away, double? homeValue = null, double? awayValue = null, bool higherIsBetter = true)
    {
        Label = label;
        Home = home;
        Away = away;
        double h = Math.Max(homeValue ?? 0, 0), a = Math.Max(awayValue ?? 0, 0);
        HasBar = homeValue != null && awayValue != null && h + a > 0;
        HomeShare = HasBar ? h / (h + a) : 0.5;
        AwayShare = 1 - HomeShare;
        bool homeBetter = higherIsBetter ? h > a : h < a;
        bool awayBetter = higherIsBetter ? a > h : a < h;
        HomeColor = HasBar && homeBetter ? Palette.Text : Palette.TextSoft;
        AwayColor = HasBar && awayBetter ? Palette.Text : Palette.TextSoft;
        HomeFont = HasBar && homeBetter ? "InterBold" : "Inter";
        AwayFont = HasBar && awayBetter ? "InterBold" : "Inter";
    }

    public string Label { get; }
    public string Home { get; }
    public string Away { get; }
    public bool HasBar { get; }
    public double HomeShare { get; }
    public double AwayShare { get; }
    public Color HomeColor { get; }
    public Color AwayColor { get; }
    public string HomeFont { get; }
    public string AwayFont { get; }
}

/// <summary>Un match récent d'une équipe (onglet Forme de la fiche match).</summary>
public sealed class FormLine
{
    public FormLine(MatchItem m, string team)
    {
        bool home = m.HomeTeam == team;
        int gf = (home ? m.HomeGoals : m.AwayGoals) ?? 0;
        int ga = (home ? m.AwayGoals : m.HomeGoals) ?? 0;
        Opponent = home ? m.AwayTeam : m.HomeTeam;
        Venue = home ? "Dom." : "Ext.";
        Score = $"{gf} – {ga}";
        DateText = m.KickoffUtc.ToLocalTime().ToString("dd/MM", Fmt.Fr);
        League = m.League;
        Letter = gf > ga ? "V" : gf == ga ? "N" : "D";
        Color = gf > ga ? Palette.Win : gf == ga ? Palette.Draw : Palette.Lose;
        double? xgf = home ? m.HomeXg : m.AwayXg, xga = home ? m.AwayXg : m.HomeXg;
        XgText = xgf is null ? "" : $"xG {Fmt.Num(xgf, 1)} – {Fmt.Num(xga, 1)}";
    }

    public string Opponent { get; }
    public string Venue { get; }
    public string Score { get; }
    public string DateText { get; }
    public string League { get; }
    public string Letter { get; }
    public Color Color { get; }
    public string XgText { get; }
    public string SubText => string.IsNullOrEmpty(XgText) ? $"{DateText} · {Venue}" : $"{DateText} · {Venue} · {XgText}";
}
