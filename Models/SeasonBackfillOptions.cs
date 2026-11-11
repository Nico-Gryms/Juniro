// =============================================================================
// SeasonBackfillOptions.cs — PARAMÈTRES DU RATTRAPAGE
// -----------------------------------------------------------------------------
// Rempli par Configuration\BackfillCommand.cs à partir de la ligne de commande,
// lu par FlashscoreScraperJob.ExecuteSeasonBackfillAsync.
// Une saison commence le 1er juillet (CurrentSeasonStart).
// =============================================================================
using Juniro.ScraperService.Configuration;

namespace Juniro.ScraperService.Models;

/// <summary>
/// Paramètres du backfill de saison (rattrapage des matchs manquants).
/// Toutes les dates sont en heure de Paris, comme MatchDate en base.
/// </summary>
public class SeasonBackfillOptions
{
    /// <summary>Début de la fenêtre à récupérer (heure de Paris).</summary>
    public DateTime From { get; set; } = CurrentSeasonStart(ParisTime.Now);

    /// <summary>Fin de la fenêtre à récupérer (heure de Paris).</summary>
    public DateTime To { get; set; } = ParisTime.Now;

    /// <summary>Noms de ligues à traiter. Vide = les 10 ligues.</summary>
    public string[] LeagueFilter { get; set; } = Array.Empty<string>();

    /// <summary>Ignore les matchs déjà complets en base (score + statistiques des deux équipes).</summary>
    public bool SkipExisting { get; set; } = true;

    /// <summary>Enregistre aussi les matchs non terminés (reportés, à venir).</summary>
    public bool IncludeUnfinished { get; set; }

    /// <summary>Phase de collecte uniquement : compte les matchs sans rien scraper ni écrire.</summary>
    public bool DryRun { get; set; }

    /// <summary>Null = valeur de ScraperSettings:Headless.</summary>
    public bool? Headless { get; set; }

    /// <summary>0 = pas de limite. Sinon, nombre maximum de matchs traités (tests rapides).</summary>
    public int Limit { get; set; }

    public int DelayMsMin { get; set; } = 700;
    public int DelayMsMax { get; set; } = 1400;

    /// <summary>
    /// Garde-fou sur le nombre de clics "Afficher plus de matchs" par ligue. La boucle
    /// s'arrête d'elle-même dès que plus aucun match n'arrive, ce plafond ne sert qu'à
    /// éviter une boucle infinie si Flashscore change de comportement.
    /// </summary>
    public int MaxMoreClicks { get; set; } = 60;

    /// <summary>
    /// Mode « rattrapage automatique » (job Hangfire repair-recent-matches), null sinon.
    /// Seuls sont traités : les matchs jamais enregistrés (quelle que soit leur date dans la
    /// fenêtre From–To), et les matchs incomplets en base joués depuis cette date. Les matchs
    /// incomplets plus anciens ne sont plus retentés (Flashscore n'a souvent rien de plus).
    /// </summary>
    public DateTime? RepairSince { get; set; }

    /// <summary>
    /// Début de la saison en cours, avec la même règle que la base : bascule au 1er juillet.
    /// </summary>
    public static DateTime CurrentSeasonStart(DateTime now)
    {
        int startYear = now.Month >= 7 ? now.Year : now.Year - 1;
        return new DateTime(startYear, 7, 1, 0, 0, 0, DateTimeKind.Unspecified);
    }
}
