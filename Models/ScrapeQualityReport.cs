// =============================================================================
// ScrapeQualityReport.cs — CONTRÔLE QUALITÉ D'UNE EXÉCUTION DU SCRAPER
// -----------------------------------------------------------------------------
// Rôle : compter, pour chaque donnée (score, xG, cotes...), la part de matchs où elle a
//        été trouvée, et lever une ALERTE si ce taux passe sous un seuil. C'est ce qui
//        révèle qu'un changement de Flashscore a cassé un sélecteur.
// Étapes : Record(match) pour chaque match -> Evaluate() calcule les taux ->
//          Render() produit le texte écrit dans les logs et dans AppExecutionLogs.
// Pour modifier un seuil : tableau Expectations (champ, % minimum, terminé/à venir).
// =============================================================================
using System.Text;

namespace Juniro.ScraperService.Models;

/// <summary>Une ligne du rapport : donnée, trouvée / total, %, seuil, alerte ou non.</summary>
public record QualityLine(string Field, int Hits, int Total, double Pct, double MinPct, bool Degraded);

/// <summary>
/// Taux de remplissage observés pendant une exécution.
///
/// C'est le détecteur de panne silencieuse. Quand Flashscore change la structure de ses
/// pages, le scraper ne lève aucune exception : il continue de « réussir » en n'écrivant
/// que des NULL. Rien ne le signale, et on ne s'en aperçoit que des mois plus tard en
/// interrogeant la base. Seule la chute d'un taux de remplissage le révèle le jour même.
/// </summary>
public class ScrapeQualityReport
{
    /// <summary>En dessous, l'échantillon est trop petit pour conclure.</summary>
    private const int MinSample = 10;

    private readonly Dictionary<string, int> _hits = new(StringComparer.Ordinal);

    public int FinishedCount { get; private set; }

    /// <summary>
    /// Vrai pour le rattrapage historique, qui doit ramener la cote d'ouverture. Faux pour le
    /// service journalier, qui ne relève que la cote actuelle : exiger l'ouverture y
    /// déclencherait une fausse alerte à chaque exécution.
    /// </summary>
    public bool ExpectOpeningOdds { get; set; }
    public int UpcomingCount { get; private set; }

    /// <summary>
    /// Seuil minimal attendu par champ. Calé sur les taux réellement constatés en base
    /// (xG et cotes au-dessus de 99 %), avec une marge confortable pour éviter les
    /// fausses alertes dues aux matchs réellement incomplets chez Flashscore.
    /// </summary>
    private static readonly (string Field, double MinPct, bool OnFinished)[] Expectations =
    {
        ("Score",                   99, true),
        ("Score mi-temps",          95, true),
        ("Statistiques",            95, true),
        ("xG",                      85, true),
        ("Possession",              90, true),
        ("Tirs",                    90, true),
        ("Tirs cadrés",             90, true),
        ("Corners",                 90, true),
        ("Cote 1X2",                90, true),
        ("Cote O/U 2.5",            75, true),
        ("Cote BTTS",               75, true),
        ("Cote Double chance",      75, true),
        ("Cote DNB",                75, true),
        ("Cote ouverture 1X2",      70, true),
        ("Cote ouverture O/U 2.5",  60, true),
        ("Cote ouverture BTTS",     60, true),
        ("Cote 1X2 (à venir)",      90, false),
        ("Cote O/U 2.5 (à venir)",  75, false),
        ("Cote BTTS (à venir)",     75, false),
    };

