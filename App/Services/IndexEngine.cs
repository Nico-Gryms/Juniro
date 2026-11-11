using Juniro.App.Models;

namespace Juniro.App.Services;

/// <summary>
/// Calcule l'indice de chaque pari d'un match : la liste des critères favorables (✓) ou
/// défavorables (✕) au pari, et le score « favorables / évalués ».
///
/// Critères (seuls ceux dont les données existent sont comptés) :
///   - pour tous les paris : modèle contre marché, valeur à la cote, probabilité ≥ 50 % ;
///   - buts (±1,5 / ±2,5 / ±3,5) : buts attendus du modèle, buts et xG d'après la forme,
///     % de matchs à +2,5 des deux équipes, BTTS des deux équipes, moyenne de la ligue ;
///   - les deux marquent : BTTS récents, attaques, défenses, buts attendus, BTTS de la ligue ;
///   - résultat (1N2, double chance, remboursé si nul) : Elo, classement, points par match,
///     forme, bilan xG, dynamique Elo, repos, modèle, avantage du terrain dans la ligue ;
///   - nul / pas de nul : écarts faibles (Elo, points, forme), probabilité du nul, match fermé.
/// Les seuils sont réglables ci-dessous.
/// </summary>
public static class IndexEngine
{
    // Seuils
    private const double OverRateLine15 = 0.35, OverRateLine25 = 0.50, OverRateLine35 = 0.65;
    private const double BttsRate = 0.50, HomeAttack = 1.2, AwayAttack = 1.0, Leaky = 1.0;
    private const double DrawEloGap = 60, DrawPpgGap = 0.35, DrawFormGap = 0.5, DrawModel = 0.27, ClosedGame = 2.5;
    private const double HomeWinLeague = 45;

    /// <summary>Indices de tous les paris du match, du plus favorable au moins favorable.</summary>
    public static List<BetIndex> Compute(MatchItem m, MatchStats? s, LeagueStats? l)
    {
        var list = new List<BetIndex>();
        foreach (var sel in m.Selections().Where(x => x.Model != null))
        {
            var c = new List<Criterion>();
            AddValue(c, sel);
            switch (sel.Key)
            {
                case "OU15_OVER": case "OU15_UNDER": AddGoals(c, m, s, l, 1.5, sel.Key.EndsWith("OVER")); break;
                case "OU25_OVER": case "OU25_UNDER": AddGoals(c, m, s, l, 2.5, sel.Key.EndsWith("OVER")); break;
                case "OU35_OVER": case "OU35_UNDER": AddGoals(c, m, s, l, 3.5, sel.Key.EndsWith("OVER")); break;
                case "BTTS_YES": case "BTTS_NO": AddBtts(c, m, s, l, sel.Key == "BTTS_YES"); break;
                case "1X2_HOME": case "DC_1X": case "DNB_HOME": AddSide(c, m, s, l, +1); break;
                case "1X2_AWAY": case "DC_X2": case "DNB_AWAY": AddSide(c, m, s, l, -1); break;
                case "1X2_DRAW": AddDraw(c, m, s, true); break;
                case "DC_12": AddDraw(c, m, s, false); break;
            }
            list.Add(new BetIndex
            {
                Key = sel.Key,
                Label = sel.Label,
                Model = sel.Model,
                Odd = sel.Odd,
                Edge = sel.Edge,
                Criteria = c
            });
        }
        return list
            .OrderByDescending(x => x.Ratio)
            .ThenByDescending(x => x.Score)
            .ThenByDescending(x => x.Edge ?? -1)
            .ToList();
    }

    /// <summary>Le pari au meilleur indice (en cas d'égalité, celui qui a le plus de valeur).</summary>
    public static BetIndex? Best(MatchItem m, MatchStats? s, LeagueStats? l) =>
        Compute(m, s, l).FirstOrDefault(x => x.Total >= 4);

    // ------------------------------------------------------------------ critères communs

    private static void AddValue(List<Criterion> c, SelectionRow sel)
    {
        if (sel.Model != null && sel.Market != null)
            c.Add(new($"Modèle {Fmt.Pct(sel.Model)} · marché {Fmt.Pct(sel.Market)}",
                sel.Model > sel.Market ? "Le modèle est plus optimiste que les bookmakers"
                                       : "Le modèle est moins optimiste que les bookmakers",
                sel.Model > sel.Market));
        if (sel.Edge != null)
            c.Add(new($"Valeur à la cote {Fmt.Odd(sel.Odd)} : {Fmt.Signed(sel.Edge * 100, 1, " %")}",
                $"Cote juste {Fmt.Odd(sel.FairOdd)} (1 / probabilité du modèle)",
                sel.Edge > 0));
        if (sel.Model != null)
            c.Add(new($"Probabilité du modèle : {Fmt.Pct(sel.Model)}",
                sel.Model >= 0.5 ? "Plus probable qu'improbable" : "Moins d'une chance sur deux",
                sel.Model >= 0.5));
    }

