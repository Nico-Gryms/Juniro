namespace Juniro.App.Services;

/// <summary>
/// Les paris possibles, avec les mêmes codes que le moteur et Supabase (bet_key) :
/// 1X2_HOME, DNB_AWAY, OU25_OVER, BTTS_YES...
/// </summary>
public static class BetKeys
{
    public static readonly string[] All =
    {
        "1X2_HOME", "1X2_DRAW", "1X2_AWAY", "DNB_HOME", "DNB_AWAY", "DC_1X", "DC_X2", "DC_12",
        "BTTS_YES", "BTTS_NO", "OU15_OVER", "OU15_UNDER", "OU25_OVER", "OU25_UNDER", "OU35_OVER", "OU35_UNDER"
    };

    /// <summary>Libellé court : « 1 », « N », « 2 », « +2,5 », « BTTS oui »...</summary>
    public static string Short(string? key) => key switch
    {
        "1X2_HOME" => "1",
        "1X2_DRAW" => "N",
        "1X2_AWAY" => "2",
        "DNB_HOME" => "1 rem. si nul",
        "DNB_AWAY" => "2 rem. si nul",
        "DC_1X" => "1N",
        "DC_X2" => "N2",
        "DC_12" => "12",
        "BTTS_YES" => "BTTS oui",
        "BTTS_NO" => "BTTS non",
        "OU15_OVER" => "+1,5",
        "OU15_UNDER" => "−1,5",
        "OU25_OVER" => "+2,5",
        "OU25_UNDER" => "−2,5",
        "OU35_OVER" => "+3,5",
        "OU35_UNDER" => "−3,5",
        _ => key ?? "?"
    };

    /// <summary>
    /// Libellé court dans le tableau des cotes, où le marché est déjà le titre :
    /// « Lens », « Nul », « Lens ou nul », « Oui », « Plus de 2,5 »...
    /// </summary>
    public static string InMarket(string? key, string? home = null, string? away = null)
    {
        string h = string.IsNullOrWhiteSpace(home) ? "Domicile" : home;
        string a = string.IsNullOrWhiteSpace(away) ? "Extérieur" : away;
        return key switch
        {
            "1X2_HOME" or "DNB_HOME" => h,
            "1X2_DRAW" => "Nul",
            "1X2_AWAY" or "DNB_AWAY" => a,
            "DC_1X" => $"{h} ou nul",
            "DC_X2" => $"Nul ou {a}",
            "DC_12" => "Pas de nul",
            "BTTS_YES" => "Oui",
            "BTTS_NO" => "Non",
            "OU15_OVER" => "Plus de 1,5",
            "OU15_UNDER" => "Moins de 1,5",
            "OU25_OVER" => "Plus de 2,5",
            "OU25_UNDER" => "Moins de 2,5",
            "OU35_OVER" => "Plus de 3,5",
            "OU35_UNDER" => "Moins de 3,5",
            _ => key ?? "?"
        };
    }

    /// <summary>Libellé complet avec le nom des équipes.</summary>
    public static string Label(string? key, string? home = null, string? away = null)
    {
        string h = string.IsNullOrWhiteSpace(home) ? "domicile" : home;
        string a = string.IsNullOrWhiteSpace(away) ? "extérieur" : away;
        return key switch
        {
            "1X2_HOME" => $"Victoire {h}",
            "1X2_DRAW" => "Match nul",
            "1X2_AWAY" => $"Victoire {a}",
            "DNB_HOME" => $"{h} (remboursé si nul)",
            "DNB_AWAY" => $"{a} (remboursé si nul)",
            "DC_1X" => $"{h} ou nul",
            "DC_X2" => $"Nul ou {a}",
            "DC_12" => "Pas de match nul",
            "BTTS_YES" => "Les deux équipes marquent",
            "BTTS_NO" => "Au moins une équipe ne marque pas",
            "OU15_OVER" => "Plus de 1,5 but",
            "OU15_UNDER" => "Moins de 1,5 but",
            "OU25_OVER" => "Plus de 2,5 buts",
            "OU25_UNDER" => "Moins de 2,5 buts",
            "OU35_OVER" => "Plus de 3,5 buts",
            "OU35_UNDER" => "Moins de 3,5 buts",
            _ => key ?? "?"
        };
    }

    /// <summary>Code bet_key à partir du marché et de la sélection d'un pari fictif (1X2 + HOME).</summary>
    public static string FromMarket(string? market, string? selection) => $"{market}_{selection}";

    /// <summary>Nom lisible d'une famille de stratégies du moteur.</summary>
    public static string Strategy(string? name) => name switch
    {
        "combo_plus25" => "Combo +2,5 buts",
        "combo_btts" => "Combo BTTS",
        "combo_moins25" => "Combo −2,5 buts",
        "value_1x2" => "Valeur 1X2",
        "value_buts" => "Valeur buts (BTTS, ±2,5)",
        "value_1x2_kelly" => "Valeur 1X2 (Kelly)",
        "papier_seriea_ext_favori" => "Serie A · favori à l'extérieur",
        "papier_laliga_dom_favori" => "LaLiga · favori à domicile",
        "papier_ligue1_value_kelly" => "Ligue 1 · valeur Kelly",
        null or "" => "Perso",
        _ => name
    };
}