    /// <summary>Comptabilise un match : terminé (scores, stats, cotes) ou à venir (journée, cotes).</summary>
    public void Record(ScrapedMatchData d)
    {
        bool finished = d.MatchStatus == "Finished";

        if (finished)
        {
            FinishedCount++;
            Hit("Score",        d.HomeScoreFT.HasValue && d.AwayScoreFT.HasValue);
            Hit("Score mi-temps", d.HomeScoreHT.HasValue && d.AwayScoreHT.HasValue);
            Hit("Statistiques", d.HomeStats != null && d.AwayStats != null);
            Hit("xG",           d.HomeStats?.ExpectedGoals.HasValue == true);
            Hit("Possession",   d.HomeStats?.PossessionPct.HasValue == true);
            Hit("Tirs",         d.HomeStats?.Shots.HasValue == true);
            Hit("Tirs cadrés",  d.HomeStats?.ShotsOnTarget.HasValue == true);
            Hit("Corners",      d.HomeStats?.Corners.HasValue == true);
            Hit("Cote 1X2",     d.Odds?.OddHome.HasValue == true);
            Hit("Cote O/U 2.5", d.Odds?.OddOv25.HasValue == true);
            Hit("Cote BTTS",    d.Odds?.OddBTTS.HasValue == true);
            Hit("Cote Double chance", d.Odds?.OddHomeDraw.HasValue == true);
            Hit("Cote DNB",     d.Odds?.OddHomeDNB.HasValue == true);
            Hit("Cote ouverture 1X2", d.Odds?.Opening?.OddHome.HasValue == true);
            Hit("Cote ouverture O/U 2.5", d.Odds?.Opening?.OddOv25.HasValue == true);
            Hit("Cote ouverture BTTS", d.Odds?.Opening?.OddBTTS.HasValue == true);
        }
        else
        {
            UpcomingCount++;
            Hit("Cote 1X2 (à venir)",     d.Odds?.OddHome.HasValue == true);
            Hit("Cote O/U 2.5 (à venir)", d.Odds?.OddOv25.HasValue == true);
            Hit("Cote BTTS (à venir)",    d.Odds?.OddBTTS.HasValue == true);
        }
    }

    /// <summary>Ajoute 1 au compteur du champ si la donnée est présente (le total est compté à part).</summary>
    private void Hit(string field, bool present)
    {
        _hits.TryGetValue(field, out int current);
        _hits[field] = current + (present ? 1 : 0);
    }

    /// <summary>Calcule le taux de remplissage de chaque champ et le compare à son seuil.</summary>
    public IReadOnlyList<QualityLine> Evaluate()
    {
        var lines = new List<QualityLine>();

        foreach (var (field, minPct, onFinished) in Expectations)
        {
            if (!ExpectOpeningOdds && field.StartsWith("Cote ouverture", StringComparison.Ordinal)) continue;

            int total = onFinished ? FinishedCount : UpcomingCount;
            if (total == 0) continue;

            _hits.TryGetValue(field, out int hits);
            double pct = 100.0 * hits / total;

            // Pas d'alerte sur un échantillon trop faible : un seul match raté ferait
            // chuter le taux à 0 % et déclencherait une fausse alerte à chaque test.
            bool degraded = total >= MinSample && pct < minPct;

            lines.Add(new QualityLine(field, hits, total, Math.Round(pct, 1), minPct, degraded));
        }

        return lines;
    }

    public bool HasDegradation => Evaluate().Any(l => l.Degraded);

    /// <summary>Rapport lisible : une ligne par champ, « ALERTE » quand le seuil n'est pas atteint.</summary>
    public string Render()
    {
        var lines = Evaluate();
        if (lines.Count == 0) return "Contrôle qualité : aucun match traité.";

        var sb = new StringBuilder();
        sb.AppendLine($"Contrôle qualité — {FinishedCount} match(s) terminé(s), {UpcomingCount} à venir");

        foreach (var l in lines)
        {
            sb.AppendLine(
                $"  {(l.Degraded ? "ALERTE" : "  ok  ")} {l.Field,-24} {l.Pct,6:0.0} %   " +
                $"({l.Hits}/{l.Total}, seuil {l.MinPct:0} %)");
        }

        if (FinishedCount > 0 && FinishedCount < MinSample)
            sb.AppendLine($"  (échantillon < {MinSample} matchs : seuils non appliqués)");

        return sb.ToString().TrimEnd();
    }
}
