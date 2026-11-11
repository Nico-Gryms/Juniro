using Juniro.App.Models;

namespace Juniro.App.Services;

/// <summary>
/// Les ligues suivies : ordre d'affichage, couleur de repère et zones du classement
/// (qualifications européennes, montées, barrages, relégation). Les zones sont celles des
/// règlements 2026-2027 ; à ajuster ici si une ligue change son format.
/// </summary>
public static class Leagues
{
    /// <summary>Ordre d'affichage : premières divisions puis deuxièmes, par pays.</summary>
    public static readonly string[] Order =
    {
        "Ligue 1", "Premier League", "LaLiga", "Bundesliga", "Serie A",
        "Ligue 2", "Championship", "LaLiga2", "Bundesliga 2", "Serie B"
    };

    public static int SortKey(string? league)
    {
        int i = Array.IndexOf(Order, league ?? "");
        return i < 0 ? 99 : i;
    }

    /// <summary>Petite couleur de repère de chaque ligue (carré devant le nom).</summary>
    public static Color ColorOf(string? league) => league switch
    {
        "Ligue 1" or "Ligue 2" => Color.FromArgb("#5B9DFF"),
        "Premier League" or "Championship" => Color.FromArgb("#B48CFF"),
        "LaLiga" or "LaLiga2" => Color.FromArgb("#FF9F43"),
        "Bundesliga" or "Bundesliga 2" => Color.FromArgb("#FF6B6B"),
        "Serie A" or "Serie B" => Color.FromArgb("#3DDC97"),
        _ => Palette.TextMuted
    };

    private sealed record Zone(int From, int To, string Name, Color Color);

    private static readonly Zone[] FirstDivision20 =
    {
        new(1, 4, "Ligue des champions", Palette.ZoneChampions),
        new(5, 5, "Ligue Europa", Palette.ZoneEuropa),
        new(6, 6, "Conférence", Palette.ZoneConference),
        new(18, 20, "Relégation", Palette.ZoneRelegation),
    };

    private static Zone[] ZonesOf(string league, int teams) => league switch
    {
        "Ligue 1" => new Zone[]
        {
            new(1, 3, "Ligue des champions", Palette.ZoneChampions),
            new(4, 4, "Qualif. Ligue des champions", Palette.ZoneChampionsQualif),
            new(5, 5, "Ligue Europa", Palette.ZoneEuropa),
            new(6, 6, "Conférence", Palette.ZoneConference),
            new(teams - 2, teams - 2, "Barrage", Palette.ZonePlayoff),
            new(teams - 1, teams, "Relégation", Palette.ZoneRelegation),
        },
        "Bundesliga" => new Zone[]
        {
            new(1, 4, "Ligue des champions", Palette.ZoneChampions),
            new(5, 5, "Ligue Europa", Palette.ZoneEuropa),
            new(6, 6, "Conférence", Palette.ZoneConference),
            new(teams - 2, teams - 2, "Barrage", Palette.ZonePlayoff),
            new(teams - 1, teams, "Relégation", Palette.ZoneRelegation),
        },
        "Premier League" or "LaLiga" or "Serie A" => FirstDivision20
            .Select(z => z.Name == "Relégation" ? z with { From = teams - 2, To = teams } : z).ToArray(),
        "Ligue 2" => new Zone[]
        {
            new(1, 2, "Montée", Palette.ZonePromotion),
            new(3, 5, "Barrages de montée", Palette.ZonePlayoff),
            new(teams - 1, teams, "Relégation", Palette.ZoneRelegation),
        },
        "Bundesliga 2" => new Zone[]
        {
            new(1, 2, "Montée", Palette.ZonePromotion),
            new(3, 3, "Barrages de montée", Palette.ZonePlayoff),
            new(teams - 2, teams - 2, "Barrage", Palette.ZonePlayoff),
            new(teams - 1, teams, "Relégation", Palette.ZoneRelegation),
        },
        "Championship" or "LaLiga2" => new Zone[]
        {
            new(1, 2, "Montée", Palette.ZonePromotion),
            new(3, 6, "Barrages de montée", Palette.ZonePlayoff),
            new(teams - (league == "LaLiga2" ? 3 : 2), teams, "Relégation", Palette.ZoneRelegation),
        },
        "Serie B" => new Zone[]
        {
            new(1, 2, "Montée", Palette.ZonePromotion),
            new(3, 8, "Barrages de montée", Palette.ZonePlayoff),
            new(teams - 2, teams, "Relégation", Palette.ZoneRelegation),
        },
        _ => Array.Empty<Zone>()
    };

    /// <summary>Colore le rang de chaque ligne selon sa zone et renvoie la légende utilisée.</summary>
    public static List<ZoneLegend> ApplyZones(string league, IList<StandingRow> rows)
    {
        var zones = ZonesOf(league, rows.Count);
        foreach (var r in rows)
            r.ZoneColor = zones.FirstOrDefault(z => r.Rank >= z.From && r.Rank <= z.To)?.Color;
        return zones
            .GroupBy(z => z.Name)
            .Select(g => new ZoneLegend(g.Key, g.First().Color))
            .ToList();
    }
}
