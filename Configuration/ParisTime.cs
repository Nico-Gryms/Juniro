// =============================================================================
// ParisTime.cs — HEURE DE PARIS
// -----------------------------------------------------------------------------
// Rôle : fournir l'heure de Paris quel que soit le fuseau du PC.
// Toutes les heures de match (dbo.Matches.MatchDate) sont stockées en heure de Paris,
// changements d'heure compris ; les relevés de cotes (OddsQuotes.QuoteDateUtc) en UTC.
// Utilisé par : JuniroRepository (saison courante), FlashscoreScraperJob (dates).
// =============================================================================
namespace Juniro.ScraperService.Configuration;

/// <summary>
/// Référence horaire unique du projet : les dates de match sont stockées en heure de Paris,
/// changement d'heure été/hiver compris, comme les données historiques déjà en base.
/// </summary>
public static class ParisTime
{
    /// <summary>Identifiant IANA, utilisé aussi pour forcer le fuseau du navigateur Playwright.</summary>
    public const string IanaId = "Europe/Paris";

    public static readonly TimeZoneInfo Zone = Resolve();

    /// <summary>Heure courante à Paris, quel que soit le fuseau de la machine.</summary>
    public static DateTime Now => TimeZoneInfo.ConvertTimeFromUtc(DateTime.UtcNow, Zone);

    /// <summary>
    /// Fuseau de Paris : identifiant IANA « Europe/Paris », sinon l'identifiant Windows
    /// « Romance Standard Time » (selon la version de Windows / .NET).
    /// </summary>
    private static TimeZoneInfo Resolve()
    {
        try
        {
            return TimeZoneInfo.FindSystemTimeZoneById(IanaId);
        }
        catch (TimeZoneNotFoundException)
        {
            // Windows sans ICU : identifiant Windows équivalent.
            return TimeZoneInfo.FindSystemTimeZoneById("Romance Standard Time");
        }
    }
}
