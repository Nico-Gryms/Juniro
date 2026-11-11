// =============================================================================
// ScrapedMatchData.cs — CE QUE LE SCRAPER LIT SUR UNE PAGE DE MATCH
// -----------------------------------------------------------------------------
// Objets « en mémoire » remplis par FlashscoreScraperJob, puis écrits en base par
// JuniroRepository.SaveFullMatchDataAsync. Aucun accès base ici.
//   TargetLeague      une des 10 ligues suivies (pays, nom, division, adresse Flashscore)
//   MatchQueueItem    un match à traiter : sa clé Flashscore + sa ligue
//   ScrapedMatchData  le match complet : équipes, date, statut, scores, journée, stats, cotes
//   ScrapedTeamStats  statistiques d'une équipe (xG, possession, tirs, tirs cadrés, corners...)
//   ScrapedOdds       les 15 cotes d'un relevé (1X2, double chance, DNB, BTTS, +/-1.5/2.5/3.5)
//                     + la cote d'ouverture (rattrapage uniquement)
// Pour ajouter une donnée : la déclarer ici, la lire dans FlashscoreScraperJob, l'écrire
// dans JuniroRepository, et l'ajouter au contrôle qualité (ScrapeQualityReport) si besoin.
// =============================================================================
namespace Juniro.ScraperService.Models;

/// <summary>
/// Ligue suivie. UrlSlug = début d'adresse Flashscore (ex. /football/france/ligue-1/) ;
/// DisplayAliases = noms affichés sur la page d'accueil pour reconnaître la ligue.
/// </summary>
public record TargetLeague(
    string CountryName,
    string LeagueName,
    int TierLevel,
    string UrlSlug,
    string[] DisplayAliases
);

/// <summary>Match en file d'attente : clé Flashscore (8 caractères, ex. « zyZq2xcL ») + ligue.</summary>
public record MatchQueueItem(string FlashscoreMatchKey, TargetLeague League);

/// <summary>Toutes les informations lues sur la page d'un match, prêtes à être enregistrées.</summary>
public class ScrapedMatchData
{
    public string FlashscoreMatchKey { get; set; } = string.Empty;

    // Infos Ligue & Saison
    public string CountryName { get; set; } = string.Empty;
    public string LeagueName { get; set; } = string.Empty;
    public int TierLevel { get; set; } = 1;
    public DateTime MatchDate { get; set; }

    // Infos Équipes
    public string HomeTeamName { get; set; } = string.Empty;
    public string HomeTeamKey { get; set; } = string.Empty;
    public string AwayTeamName { get; set; } = string.Empty;
    public string AwayTeamKey { get; set; } = string.Empty;

    // Statut, Journée et Scores (FT + HT)
    /// <summary>
    /// Finished, Scheduled, Live, Postponed, Cancelled, Abandoned, Unknown.
    /// "Unknown" interdit toute écriture de score ou de statistique.
    /// </summary>
    public string MatchStatus { get; set; } = "Scheduled";

    /// <summary>Libellé brut lu sur Flashscore, conservé pour le diagnostic (non persisté).</summary>
    public string? RawStatusText { get; set; }
    public int? HomeScoreFT { get; set; }
    public int? AwayScoreFT { get; set; }
    public int? HomeScoreHT { get; set; }
    public int? AwayScoreHT { get; set; }
    public int? RoundNumber { get; set; }
    public string? RoundName { get; set; }

    /// <summary>
    /// False si l'heure de coup d'envoi n'a pas pu être lue : MatchDate contient alors
    /// une valeur de repli (date du jour) qu'il ne faut pas enregistrer en historique.
    /// </summary>
    public bool KickoffParsed { get; set; }

    // Statistiques détaillées
    public ScrapedTeamStats? HomeStats { get; set; }
    public ScrapedTeamStats? AwayStats { get; set; }

    // Cotes complètes
    public ScrapedOdds? Odds { get; set; }
}

/// <summary>Statistiques d'une équipe pour un match (NULL = non trouvé sur la page).</summary>
public class ScrapedTeamStats
{
    public decimal? ExpectedGoals { get; set; }
    public decimal? PossessionPct { get; set; }
    public int? Shots { get; set; }
    public int? ShotsOnTarget { get; set; }
    public int? Corners { get; set; }
    public int? Fouls { get; set; }
    public int? YellowCards { get; set; }
    public int? RedCards { get; set; }
}

/// <summary>
/// Un relevé de cotes (un bookmaker, un instant). Opening = même structure pour la cote
/// d'ouverture, remplie seulement par le rattrapage.
/// </summary>
public class ScrapedOdds
{
    public int BookmakerId { get; set; }

    /// <summary>
    /// Cotes d'ouverture du même bookmaker, lues dans l'attribut title des cellules Flashscore
    /// (« ouverture » actuelle »). Un seul passage sur un match terminé donne ainsi l'ouverture
    /// ET la clôture, sans avoir eu à scraper le match plusieurs jours avant.
    /// </summary>
    public ScrapedOdds? Opening { get; set; }

    /// <summary>Nom du bookmaker dont proviennent les cotes (journalisation, non persisté).</summary>
    public string? SourceBookmaker { get; set; }

    // 1X2
    public decimal? OddHome { get; set; }
    public decimal? OddDraw { get; set; }
    public decimal? OddAway { get; set; }

    // Draw No Bet (Remboursé si match nul)
    public decimal? OddHomeDNB { get; set; }
    public decimal? OddAwayDNB { get; set; }

    // Double Chance (1X et X2)
    public decimal? OddHomeDraw { get; set; }
    public decimal? OddAwayDraw { get; set; }

    // Both Teams To Score (Les deux équipes marquent : Oui / Non)
    public decimal? OddBTTS { get; set; }
    public decimal? OddBTNS { get; set; }

    // Over / Under 1.5, 2.5 et 3.5 buts
    public decimal? OddUn15 { get; set; }
    public decimal? OddOv15 { get; set; }
    public decimal? OddUn25 { get; set; }
    public decimal? OddOv25 { get; set; }
    public decimal? OddUn35 { get; set; }
    public decimal? OddOv35 { get; set; }

    /// <summary>
    /// Vrai dès qu'une seule cote a été récupérée. Ne tester que 1X2, O/U 2.5 et BTTS
    /// faisait perdre un match dont seuls Double Chance ou DNB étaient disponibles.
    /// </summary>
    public bool HasAnyOdd =>
        OddHome.HasValue || OddDraw.HasValue || OddAway.HasValue ||
        OddHomeDNB.HasValue || OddAwayDNB.HasValue ||
        OddHomeDraw.HasValue || OddAwayDraw.HasValue ||
        OddBTTS.HasValue || OddBTNS.HasValue ||
        OddUn15.HasValue || OddOv15.HasValue ||
        OddUn25.HasValue || OddOv25.HasValue ||
        OddUn35.HasValue || OddOv35.HasValue;
}