    // ------------------------------------------------------------------ buts

    private static void AddGoals(List<Criterion> c, MatchItem m, MatchStats? s, LeagueStats? l, double line, bool over)
    {
        string lineText = Fmt.Num(line);
        void Add(string title, string detail, bool favOver) => c.Add(new(title, detail, over ? favOver : !favOver));

        if (m.ExpTotalGoals is double exp)
            Add($"Buts attendus par le modèle : {Fmt.Num(exp, 2)}", $"Ligne {lineText}", exp > line);
        if (s == null) { AddLeagueGoals(c, l, line, over); return; }

        if (s.HomeGf5 is double hgf && s.AwayGa5 is double aga && s.AwayGf5 is double agf && s.HomeGa5 is double hga)
        {
            double est = (hgf + aga) / 2 + (agf + hga) / 2;
            Add($"Buts d'après la forme : {Fmt.Num(est, 2)}", "Attaque et défense des deux équipes (5 derniers matchs)", est > line);
        }
        if (s.HomeXgf5 is double hx && s.AwayXga5 is double axa && s.AwayXgf5 is double ax && s.HomeXga5 is double hxa)
        {
            double est = (hx + axa) / 2 + (ax + hxa) / 2;
            Add($"xG d'après la forme : {Fmt.Num(est, 2)}", "Occasions créées et concédées (5 derniers matchs)", est > line);
        }
        double rate = line < 2 ? OverRateLine15 : line < 3 ? OverRateLine25 : OverRateLine35;
        if (s.HomeOver2510 is double ho)
            Add($"{m.HomeTeam} : {Fmt.Pct(ho)} de matchs à +2,5", "10 derniers matchs", ho >= rate);
        if (s.AwayOver2510 is double ao)
            Add($"{m.AwayTeam} : {Fmt.Pct(ao)} de matchs à +2,5", "10 derniers matchs", ao >= rate);
        if (s.HomeBtts10 is double hb && s.AwayBtts10 is double ab)
            Add($"Les deux marquent : {Fmt.Pct(hb)} / {Fmt.Pct(ab)}", "Part des 10 derniers matchs où les deux équipes ont marqué",
                hb >= BttsRate && ab >= BttsRate);
        AddLeagueGoals(c, l, line, over);
    }

    private static void AddLeagueGoals(List<Criterion> c, LeagueStats? l, double line, bool over)
    {
        if (l?.AvgGoals is not double avg) return;
        bool favOver = line < 2 ? avg >= 2.3 : line < 3 ? (l.PctOver25 is double p ? p >= 50 : avg >= 2.7) : avg >= 3.2;
        c.Add(new($"{l.League} : {Fmt.Num(avg, 2)} buts par match", $"{Fmt.Pct100(l.PctOver25, 0)} de matchs à +2,5 cette saison",
            over ? favOver : !favOver));
    }

    // ------------------------------------------------------------------ les deux marquent

    private static void AddBtts(List<Criterion> c, MatchItem m, MatchStats? s, LeagueStats? l, bool yes)
    {
        void Add(string title, string detail, bool favYes) => c.Add(new(title, detail, yes ? favYes : !favYes));

        if (m.ExpHomeGoals is double eh && m.ExpAwayGoals is double ea)
            Add($"Buts attendus : {Fmt.Num(eh, 2)} – {Fmt.Num(ea, 2)}", "Chaque équipe à au moins 1 but attendu ?", eh >= 1 && ea >= 1);
        if (s != null)
        {
            if (s.HomeBtts10 is double hb)
                Add($"{m.HomeTeam} : les deux marquent dans {Fmt.Pct(hb)} des matchs", "10 derniers matchs", hb >= BttsRate);
            if (s.AwayBtts10 is double ab)
                Add($"{m.AwayTeam} : les deux marquent dans {Fmt.Pct(ab)} des matchs", "10 derniers matchs", ab >= BttsRate);
            if (s.HomeGf5 is double hgf)
                Add($"{m.HomeTeam} marque {Fmt.Num(hgf, 1)} but(s) par match", "5 derniers matchs", hgf >= HomeAttack);
            if (s.AwayGf5 is double agf)
                Add($"{m.AwayTeam} marque {Fmt.Num(agf, 1)} but(s) par match", "5 derniers matchs", agf >= AwayAttack);
            if (s.HomeGa5 is double hga)
                Add($"{m.HomeTeam} encaisse {Fmt.Num(hga, 1)} but(s) par match", "5 derniers matchs", hga >= Leaky);
            if (s.AwayGa5 is double aga)
                Add($"{m.AwayTeam} encaisse {Fmt.Num(aga, 1)} but(s) par match", "5 derniers matchs", aga >= Leaky);
        }
        if (l?.PctBtts is double lb)
            Add($"{l.League} : les deux marquent dans {Fmt.Pct100(lb, 0)} des matchs", "Saison en cours", lb >= 50);
    }

    // ------------------------------------------------------------------ résultat

    /// <summary>side = +1 : pari sur l'équipe à domicile ; −1 : sur l'équipe à l'extérieur.</summary>
    private static void AddSide(List<Criterion> c, MatchItem m, MatchStats? s, LeagueStats? l, int side)
    {
        string team = side > 0 ? m.HomeTeam : m.AwayTeam;
        // d > 0 favorise l'équipe à domicile
        void Add(string title, string detail, double d) => c.Add(new(title, detail, side > 0 ? d > 0 : d < 0));

        if (m.PHome is double ph && m.PAway is double pa)
            Add($"Modèle : {Fmt.Pct(ph)} contre {Fmt.Pct(pa)}", $"Victoire {m.HomeTeam} contre victoire {m.AwayTeam}", ph - pa);
        if (s == null) return;
        if (s.HomeElo is double he && s.AwayElo is double ae)
            Add($"Elo : {Fmt.Num(he, 0)} contre {Fmt.Num(ae, 0)}", $"Écart de {Fmt.Num(Math.Abs(he - ae), 0)} points pour {(he >= ae ? m.HomeTeam : m.AwayTeam)}", he - ae);
        if (s.HomeRank is int hr && s.AwayRank is int ar)
            Add($"Classement : {Rank(hr)} contre {Rank(ar)}", "Classement avant le match", ar - hr);
        if (s.HomePpg is double hp && s.AwayPpg is double ap)
            Add($"Points par match : {Fmt.Num(hp, 2)} contre {Fmt.Num(ap, 2)}", "Saison en cours", hp - ap);
        if (s.HomePts5 is double h5 && s.AwayPts5 is double a5)
            Add($"Forme : {Fmt.Num(h5, 2)} contre {Fmt.Num(a5, 2)} pts/match", "5 derniers matchs", h5 - a5);
        if (s.HomeXgf5 is double hx && s.HomeXga5 is double hxa && s.AwayXgf5 is double ax && s.AwayXga5 is double axa)
            Add($"Bilan xG : {Fmt.Signed(hx - hxa, 2)} contre {Fmt.Signed(ax - axa, 2)}", "xG créés − concédés par match (5 derniers)", (hx - hxa) - (ax - axa));
        if (s.HomeEloTrend5 is double ht && s.AwayEloTrend5 is double at)
            Add($"Dynamique Elo : {Fmt.Signed(ht, 0)} contre {Fmt.Signed(at, 0)}", "Évolution sur les 5 derniers matchs", ht - at);
        if (s.HomeRestDays is double hr2 && s.AwayRestDays is double ar2 && Math.Abs(hr2 - ar2) >= 1)
        {
            bool rested = side > 0 ? hr2 > ar2 : ar2 > hr2;
            Add($"Repos : {Fmt.Num(hr2, 0)} j contre {Fmt.Num(ar2, 0)} j", $"{team} {(rested ? "plus" : "moins")} reposé", hr2 - ar2);
        }
        if (l?.PctHomeWin is double hw)
            Add($"{l.League} : {Fmt.Pct100(hw, 0)} de victoires à domicile", "Avantage du terrain cette saison", hw - HomeWinLeague);
    }

    private static void AddDraw(List<Criterion> c, MatchItem m, MatchStats? s, bool draw)
    {
        void Add(string title, string detail, bool favDraw) => c.Add(new(title, detail, draw ? favDraw : !favDraw));

        if (m.PDraw is double pd)
            Add($"Modèle : nul à {Fmt.Pct(pd)}", $"Seuil {Fmt.Pct(DrawModel)}", pd >= DrawModel);
        if (m.ExpTotalGoals is double exp)
            Add($"Buts attendus : {Fmt.Num(exp, 2)}", "Un match fermé favorise le nul", exp < ClosedGame);
        if (s == null) return;
        if (s.HomeElo is double he && s.AwayElo is double ae)
            Add($"Écart Elo : {Fmt.Num(Math.Abs(he - ae), 0)} points", "Équipes de niveau proche ?", Math.Abs(he - ae) < DrawEloGap);
        if (s.HomePpg is double hp && s.AwayPpg is double ap)
            Add($"Écart de points par match : {Fmt.Num(Math.Abs(hp - ap), 2)}", "Saison en cours", Math.Abs(hp - ap) < DrawPpgGap);
        if (s.HomePts5 is double h5 && s.AwayPts5 is double a5)
            Add($"Écart de forme : {Fmt.Num(Math.Abs(h5 - a5), 2)} pt/match", "5 derniers matchs", Math.Abs(h5 - a5) < DrawFormGap);
    }

    private static string Rank(int r) => r == 1 ? "1er" : $"{r}e";
}
