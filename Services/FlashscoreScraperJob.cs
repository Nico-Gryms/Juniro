// =============================================================================
// FlashscoreScraperJob.cs — LE SCRAPER FLASHSCORE (cœur de la collecte)
// -----------------------------------------------------------------------------
// Rôle : piloter un navigateur Chromium (Playwright) sur www.flashscore.fr, lire pour chaque
//        match des 10 ligues : équipes, date, statut, scores, journée, statistiques et cotes,
//        puis tout confier à JuniroRepository pour l'écriture en base.
//
// Deux points d'entrée :
//   A. ExecuteDailyScrapeAsync   service journalier (Hangfire, 03h00) : matchs de J-1 à J+3,
//                                cote ACTUELLE uniquement.
//   B. ExecuteSeasonBackfillAsync rattrapage (ligne de commande --backfill) : tous les matchs
//                                joués d'une période, cote d'OUVERTURE + cote FINALE.
//   C. ExecuteRecentRepairAsync  rattrapage AUTOMATIQUE (Hangfire, 04h30) : matchs jamais
//                                enregistrés de la saison + matchs incomplets des 10 derniers
//                                jours (score, statistiques ou cotes manquants). Même moteur que B.
//
// Plan du fichier (chercher les bandeaux « // ===== » pour naviguer) :
//   1. Ligues suivies, statuts autorisés, constructeur
//   2. A. Job journalier
//   3. B. Rattrapage de saison (+ CollectLeagueResultKeysAsync : page « résultats » d'une ligue)
//      C. Rattrapage automatique (ExecuteRecentRepairAsync)
//   4. Collecte des matchs du jour sur l'accueil (défilement, filtrage des 10 ligues)
//   5. Page détail d'un match : ScrapeMatchDetailTabsAsync (équipes, date, statut, journée,
//      score mi-temps, statistiques, cotes)
//   6. Cotes : adresses des sous-pages, lecture des tableaux, choix du bookmaker, contrôles
//      de cohérence (Plus/Moins, double chance, marge)
//   7. Score à la mi-temps
//   8. Navigateur : contexte optimisé, navigation par jour, cookies
//   9. Diagnostic et contrôle qualité (captures HTML/PNG, rapport des taux de remplissage)
//  10. Statistiques : lecture du tableau, correspondance libellé Flashscore -> colonne
//  11. Petits outils : statut du match, conversion des nombres
//
// Si Flashscore change ses pages : le contrôle qualité (fin de chaque exécution) affiche
// « ALERTE » sur la donnée touchée, et une capture est enregistrée dans diagnostics\.
// Les sélecteurs CSS à revoir sont dans les sections 4 à 7 et 10.
// Documentation : DOCUMENTATION.md, chapitre 3.
// =============================================================================
using Juniro.ScraperService.Configuration;
using Juniro.ScraperService.Data;
using Juniro.ScraperService.Models;
using Hangfire;
using Microsoft.Extensions.FileSystemGlobbing;
using Microsoft.Playwright;
using System.Diagnostics;
using System.Globalization;
using System.Reflection.Emit;
using System.Text.RegularExpressions;

namespace Juniro.ScraperService.Services;

/// <summary>Scraper Flashscore : job journalier et rattrapage de saison.</summary>
public class FlashscoreScraperJob
{
    private readonly ILogger<FlashscoreScraperJob> _logger;
    private readonly JuniroRepository _repository;
    private readonly IConfiguration _config;

    // ===== 1. LIGUES SUIVIES =====================================================
    // Pays, nom en base, division (1 ou 2), début d'adresse Flashscore, noms affichés.
    // Pour suivre une nouvelle ligue : ajouter une ligne ici (et la division dans dbo.Leagues).
    private static readonly List<TargetLeague> AllowedLeagues = new()
    {
        // FRANCE
        new("France", "Ligue 1", 1, "/football/france/ligue-1/", new[] { "ligue 1" }),
        new("France", "Ligue 2", 2, "/football/france/ligue-2/", new[] { "ligue 2" }),

        // ANGLETERRE
        new("Angleterre", "Premier League", 1, "/football/angleterre/premier-league/", new[] { "premier league" }),
        new("Angleterre", "Championship", 2, "/football/angleterre/championship/", new[] { "championship" }),

        // ALLEMAGNE
        new("Allemagne", "Bundesliga", 1, "/football/allemagne/bundesliga/", new[] { "bundesliga" }),
        new("Allemagne", "Bundesliga 2", 2, "/football/allemagne/2-bundesliga/", new[] { "2. bundesliga", "bundesliga 2" }),

        // ESPAGNE
        new("Espagne", "LaLiga", 1, "/football/espagne/laliga/", new[] { "laliga" }),
        new("Espagne", "LaLiga2", 2, "/football/espagne/laliga2/", new[] { "laliga2" }),

        // ITALIE
        new("Italie", "Serie A", 1, "/football/italie/serie-a/", new[] { "serie a" }),
        new("Italie", "Serie B", 2, "/football/italie/serie-b/", new[] { "serie b" })
    };

    /// <summary>Les 10 ligues suivies, exposées pour la ligne de commande du backfill.</summary>
    public static IReadOnlyList<TargetLeague> TargetLeagues => AllowedLeagues;

    /// <summary>
    /// Seules valeurs acceptées par la contrainte CK_Matches_Status en base.
    /// Tout autre statut ("Live", "Unknown") ferait échouer l'INSERT, et comme l'écriture
    /// est transactionnelle, le match, ses statistiques ET ses cotes seraient perdus.
    /// </summary>
    private static readonly HashSet<string> PersistableStatuses = new(StringComparer.Ordinal)
    {
        "Finished", "Scheduled", "Postponed", "Cancelled", "Interrupted"
    };

    /// <summary>Reçoit le journal, le dépôt SQL et la configuration (injection de dépendances).</summary>
    public FlashscoreScraperJob(
        ILogger<FlashscoreScraperJob> logger,
        JuniroRepository repository,
        IConfiguration config)
    {
        _logger = logger;
        _repository = repository;
        _config = config;
    }

    // ===== 2. A. JOB JOURNALIER ==================================================

    /// <summary>
    /// Exécution quotidienne : 1) parcourt l'accueil Flashscore jour par jour de J-1 à J+3 et
    /// retient les matchs des 10 ligues ; 2) ouvre chaque match, lit tout, l'enregistre ;
    /// 3) écrit le rapport de qualité. Réglages : section ScraperSettings de appsettings
    /// (DaysBehind, DaysAhead, Headless, ContextRecycleEveryNMatches...).
    /// </summary>
    public async Task ExecuteDailyScrapeAsync()
    {
        string baseUrl = (_config["ScraperSettings:BaseUrl"] ?? "https://www.flashscore.fr").TrimEnd('/');
        bool headless = _config.GetValue<bool>("ScraperSettings:Headless", true);
        int daysBehind = _config.GetValue<int>("ScraperSettings:DaysBehind", 1); // J-1
        int daysAhead = _config.GetValue<int>("ScraperSettings:DaysAhead", 3);   // J+3
        int recycleEvery = _config.GetValue<int>("ScraperSettings:ContextRecycleEveryNMatches", 20); 
        int currentRunId = await _repository.StartExecutionRunAsync("ScheduledTask");
        await _repository.LogExecutionAsync(currentRunId, "INFO", "Scraper.Startup",
            $"Exécution initialisée : matchs de J-{daysBehind} à J+{daysAhead}.");

        // Tout le travail est dans DailyScrapeCoreAsync ; ici on garantit que l'exécution est
        // TOUJOURS clôturée dans AppExecutionRuns (statut, durée, compteurs), même en cas de plantage.
        var stats = new RunStats();
        string? globalError = null;
        try
        {
            await DailyScrapeCoreAsync(currentRunId, stats, baseUrl, headless, daysBehind, daysAhead, recycleEvery);
        }
        catch (Exception ex)
        {
            globalError = ex.Message;
            await _repository.LogExecutionAsync(currentRunId, "FATAL", "Scraper.Crash", "Le job a crashé de manière inattendue.", null, ex.ToString());
            throw;
        }
        finally
        {
            await FinishRunAsync(currentRunId, stats, "Scraper", globalError);
        }
    }

    /// <summary>
    /// Cœur du job journalier : collecte (étape 1) puis traitement de chaque match (étape 2).
    /// Remplit <paramref name="stats"/> au fil de l'eau ; la clôture est faite par l'appelant.
    /// </summary>
    private async Task DailyScrapeCoreAsync(int currentRunId, RunStats stats, string baseUrl, bool headless,
        int daysBehind, int daysAhead, int recycleEvery)
    {
        _logger.LogInformation("Démarrage du scraping global (Accueil Flashscore + Scroll) de J-{Behind} à J+{Ahead}...", daysBehind, daysAhead);

        using var playwright = await Playwright.CreateAsync();
        await using var browser = await playwright.Chromium.LaunchAsync(new BrowserTypeLaunchOptions
        {
            Headless = headless,
            Args = new[] { "--disable-gpu", "--disable-dev-shm-usage", "--no-sandbox" }
        });

        // -------------------------------------------------------------------------
        // ÉTAPE 1 : Collecter les matchs depuis la page d'accueil jour par jour
        // -------------------------------------------------------------------------
        var matchQueue = new Dictionary<string, MatchQueueItem>();

        await using (var listContext = await CreateOptimizedContextAsync(browser))
        {
            var page = await listContext.NewPageAsync();
            await page.GotoAsync(baseUrl, new PageGotoOptions { WaitUntil = WaitUntilState.DOMContentLoaded });
            await DismissCookiesIfPresentAsync(page);

            for (int offset = -daysBehind; offset <= daysAhead; offset++)
            {
                _logger.LogInformation("Collecte des matchs pour le décalage J{Offset:+#;-#;0}...", offset);

                // 1. Naviguer vers le jour J+offset
                await NavigateToDayOffsetAsync(page, baseUrl, offset);

                // 2. Faire défiler et cliquer sur "Afficher plus" pour charger tous les matchs de la page
                await ScrollAndExpandAllMatchesAsync(page);

                // 3. Extraire et filtrer les matchs des 10 ligues
                var dayMatches = await ExtractFilteredMatchesForDayAsync(page);

                foreach (var item in dayMatches)
                {
                    matchQueue.TryAdd(item.FlashscoreMatchKey, item);
                }

                _logger.LogInformation("[Jour J{Offset:+#;-#;0}] {Count} matchs retenus dans les 10 ligues cibles.", offset, dayMatches.Count);
            }
        }

        // -------------------------------------------------------------------------
        // ÉTAPE 2 : Traiter chaque match et ses onglets détaillés
        // -------------------------------------------------------------------------
        var allItems = matchQueue.Values.ToList();
        _logger.LogInformation("Total de {Total} matchs uniques à traiter en détail.", allItems.Count);
        stats.Found = allItems.Count;
        foreach (var queued in allItems) stats.CountLeague(queued.League.LeagueName, found: 1);
        await _repository.LogExecutionAsync(currentRunId, "INFO", "Scraper.Collected",
            $"{allItems.Count} matchs trouvés dans les 10 ligues : {stats.RenderLeagueFound()}");

        IBrowserContext? detailContext = null;
        IPage? detailPage = null;

        try
        {
            for (int i = 0; i < allItems.Count; i++)
            {
                var item = allItems[i];

                // Un nouveau contexte navigateur tous les N matchs : évite que la mémoire de Chromium
                // gonfle au fil de centaines de pages.
                if (detailContext == null || i % recycleEvery == 0)
                {
                    if (detailContext != null) await detailContext.DisposeAsync();
                    detailContext = await CreateOptimizedContextAsync(browser);
                    detailPage = await detailContext.NewPageAsync();
                }

                try
                {
                    // Journalier : uniquement la cote actuelle, relevée à chaque passage (J-3 … J-1).
                    var matchData = await ScrapeMatchDetailTabsAsync(detailPage!, baseUrl, item, includeOpeningOdds: false);

                    if (matchData != null && !PersistableStatuses.Contains(matchData.MatchStatus))
                    {
                        // Match en cours ou statut illisible : rien n'est écrit. Il sera repris
                        // à la prochaine exécution, une fois terminé.
                        _logger.LogInformation(
                            "⏭️ IGNORÉ ({Status}, « {Raw} ») : {MatchKey}",
                            matchData.MatchStatus, matchData.RawStatusText, item.FlashscoreMatchKey);
                        stats.Skipped++;
                        await _repository.LogExecutionAsync(currentRunId, "INFO", "Scraper.MatchSkipped",
                            $"Non enregistré : statut {matchData.MatchStatus} (« {matchData.RawStatusText} »), repris au prochain passage.",
                            item.FlashscoreMatchKey);
                        matchData = null;
                    }
                    else if (matchData == null)
                    {
                        stats.Failed++;
                        stats.CountLeague(item.League.LeagueName, failed: 1);
                        _logger.LogWarning("Aucune donnée lue pour le match {MatchKey}.", item.FlashscoreMatchKey);
                        await _repository.LogExecutionAsync(currentRunId, "WARN", "Scraper.MatchEmpty",
                            "Aucune donnée lue sur la page du match.", item.FlashscoreMatchKey);
                    }

                    if (matchData != null)
                    {
                        await _repository.SaveFullMatchDataAsync(matchData);
                        _quality.Record(matchData);
                        stats.Saved++;
                        stats.CountLeague(item.League.LeagueName, saved: 1);

                        string logMessage = $"{matchData.HomeTeamName} {matchData.HomeScoreFT ?? 0}-{matchData.AwayScoreFT ?? 0} {matchData.AwayTeamName} | 1X2 {matchData.Odds?.SourceBookmaker}: [{matchData.Odds?.OddHome} / {matchData.Odds?.OddDraw} / {matchData.Odds?.OddAway}] | O/U 2.5: [{matchData.Odds?.OddOv25} / {matchData.Odds?.OddUn25}]";

                        _logger.LogInformation("✅ ENREGISTRÉ: {Message}", logMessage);
                        // Une ligne par match enregistré : c'est la preuve de l'import (sans risque
                        // depuis que LogExecutionAsync ne peut plus faire échouer le match).
                        // await _repository.LogExecutionAsync(currentRunId, "INFO", "Scraper.MatchProcessed", logMessage, item.FlashscoreMatchKey);
                    }

                    // Pause aléatoire entre deux matchs pour ne pas surcharger le site.
                    await Task.Delay(Random.Shared.Next(700, 1400));
                }
                catch (Exception ex)
                {
                    stats.Failed++;
                    stats.CountLeague(item.League.LeagueName, failed: 1);
                    _logger.LogError(ex, "Erreur sur le match {MatchKey}", item.FlashscoreMatchKey);
                    await _repository.LogExecutionAsync(currentRunId, "ERROR", "Scraper.MatchError", $"Échec du scraping pour le match {item.FlashscoreMatchKey}", item.FlashscoreMatchKey, ex.ToString());
                }
            }
        }
        finally
        {
            // Plantage : remonté à ExecuteDailyScrapeAsync, qui l'écrit (FATAL) et clôture le run.
            if (detailContext != null) await detailContext.DisposeAsync();
        }
    }

    // =========================================================================
    // ===== 3. B. RATTRAPAGE DE SAISON (BACKFILL) =====
    // Même extraction détaillée que le job quotidien, mais la collecte part de la
    // page "résultats" de chaque ligue au lieu du calendrier de l'accueil. C'est le
    // seul moyen de remonter tous les matchs déjà joués depuis le début de saison.
    // =========================================================================

    /// <summary>
    /// Récupère tous les matchs joués de la fenêtre demandée pour les ligues demandées.
    /// Reprend là où il s'est arrêté : les matchs déjà complets en base sont ignorés.
    /// Retourne le nombre de matchs enregistrés.
    /// </summary>
    public async Task<int> ExecuteSeasonBackfillAsync(SeasonBackfillOptions options, string triggerSource = "Backfill")
    {
        string baseUrl = (_config["ScraperSettings:BaseUrl"] ?? "https://www.flashscore.fr").TrimEnd('/');
        bool headless = options.Headless ?? _config.GetValue<bool>("ScraperSettings:Headless", true);
        int recycleEvery = _config.GetValue<int>("ScraperSettings:ContextRecycleEveryNMatches", 20);

        var leagues = ResolveLeagues(options.LeagueFilter);
        if (leagues.Count == 0)
        {
            _logger.LogError(
                "Aucune ligue ne correspond au filtre fourni. Ligues valides : {Leagues}",
                string.Join(", ", AllowedLeagues.Select(l => l.LeagueName)));
            return 0;
        }

        _logger.LogInformation(
            "Backfill du {From:yyyy-MM-dd} au {To:yyyy-MM-dd} sur {Count} ligue(s) : {Leagues}",
            options.From, options.To, leagues.Count,
            string.Join(", ", leagues.Select(l => l.LeagueName)));

        _sector = triggerSource;
        int runId = await _repository.StartExecutionRunAsync(triggerSource);
        await _repository.LogExecutionAsync(runId, "INFO", $"{_sector}.Startup",
            (options.RepairSince is { } since
                ? $"Rattrapage automatique : matchs jamais enregistrés depuis le {options.From:yyyy-MM-dd}, " +
                  $"matchs incomplets depuis le {since:yyyy-MM-dd}, sur {leagues.Count} ligue(s) : "
                : $"Backfill du {options.From:yyyy-MM-dd} au {options.To:yyyy-MM-dd} sur {leagues.Count} ligue(s) : ") +
            string.Join(", ", leagues.Select(l => l.LeagueName)));

        // Comme le journalier : le travail est dans BackfillCoreAsync, la clôture du run est garantie ici.
        var stats = new RunStats();
        string? error = null;
        try
        {
            return await BackfillCoreAsync(options, leagues, runId, stats, baseUrl, headless, recycleEvery);
        }
        catch (Exception ex)
        {
            error = ex.Message;
            await _repository.LogExecutionAsync(runId, "FATAL", $"{_sector}.Crash",
                "Le rattrapage s'est interrompu de manière inattendue.", null, ex.ToString());
            throw;
        }
        finally
        {
            await FinishRunAsync(runId, stats, _sector, error);
        }
    }

    /// <summary>Préfixe des lignes de journal du rattrapage : « Backfill » ou « Repair ».</summary>
    private string _sector = "Backfill";

    // =========================================================================
    // ===== C. RATTRAPAGE AUTOMATIQUE (job Hangfire repair-recent-matches) =====
    // Filet de sécurité du journalier, qui ne repasse qu'une fois sur chaque match (à J+1) :
    // si ce passage échoue (PC éteint, Flashscore lent, statistiques pas encore publiées,
    // plantage), le match resterait incomplet pour toujours. Chaque matin, ce job :
    //   1. lit la page « résultats » des 10 ligues en entier (Repair:MaxMoreClicks clics au plus ;
    //      la lecture s'arrête d'elle-même quand toute la saison est affichée) et repère les
    //      matchs JAMAIS enregistrés de la saison, y compris ceux des premières journées ;
    //   2. ajoute les matchs en base des Repair:DaysBack derniers jours qui sont incomplets
    //      (statut resté « Scheduled », score, mi-temps, statistiques ou cotes manquants) ;
    //   3. les repasse tous avec le moteur du backfill (cote d'ouverture + cote finale).
    // Réglages : appsettings, section Repair (Enabled, Cron, DaysBack, MaxMoreClicks, MaxMatches).
    // Journal : TriggerSource « Repair » dans dbo.AppExecutionRuns (Q4_DernieresExecutions.sql).
    // =========================================================================

    /// <summary>
    /// Rattrapage automatique des trous de données récents (voir le bandeau ci-dessus).
    /// Retourne le nombre de matchs enregistrés.
    /// </summary>
    [AutomaticRetry(Attempts = 0)]
    [DisableConcurrentExecution(timeoutInSeconds: 3600)]
    public async Task<int> ExecuteRecentRepairAsync()
    {
        DateTime now = ParisTime.Now;
        int daysBack = Math.Max(1, _config.GetValue("Repair:DaysBack", 10));
        var options = new SeasonBackfillOptions
        {
            // Matchs jamais enregistrés : toute la saison. Matchs incomplets : DaysBack jours.
            From = SeasonBackfillOptions.CurrentSeasonStart(now),
            // Un match n'est repris que 3 h après son coup d'envoi (terminé, stats publiées).
            To = now.AddHours(-3),
            RepairSince = now.Date.AddDays(-daysBack),
            SkipExisting = true,
            // Enregistre aussi un report ou une annulation découverts en repassant le match.
            IncludeUnfinished = true,
            // 30 clics couvrent une saison entière : la lecture s'arrête dès que le bouton
            // « Afficher plus » disparaît, le coût réel dépend donc du nombre de journées jouées.
            MaxMoreClicks = Math.Max(0, _config.GetValue("Repair:MaxMoreClicks", 30)),
            Limit = Math.Max(0, _config.GetValue("Repair:MaxMatches", 250))
        };
        return await ExecuteSeasonBackfillAsync(options, "Repair");
    }

    /// <summary>
    /// Cœur du rattrapage : phase 1 (collecte des clés sur la page résultats de chaque ligue)
    /// puis phase 2 (détail de chaque match). Remplit <paramref name="stats"/> ; la clôture est
    /// faite par l'appelant. Retourne le nombre de matchs enregistrés.
    /// </summary>
    private async Task<int> BackfillCoreAsync(SeasonBackfillOptions options, List<TargetLeague> leagues, int runId,
        RunStats stats, string baseUrl, bool headless, int recycleEvery)
    {
        var alreadyComplete = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (options.SkipExisting)
        {
            alreadyComplete = await _repository.GetCompletedMatchKeysAsync();
            _logger.LogInformation("{Count} matchs sont déjà complets en base et seront ignorés.", alreadyComplete.Count);
        }

        using var playwright = await Playwright.CreateAsync();
        await using var browser = await playwright.Chromium.LaunchAsync(new BrowserTypeLaunchOptions
        {
            Headless = headless,
            Args = new[] { "--disable-gpu", "--disable-dev-shm-usage", "--no-sandbox" }
        });

        // ---------------------------------------------------------------------
        // PHASE 1 : collecter les clés de match sur la page résultats de chaque ligue
        // ---------------------------------------------------------------------
        var queue = new Dictionary<string, MatchQueueItem>(StringComparer.OrdinalIgnoreCase);

        await using (var listContext = await CreateOptimizedContextAsync(browser))
        {
            var listPage = await listContext.NewPageAsync();

            // Le bandeau cookies n'est accepté qu'une fois : le choix vaut pour tout le contexte.
            await listPage.GotoAsync(baseUrl, new PageGotoOptions { WaitUntil = WaitUntilState.DOMContentLoaded });
            await DismissCookiesIfPresentAsync(listPage);

            foreach (var league in leagues)
            {
                var keys = await CollectLeagueResultKeysAsync(listPage, baseUrl, league, options.MaxMoreClicks);

                int added = 0;
                foreach (var key in keys)
                {
                    if (queue.TryAdd(key, new MatchQueueItem(key, league))) added++;
                }

                if (added != keys.Count)
                {
                    _logger.LogWarning("[{League}] {Dup} matchs étaient déjà rattachés à une autre ligue.",
                        league.LeagueName, keys.Count - added);
                }

                await Task.Delay(Random.Shared.Next(800, 1600));
            }
        }

        var items = queue.Values.ToList();
        _logger.LogInformation("Total collecté : {Total} matchs uniques.", items.Count);

        if (options.RepairSince is { } repairSince)
        {
            items = await SelectRepairItemsAsync(items, alreadyComplete, repairSince, options.To, leagues, runId);
        }
        else if (options.SkipExisting)
        {
            int before = items.Count;
            items = items.Where(i => !alreadyComplete.Contains(i.FlashscoreMatchKey)).ToList();
            _logger.LogInformation(
                "{Skipped} déjà en base, {Remaining} restent à traiter.", before - items.Count, items.Count);
        }

        if (options.Limit > 0 && items.Count > options.Limit)
        {
            _logger.LogInformation("Limite --limit {Limit} : seuls les {Limit} premiers matchs seront traités.",
                options.Limit, options.Limit);
            items = items.Take(options.Limit).ToList();
        }

        if (options.DryRun)
        {
            _logger.LogInformation(
                "Mode simulation (--dry-run) : aucun détail scrapé, aucune écriture en base. {Count} matchs seraient traités.",
                items.Count);
            await _repository.LogExecutionAsync(runId, "INFO", $"{_sector}.DryRun",
                $"Simulation : {items.Count} matchs seraient traités.");
            stats.Note = $"Simulation (--dry-run) : {items.Count} matchs seraient traités, rien n'a été écrit.";
            return 0;
        }

        if (items.Count == 0)
        {
            _logger.LogInformation("Rien à faire : la base est déjà à jour sur cette fenêtre.");
            stats.Note = "Aucun match à traiter : la base est déjà à jour sur cette fenêtre.";
            return 0;
        }

        // ---------------------------------------------------------------------
        // PHASE 2 : scraper le détail de chaque match et l'enregistrer
        // ---------------------------------------------------------------------
        var watch = Stopwatch.StartNew();
        int saved = 0, outOfRange = 0, notFinished = 0, failed = 0;
        stats.Found = items.Count;
        foreach (var queued in items) stats.CountLeague(queued.League.LeagueName, found: 1);
        await _repository.LogExecutionAsync(runId, "INFO", $"{_sector}.Collected",
            $"{items.Count} matchs à traiter : {stats.RenderLeagueFound()}");
        _quality.ExpectOpeningOdds = true;

        IBrowserContext? detailContext = null;
        IPage? detailPage = null;

        try
        {
            for (int i = 0; i < items.Count; i++)
            {
                var item = items[i];

                if (detailContext == null || i % recycleEvery == 0)
                {
                    if (detailContext != null) await detailContext.DisposeAsync();
                    detailContext = await CreateOptimizedContextAsync(browser);
                    detailPage = await detailContext.NewPageAsync();
                }

                try
                {
                    // Rattrapage : cote d'ouverture ET cote finale, en un seul passage.
                    var data = await ScrapeMatchDetailTabsAsync(detailPage!, baseUrl, item, includeOpeningOdds: true);

                    if (data == null)
                    {
                        failed++;
                        stats.CountLeague(item.League.LeagueName, failed: 1);
                    }
                    else if (!data.KickoffParsed)
                    {
                        // Date de coup d'envoi illisible : on refuse d'écrire une date fausse
                        // dans l'historique, qui fausserait tous les calculs glissants.
                        failed++;
                        stats.CountLeague(item.League.LeagueName, failed: 1);
                        _logger.LogWarning(
                            "[{Index}/{Total}] Date de coup d'envoi illisible pour {MatchKey} : match ignoré.",
                            i + 1, items.Count, item.FlashscoreMatchKey);
                        await _repository.LogExecutionAsync(runId, "WARN", $"{_sector}.UnparsedKickoff",
                            "Date de coup d'envoi illisible, match ignoré.", item.FlashscoreMatchKey);
                    }
                    else if (data.MatchDate < options.From || data.MatchDate > options.To)
                    {
                        outOfRange++;
                    }
                    else if (!PersistableStatuses.Contains(data.MatchStatus))
                    {
                        // Jamais enregistré, même avec --include-unfinished : statut refusé
                        // par la contrainte en base (match en cours ou libellé illisible).
                        failed++;
                        stats.CountLeague(item.League.LeagueName, failed: 1);
                        _logger.LogWarning(
                            "[{Index}/{Total}] Statut non enregistrable ({Status}, « {Raw} ») pour {MatchKey} : match ignoré.",
                            i + 1, items.Count, data.MatchStatus, data.RawStatusText, item.FlashscoreMatchKey);
                    }
                    else if (!options.IncludeUnfinished && data.MatchStatus != "Finished")
                    {
                        notFinished++;
                    }
                    else
                    {
                        await _repository.SaveFullMatchDataAsync(data);
                        _quality.Record(data);
                        saved++;
                        stats.CountLeague(item.League.LeagueName, saved: 1);

                        string message =
                            $"{data.MatchDate:yyyy-MM-dd} {data.HomeTeamName} {data.HomeScoreFT ?? 0}-{data.AwayScoreFT ?? 0} {data.AwayTeamName} " +
                            $"| xG: [{data.HomeStats?.ExpectedGoals} / {data.AwayStats?.ExpectedGoals}] " +
                            $"| 1X2 {data.Odds?.SourceBookmaker}: [{data.Odds?.Opening?.OddHome}→{data.Odds?.OddHome} / {data.Odds?.Opening?.OddDraw}→{data.Odds?.OddDraw} / {data.Odds?.Opening?.OddAway}→{data.Odds?.OddAway}] " +
                            $"| O/U 2.5: [{data.Odds?.OddOv25} / {data.Odds?.OddUn25}] | BTTS: [{data.Odds?.OddBTTS} / {data.Odds?.OddBTNS}]";

                        _logger.LogInformation("[{Index}/{Total}] {Eta} ✅ {Message}",
                            i + 1, items.Count, FormatEta(watch.Elapsed, i + 1, items.Count), message);

                        await _repository.LogExecutionAsync(runId, "INFO", $"{_sector}.MatchProcessed",
                            message, item.FlashscoreMatchKey);
                    }
                }
                catch (Exception ex)
                {
                    failed++;
                    stats.CountLeague(item.League.LeagueName, failed: 1);
                    _logger.LogError(ex, "[{Index}/{Total}] Échec sur le match {MatchKey}",
                        i + 1, items.Count, item.FlashscoreMatchKey);
                    await _repository.LogExecutionAsync(runId, "ERROR", $"{_sector}.MatchError",
                        $"Échec du rattrapage pour le match {item.FlashscoreMatchKey}",
                        item.FlashscoreMatchKey, ex.ToString());
                }

                await Task.Delay(Random.Shared.Next(options.DelayMsMin, options.DelayMsMax));
            }
        }
        finally
        {
            // Plantage : remonté à ExecuteSeasonBackfillAsync, qui l'écrit (FATAL) et clôture le run.
            if (detailContext != null) await detailContext.DisposeAsync();
            stats.Saved = saved;
            stats.OutOfRange = outOfRange;
            stats.Skipped = notFinished;
            stats.Failed = failed;
        }

        return saved;
    }

    /// <summary>
    /// Rattrapage automatique : garde, parmi les matchs lus sur les pages « résultats », ceux
    /// qui n'ont jamais été enregistrés, plus ceux qui sont incomplets en base et joués depuis
    /// <paramref name="since"/> ; ajoute les matchs incomplets en base absents des pages
    /// « résultats » (la collecte d'une ligue a pu échouer). Écrit la liste dans le journal.
    /// </summary>
    private async Task<List<MatchQueueItem>> SelectRepairItemsAsync(List<MatchQueueItem> collected,
        HashSet<string> complete, DateTime since, DateTime to, List<TargetLeague> leagues, int runId)
    {
        var known = await _repository.GetKnownMatchDatesAsync();
        var candidates = await _repository.GetRepairCandidatesAsync(since, to);
        var reasons = candidates.ToDictionary(c => c.Key, c => c.Reason ?? "", StringComparer.OrdinalIgnoreCase);

        var selected = new Dictionary<string, MatchQueueItem>(StringComparer.OrdinalIgnoreCase);
        int neverSaved = 0, incomplete = 0, fromDatabase = 0;

        foreach (var item in collected)
        {
            string key = item.FlashscoreMatchKey;
            if (!known.ContainsKey(key))
            {
                selected[key] = item;          // trou : match jamais enregistré
                neverSaved++;
            }
            else if (!complete.Contains(key) && reasons.ContainsKey(key))
            {
                selected[key] = item;          // en base mais incomplet, et récent
                incomplete++;
            }
        }

        foreach (var c in candidates)
        {
            if (selected.ContainsKey(c.Key)) continue;
            var league = leagues.FirstOrDefault(l => string.Equals(l.LeagueName, c.LeagueName, StringComparison.OrdinalIgnoreCase));
            if (league == null) continue;      // ligue exclue par le filtre
            selected[c.Key] = new MatchQueueItem(c.Key, league);
            fromDatabase++;
        }

        string detail = string.Join(" ; ", candidates.Where(c => selected.ContainsKey(c.Key)).Take(60)
            .Select(c => $"{c.MatchDate:dd/MM} {c.LeagueName} {c.Key} ({c.Reason})"));
        string summary =
            $"À reprendre : {selected.Count} matchs — {neverSaved} jamais enregistrés, " +
            $"{incomplete + fromDatabase} incomplets depuis le {since:dd/MM}" +
            (fromDatabase > 0 ? $" (dont {fromDatabase} absents des pages résultats)" : "") + ".";
        _logger.LogInformation("{Summary} {Detail}", summary, detail);
        await _repository.LogExecutionAsync(runId, "INFO", $"{_sector}.Selection",
            string.IsNullOrEmpty(detail) ? summary : $"{summary} Incomplets : {detail}");

        return selected.Values.ToList();
    }

    /// <summary>Ligues à traiter : toutes, ou celles citées dans --leagues (nom ou alias).</summary>
    private static List<TargetLeague> ResolveLeagues(string[]? filter)
    {
        if (filter is null || filter.Length == 0) return AllowedLeagues.ToList();

        return AllowedLeagues
            .Where(l => filter.Any(f =>
                l.LeagueName.Equals(f.Trim(), StringComparison.OrdinalIgnoreCase) ||
                l.DisplayAliases.Any(a => a.Equals(f.Trim(), StringComparison.OrdinalIgnoreCase))))
            .ToList();
    }

    /// <summary>Estimation du temps restant, affichée dans les logs du rattrapage.</summary>
    private static string FormatEta(TimeSpan elapsed, int done, int total)
    {
        if (done <= 0) return "--:--";
        double perItem = elapsed.TotalSeconds / done;
        var remaining = TimeSpan.FromSeconds(perItem * (total - done));
        return $"reste ~{remaining:hh\\:mm\\:ss}";
    }

    /// <summary>
    /// Ouvre la page "résultats" d'une ligue et clique sur "Afficher plus de matchs"
    /// jusqu'à ce que toute la saison soit chargée, puis retourne les clés de match.
    /// </summary>
    private async Task<List<string>> CollectLeagueResultKeysAsync(
        IPage page, string baseUrl, TargetLeague league, int maxMoreClicks)
    {
        string url = $"{baseUrl}{league.UrlSlug}resultats/";
        _logger.LogInformation("[{League}] Collecte sur {Url}", league.LeagueName, url);

        await page.GotoAsync(url, new PageGotoOptions { WaitUntil = WaitUntilState.DOMContentLoaded });

        try
        {
            await page.WaitForSelectorAsync("div.event__match[id^='g_1_']",
                new PageWaitForSelectorOptions { Timeout = 15000 });
        }
        catch (TimeoutException)
        {
            _logger.LogWarning(
                "[{League}] Aucun match sur {Url}. Slug modifié par Flashscore, ou ligue hors saison.",
                league.LeagueName, url);
            return new List<string>();
        }

        const string countScript =
            @"() => Array.from(document.querySelectorAll('div.event__match'))
                    .filter(e => e.id && e.id.startsWith('g_1_')).length";

        // Le bouton est cherché DANS le conteneur des matchs, et par sa classe avant son
        // libellé. Chercher dans tout le document faisait cliquer sur un lien "afficher plus"
        // de la barre latérale, qui n'ajoute aucun match.
        // Bouton de bas de liste cherché dans TOUTE la page, mais par sa formulation exacte :
        // « Afficher plus de matchs ». Le restreindre au conteneur des matchs l'excluait (il est
        // placé en dehors) : 0 clic, et les ligues longues étaient tronquées (Championship sans
        // ses matchs d'août). Les liens « Afficher plus » de la barre latérale ne contiennent pas
        // « matchs », ils ne peuvent donc pas être pris pour lui.
        // Novembre 2026 : Flashscore a renommé le bouton « Montrer plus de matchs » (bouton
        // data-testid="wcl-buttonLink" dans le pied de section data-testid="wcl-section-footer").
        // L'ancien libellé n'étant plus reconnu, la collecte s'arrêtait à 0 clic (Championship
        // et LaLiga2 privées de leurs journées d'août). Ordre de recherche : pied de section,
        // puis libellé (afficher / voir / montrer), puis ancienne classe event__more.
        const string moreButtonFinder =
            @"const re = /(afficher|voir|montrer)\s+plus\s+de\s+matchs|show\s+more\s+matches/i;
              const visible = n => n.offsetParent !== null;
              const footer = Array.from(document.querySelectorAll('[data-testid=wcl-section-footer] button, [data-testid=wcl-section-footer] a'))
                  .filter(n => visible(n) && re.test((n.innerText || '').replace(/\s+/g, ' ').trim()));
              const found = Array.from(document.querySelectorAll('a, button, [role=button], [data-testid*=button], [data-testid*=Button]'))
                  .filter(n => visible(n) && re.test((n.innerText || '').replace(/\s+/g, ' ').trim()));
              const legacy = Array.from(document.querySelectorAll('a.event__more, .event__more')).filter(visible);
              const candidates = footer.length ? footer : found.length ? found : legacy;";

        const string clickMoreScript =
            @"() => { " + moreButtonFinder + @"
                if (!candidates.length) return false;
                const btn = candidates[candidates.length - 1];
                btn.scrollIntoView({ block: 'center' });
                btn.click();
                return true;
            }";

        const string morePresentScript =
            @"() => { " + moreButtonFinder + @" return candidates.length > 0; }";

        // On attend que de NOUVEAUX matchs soient réellement dans le DOM, au lieu d'une pause
        // fixe : une page lente dispose de tout le délai, une page rapide enchaîne aussitôt.
        const string growthScript =
            @"expected => Array.from(document.querySelectorAll('div.event__match'))
                          .filter(e => e.id && e.id.startsWith('g_1_')).length >= expected";

        const string extractScript =
            @"() => Array.from(document.querySelectorAll('div.event__match'))
                    .filter(e => e.id && e.id.startsWith('g_1_'))
                    .map(e => e.id.substring(4))";

        // Les clés sont accumulées AVANT chaque clic : si un clic recharge la page, change
        // d'URL ou vide la liste, ce qui a déjà été vu n'est jamais perdu.
        var collected = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        async Task HarvestAsync()
        {
            var visible = await page.EvaluateAsync<string[]>(extractScript);
            if (visible != null) collected.UnionWith(visible);
        }

        string startUrl = page.Url;
        int count = await page.EvaluateAsync<int>(countScript);
        int clicksUsed = 0;

        while (clicksUsed < maxMoreClicks)
        {
            await HarvestAsync();

            if (!await page.EvaluateAsync<bool>(clickMoreScript)) break;
            clicksUsed++;

            try
            {
                await page.WaitForFunctionAsync(growthScript, count + 1,
                    new PageWaitForFunctionOptions { Timeout = 6000 });
            }
            catch (TimeoutException)
            {
                // Le bouton répond encore mais plus rien ne se charge : fin de la liste.
                break;
            }
            catch (PlaywrightException ex)
            {
                // Le clic a provoqué une navigation : on garde ce qui a été récolté.
                _logger.LogWarning("[{League}] Le clic « afficher plus » a quitté la page ({Message}).",
                    league.LeagueName, ex.Message);
                break;
            }

            if (!string.Equals(page.Url.Split('#')[0], startUrl.Split('#')[0], StringComparison.OrdinalIgnoreCase))
            {
                _logger.LogWarning("[{League}] Le clic a redirigé vers {Url} : collecte arrêtée.",
                    league.LeagueName, page.Url);
                break;
            }

            count = await page.EvaluateAsync<int>(countScript);
        }

        // Dernière récolte, seulement si l'on est toujours sur la page de résultats.
        if (string.Equals(page.Url.Split('#')[0], startUrl.Split('#')[0], StringComparison.OrdinalIgnoreCase))
        {
            await HarvestAsync();
        }

        var result = collected.ToList();

        // Le bouton est-il encore là ? Si oui, la liste n'a pas été entièrement chargée :
        // c'est le seul signal d'une saison tronquée, le contrôle qualité ne le voit pas.
        bool moreStillPresent = false;
        try { moreStillPresent = await page.EvaluateAsync<bool>(morePresentScript); }
        catch (PlaywrightException) { }

        if (moreStillPresent && clicksUsed < maxMoreClicks)
        {
            _logger.LogWarning(
                "[{League}] {Count} matchs après {Clicks} clic(s), mais le bouton « Afficher plus de matchs » " +
                "est toujours affiché : la liste est probablement INCOMPLÈTE.",
                league.LeagueName, result.Count, clicksUsed);
        }
        else if (clicksUsed >= maxMoreClicks)
        {
            _logger.LogWarning(
                "[{League}] {Count} matchs après {Clicks} clic(s) : PLAFOND ATTEINT, la liste est " +
                "probablement incomplète. Relancez avec --max-more-clicks plus élevé.",
                league.LeagueName, result.Count, clicksUsed);
        }
        else
        {
            _logger.LogInformation("[{League}] {Count} matchs après {Clicks} clic(s).",
                league.LeagueName, result.Count, clicksUsed);
        }

        return result;
    }

    // ===== 4. COLLECTE DES MATCHS DU JOUR (ACCUEIL FLASHSCORE) ===================
    /// <summary>
    /// 3. DÉFILEMENT ET DÉPLIAGE INTELLIGENT DES 10 LIGUES :
    /// Fait défiler la page pas à pas et déplie UNIQUEMENT les ligues cibles qui sont fermées.
    /// </summary>
    private async Task ScrollAndExpandAllMatchesAsync(IPage page)
    {
        var targetSlugs = AllowedLeagues.Select(l => l.UrlSlug.ToLowerInvariant()).ToArray();
        var targetCountries = AllowedLeagues.Select(l => l.CountryName.ToLowerInvariant()).Distinct().ToArray();
        var targetNames = AllowedLeagues.SelectMany(l => l.DisplayAliases).Select(a => a.ToLowerInvariant()).Distinct().ToArray();

        // 1. Défilement progressif (par paliers de 800px) pour forcer le Lazy-Loading de Flashscore à charger tout le DOM
        await page.EvaluateAsync(@"async () => {
            await new Promise((resolve) => {
                let totalHeight = 0;
                const distance = 800;
                const timer = setInterval(() => {
                    const scrollHeight = document.body.scrollHeight;
                    window.scrollBy(0, distance);
                    totalHeight += distance;

                    if (totalHeight >= scrollHeight || totalHeight > 15000) {
                        clearInterval(timer);
                        window.scrollTo(0, 0);
                        resolve();
                    }
                }, 150);
            });
        }");

        await Task.Delay(800);

        // 2. Dépliage chirurgical : ouvre uniquement nos 10 ligues si et seulement si aucun event__match ne les suit
        int expandedLeagues = await page.EvaluateAsync<int>(@"([slugs, countries, names]) => {
            let openedCount = 0;
            const container = document.querySelector('.sportName.soccer');
            if (!container) return 0;

            // Trouve tous les blocs d'en-tête de ligue sur la page
            const headers = container.querySelectorAll('.headerLeague__wrapper, .event__header, [data-testid=""wcl-headerLeague""]');

            for (const header of headers) {
                const text = header.innerText.toLowerCase();
                const linkEl = header.querySelector('a[href]');
                const href = linkEl ? (linkEl.getAttribute('href') || '').toLowerCase() : '';

                // Vérifie si cet en-tête appartient à l'une de nos 10 ligues
                const matchBySlug = href && slugs.some(s => href.includes(s));
                const matchByText = countries.some(c => text.includes(c)) && names.some(n => text.includes(n));

                if (!matchBySlug && !matchByText) continue;

                // Remonte au conteneur direct qui est frère des div.event__match dans .sportName.soccer
                let topNode = header;
                while (topNode.parentElement && topNode.parentElement !== container) {
                    topNode = topNode.parentElement;
                }

                // Vérifie si l'élément suivant est DÉJÀ un match (si oui, la ligue est déjà ouverte, on n'y touche surtout pas !)
                const nextSibling = topNode.nextElementSibling;
                const isAlreadyOpen = nextSibling && nextSibling.classList.contains('event__match');

                if (!isAlreadyOpen) {
                    // La ligue est fermée (""Afficher les matchs"") : on clique en JS sur le bouton d'accordéon
                    const trigger = topNode.querySelector('.headerLeague__actions button, [data-testid*=""accordion""], [class*=""expander""], button');
                    if (trigger)
                    {
                        trigger.click();
                        openedCount++;
                    }
                }
            }
            return openedCount;
        }", new object[] { targetSlugs, targetCountries, targetNames });

        if (expandedLeagues > 0)
        {
            _logger.LogInformation("{Count} ligues cibles étaient masquées et ont été dépliées.", expandedLeagues);
            await Task.Delay(1200); // Laisse le temps à Flashscore d'injecter les matchs dépliés dans le DOM
        }
    }

    /// <summary>
    /// 4. EXTRACTION UNIVERSELLE DES MATCHS DU JOUR :
    /// Associe chaque div.event__match à son en-tête de ligue, quelle que soit la version du DOM Flashscore.
    /// </summary>
    private async Task<List<MatchQueueItem>> ExtractFilteredMatchesForDayAsync(IPage page)
{
    var rawLines = await page.EvaluateAsync<string[]>(@"() => {
            const results = [];
            const container = document.querySelector('.sportName.soccer');
            if (!container) return results;

            let currentCountry = '';
            let currentLeagueTitle = '';
            let currentHref = '';

            // On parcourt les enfants directs de .sportName.soccer de haut en bas
            for (const child of container.children) {
                // Cas A : L'enfant est un match direct (ou contient des matchs)
                if (child.classList.contains('event__match') && child.id && child.id.startsWith('g_1_')) {
                    const matchKey = child.id.substring(4);
                    results.push(`${matchKey}|${currentCountry}|${currentLeagueTitle}|${currentHref}`);
                    continue;
                }

                // Cas B : C'est un en-tête de ligue (nouveau design .headerLeague__wrapper ou ancien .event__header)
                const linkEl = child.querySelector('a[href*=""/football/""], a.headerLeague__title');
                const countryEl = child.querySelector('.headerLeague__category-text, .event__title--type, [data-testid=""wcl-scores-overline-03""]');
                const titleEl = child.querySelector('.headerLeague__title-text, .event__title--name');

                if (linkEl || countryEl || titleEl) {
                    currentHref = linkEl ? (linkEl.getAttribute('href') || '').toLowerCase() : '';
                    currentCountry = countryEl ? countryEl.innerText.trim().replace(':', '') : '';
                    currentLeagueTitle = titleEl ? titleEl.innerText.trim() : (linkEl ? linkEl.innerText.trim() : '');
                }

                // Si jamais des matchs sont imbriqués à l'intérieur d'un sous-conteneur
                const nestedMatches = child.querySelectorAll('div.event__match[id^=""g_1_""]');
                for (const m of nestedMatches) {
                    const matchKey = m.id.substring(4);
                    results.push(`${matchKey}|${currentCountry}|${currentLeagueTitle}|${currentHref}`);
                }
            }
            return results;
        }");

    var filtered = new List<MatchQueueItem>();
    if (rawLines == null) return filtered;

    foreach (var line in rawLines)
    {
        var parts = line.Split('|');
        if (parts.Length < 4) continue;

        string matchKey = parts[0];
        string country = parts[1];
        string leagueTitle = parts[2];
        string href = parts[3];

        var matchedLeague = MatchToTargetLeague(href, country, leagueTitle);
        if (matchedLeague != null)
        {
            filtered.Add(new MatchQueueItem(matchKey, matchedLeague));
        }
    }

    return filtered.DistinctBy(x => x.FlashscoreMatchKey).ToList();
}

/// <summary>
/// Reconnaît la ligue d'un bloc de l'accueil : d'abord par l'adresse du lien, sinon par le
/// pays + le nom affiché. Renvoie null pour une compétition non suivie.
/// </summary>
private static TargetLeague? MatchToTargetLeague(string href, string country, string leagueTitle)
    {
        if (!string.IsNullOrEmpty(href))
        {
            foreach (var target in AllowedLeagues)
            {
                if (href.Contains(target.UrlSlug, StringComparison.OrdinalIgnoreCase))
                    return target;
            }
        }

        string cleanCountry = country.Trim();
        string cleanTitle = leagueTitle.Trim().ToLowerInvariant();

        foreach (var target in AllowedLeagues)
        {
            if (cleanCountry.Equals(target.CountryName, StringComparison.OrdinalIgnoreCase) &&
                target.DisplayAliases.Any(alias => cleanTitle.Equals(alias, StringComparison.OrdinalIgnoreCase)))
            {
                return target;
            }
        }

        return null;
    }

    // ===== 5. PAGE DÉTAIL D'UN MATCH =============================================
    // Ordre de lecture : résumé (équipes, date, statut, journée, score mi-temps)
    // -> statistiques -> cotes (1X2, Plus/Moins, BTTS, Double chance, DNB).
    /// <param name="includeOpeningOdds">
    /// true pour le rattrapage historique : la cote d'ouverture est lue dans l'attribut title et
    /// enregistrée avec la cote finale. false pour le service journalier : seule la cote actuelle
    /// est retenue, l'évolution étant construite par les relevés successifs J-3, J-2, J-1…
    /// </param>
    private async Task<ScrapedMatchData?> ScrapeMatchDetailTabsAsync(IPage page, string baseUrl, MatchQueueItem item, bool includeOpeningOdds)
    {
        string matchKey = item.FlashscoreMatchKey;
        var data = new ScrapedMatchData
        {
            FlashscoreMatchKey = matchKey,
            CountryName = item.League.CountryName,
            LeagueName = item.League.LeagueName,
            TierLevel = item.League.TierLevel
        };

        // 1. RÉSUMÉ DU MATCH
        // L'ancienne adresse sans ancre redirige vers la nouvelle page du match. Les anciennes
        // ancres "#/comparaison-des-cotes/..." retombent sur ce résumé : ne plus les utiliser.
        string summaryUrl = $"{baseUrl}/match/{matchKey}/";
        await page.GotoAsync(summaryUrl, new PageGotoOptions { WaitUntil = WaitUntilState.DOMContentLoaded });
        await page.WaitForSelectorAsync(".duelParticipant", new PageWaitForSelectorOptions { Timeout = 10000 });

        // Bandeau cookies : refus des traceurs non essentiels, immédiat s'il est absent.
        // Les clics se font en JavaScript, le bandeau ne peut donc pas les intercepter.
        await page.EvaluateAsync("() => document.querySelector('#onetrust-reject-all-handler')?.click()");

        var routes = await ResolveMatchRoutesAsync(page, matchKey);

        data.HomeTeamName = (await page.Locator(".duelParticipant__home .participant__participantName").First.InnerTextAsync()).Trim();
        data.AwayTeamName = (await page.Locator(".duelParticipant__away .participant__participantName").First.InnerTextAsync()).Trim();
        data.HomeTeamKey = await ExtractTeamKeyFromHrefAsync(page, ".duelParticipant__home a.participant__participantLink");
        data.AwayTeamKey = await ExtractTeamKeyFromHrefAsync(page, ".duelParticipant__away a.participant__participantLink");

        var breadCrumbLocator = page.Locator(".tournamentHeader__country, [data-testid='wcl-scores-overline-03']");
        if (await breadCrumbLocator.CountAsync() > 0)
        {
            string headerText = (await breadCrumbLocator.Last.InnerTextAsync()).Trim();
            if (headerText.Contains('-'))
            {
                data.RoundName = headerText.Split('-').Last().Trim();
                var numMatch = Regex.Match(data.RoundName, @"\d+");
                if (numMatch.Success && int.TryParse(numMatch.Value, out int rNum))
                {
                    data.RoundNumber = rNum;
                }
            }
        }

        string startTimeText = await page.Locator(".duelParticipant__startTime").First.InnerTextAsync();
        // Le contexte navigateur est forcé sur Europe/Paris (CreateOptimizedContextAsync) :
        // l'heure affichée par Flashscore EST l'heure de Paris, on la stocke telle quelle.
        // L'ancienne conversion ToUniversalTime() décalait chaque match d'1 h (hiver) ou 2 h (été).
        if (DateTime.TryParseExact(startTimeText.Trim(), "dd.MM.yyyy HH:mm", CultureInfo.InvariantCulture, DateTimeStyles.None, out var kickoffParis))
        {
            data.MatchDate = DateTime.SpecifyKind(kickoffParis, DateTimeKind.Unspecified);
            data.KickoffParsed = true;
        }
        else
        {
            // Valeur de repli jamais écrite en base (protégée par KickoffParsed).
            data.MatchDate = ParisTime.Now;
            data.KickoffParsed = false;
        }

        // Le statut est lu dans l'en-tête du score, avec repli sur l'en-tête flottant. Ce dernier
        // n'est rendu qu'au défilement : c'est l'origine des statuts vides vus en rattrapage.
        const string statusScript =
            @"() => {
                for (const s of ['.detailScore__status', '.fixedHeaderDuel__detailStatus']) {
                    const e = document.querySelector(s);
                    if (e && e.innerText.trim()) return e.innerText.trim();
                }
                return '';
            }";

        // Pour un match déjà commencé, on laisse au statut le temps d'apparaître.
        // Pas d'attente pour un match à venir : il n'affiche pas de statut.
        if (data.KickoffParsed && data.MatchDate <= ParisTime.Now)
        {
            try
            {
                await page.WaitForFunctionAsync(statusScript, null, new PageWaitForFunctionOptions { Timeout = 4000 });
            }
            catch (TimeoutException) { }
        }

        string statusText = ((await page.EvaluateAsync<string>(statusScript)) ?? "").Trim();

        data.RawStatusText = statusText;
        data.MatchStatus = MapMatchStatus(statusText);

        // Un match à venir n'affiche aucun libellé de statut : le coup d'envoi tranche.
        if (data.MatchStatus == "Unknown" && data.KickoffParsed && data.MatchDate > ParisTime.Now.AddMinutes(5))
        {
            data.MatchStatus = "Scheduled";
        }

        if (data.MatchStatus == "Unknown")
        {
            // Statut illisible : on ne déduit AUCUN score. Écrire "Scheduled" ici ferait
            // régresser en base un match déjà enregistré comme terminé.
            _logger.LogWarning("Statut illisible (« {Raw} ») pour le match {MatchKey}.", statusText, matchKey);
            await SaveDiagnosticAsync(page, matchKey, "statut");

            // Inutile de scraper stats et cotes : ce match ne sera pas enregistré.
            return data;
        }

        // Les scores ne sont lus que pour un match réellement terminé : un match interrompu
        // ou en cours affiche un score partiel qui n'a pas de sens en historique.
        if (data.MatchStatus == "Finished")
        {
            var scores = await page.Locator(".detailScore__wrapper").First.Locator("span:not(.detailScore__divider)").AllInnerTextsAsync();
            if (scores.Count >= 2 && int.TryParse(scores[0], out int hFt) && int.TryParse(scores[1], out int aFt))
            {
                data.HomeScoreFT = hFt;
                data.AwayScoreFT = aFt;
            }
            else
            {
                _logger.LogWarning("Match {MatchKey} annoncé terminé mais score illisible.", matchKey);
            }

            await ExtractHalfTimeScoreAsync(page, data);
        }

        // 2. STATISTIQUES (Si terminé)
        if (data.MatchStatus == "Finished")
        {
            await page.GotoAsync(routes.StatsUrl, new PageGotoOptions { WaitUntil = WaitUntilState.DOMContentLoaded });

            await ExtractMatchStatsAsync(page, matchKey, data);
        }

        // 3. COTES
        data.Odds = await ScrapeAllOddsMarketsAsync(page, routes, matchKey, includeOpeningOdds);

        if (data.MatchStatus == "Finished" && data.Odds?.HasAnyOdd != true)
        {
            _logger.LogWarning("Aucune cote lisible pour le match terminé {MatchKey}.", matchKey);
            await SaveDiagnosticAsync(page, matchKey, "cotes");
        }

        return data;
    }

    // =========================================================================
    // ===== 6. COTES — NAVIGATION DANS LA PAGE D'UN MATCH =====
    // Flashscore a abandonné le format "/match/{id}/#/comparaison-des-cotes/..." : ces
    // ancres retombent sur la page résumé. Le scraper lisait alors l'encart de cotes du
    // résumé (toujours le 1X2) à la place du marché demandé : BTTS, Double chance et DNB
    // recevaient des cotes 1X2, et Plus/Moins restait vide. Les sous-pages ont désormais
    // une vraie adresse, et leurs identifiants sont lus dans la page elle-même pour suivre
    // automatiquement un futur renommage.
    // =========================================================================

    /// <summary>Adresses des sous-pages d'un match (statistiques, cotes par marché).</summary>
    private sealed class MatchRoutes
    {
        public required string Base { get; init; }
        public required string Mid { get; init; }
        public required string StatsUrl { get; init; }
        public required string OddsSegment { get; init; }
        public required string ScopeFullTime { get; init; }
        public required string Market1X2 { get; init; }
        public required string MarketOverUnder { get; init; }
        public required string MarketBtts { get; init; }
        public required string MarketDoubleChance { get; init; }
        public required string MarketDrawNoBet { get; init; }

        public string OddsUrl(string market) => $"{Base}{OddsSegment}/{market}/{ScopeFullTime}/?mid={Mid}";
    }

    /// <summary>
    /// Lit dans la page résumé la table de routage de Flashscore (identifiants des sous-pages)
    /// pour construire les adresses ; valeurs par défaut si elle est introuvable.
    /// </summary>
    private async Task<MatchRoutes> ResolveMatchRoutesAsync(IPage page, string matchKey)
    {
        var raw = await page.EvaluateAsync<Dictionary<string, string?>>("""
            (key) => {
                const html = document.documentElement.innerHTML;
                const ident = k => {
                    const m = html.match(new RegExp('"TRANS_DETAIL_BOOKMARK_URL_IDENT_' + k + '":"([^"]+)"'));
                    return m ? m[1] : null;
                };
                const canon = document.querySelector('link[rel="canonical"]');
                const statsLink = Array.from(document.querySelectorAll('a[href*="/stats"]'))
                    .map(a => a.href)
                    .find(h => h.includes('mid=' + key) && !h.includes('stats-joueurs'));
                return {
                    base: canon ? canon.href : null,
                    statsLink: statsLink || null,
                    summary: ident('SUMMARY'), stats: ident('STATISTICS'), odds: ident('ODDS_COMPARISON'),
                    scope: ident('COMPARISON_SCOPE_FT'), m1x2: ident('COMPARISON_1X2'),
                    ou: ident('COMPARISON_OVER_UNDER'), btts: ident('COMPARISON_BOTH_TEAMS_TO_SCORE'),
                    dc: ident('COMPARISON_DOUBLE_CHANCE'), dnb: ident('COMPARISON_DRAW_NO_BET')
                };
            }
            """, matchKey) ?? new();

        string Get(string k, string fallback) =>
            raw.TryGetValue(k, out var v) && !string.IsNullOrWhiteSpace(v) ? v! : fallback;

        // Valeurs de repli : celles relevées le 30/10/2026 dans la table de routage Flashscore.
        string basePath = Get("base", page.Url.Split('#')[0].Split('?')[0]);
        if (!basePath.EndsWith('/')) basePath += "/";

        if (!raw.TryGetValue("m1x2", out var probe) || string.IsNullOrEmpty(probe))
        {
            _logger.LogWarning("Table de routage Flashscore introuvable pour {MatchKey} : identifiants par défaut utilisés.", matchKey);
        }

        return new MatchRoutes
        {
            Base = basePath,
            Mid = matchKey,
            StatsUrl = Get("statsLink", $"{basePath}{Get("summary", "resume")}/{Get("stats", "stats")}/?mid={matchKey}"),
            OddsSegment = Get("odds", "cotes"),
            ScopeFullTime = Get("scope", "temps-regulier"),
            Market1X2 = Get("m1x2", "cotes-1x2"),
            MarketOverUnder = Get("ou", "plus-moins"),
            MarketBtts = Get("btts", "les-deux-marquent"),
            MarketDoubleChance = Get("dc", "chance-double"),
            MarketDrawNoBet = Get("dnb", "draw-no-bet"),
        };
    }

    // Cellules de cotes identifiées par leurs attributs analytics, repris de l'ancienne
    // application : bien plus stables que les classes CSS, et elles portent dans leur
    // attribut title la cote d'ouverture et la cote actuelle (« 2.10 » 1.95 »).
    // Flashscore a corrigé sa faute de frappe : ODDS_COMPARIONS_… est devenu ODDS_COMPARISONS_….
    // C'est ce qui a cassé l'ancienne application. On ne s'appuie plus que sur la fin stable
    // de l'attribut (« _ODD_CELL_n »), valable pour les deux orthographes.
    private const string OddsCellSelector = "[data-analytics-element*='_ODD_CELL_'], [data-testid*='oddsValue'], .oddsCell__odd";

    // Une cellule de cote : ouverture et actuelle. Une ligne : ligne de pari (ex. 2.5), 2 ou 3
    // cellules, identifiant et nom du bookmaker.
    private sealed record OddCell(decimal? Opening, decimal? Current);
    private sealed record OddsRow(string Line, OddCell C1, OddCell C2, OddCell C3, string Bookmaker, string BookmakerName);

    /// <summary>
    /// Ouvre la page d'un marché par son adresse et vérifie qu'on y est réellement. Si Flashscore
    /// redirige vers un autre marché ou vers le résumé, on renonce : mieux vaut une cote absente,
    /// visible dans le contrôle qualité, qu'une cote d'un autre marché enregistrée en silence.
    /// </summary>
    private async Task<bool> OpenOddsMarketAsync(IPage page, MatchRoutes routes, string market, string matchKey)
    {
        await page.GotoAsync(routes.OddsUrl(market), new PageGotoOptions { WaitUntil = WaitUntilState.DOMContentLoaded });

        try
        {
            await page.WaitForSelectorAsync(OddsCellSelector, new PageWaitForSelectorOptions { Timeout = 6000 });
        }
        catch (TimeoutException)
        {
            return false; // marché listé mais sans cote : fréquent sur les petits marchés
        }

        // Vérification APRÈS chargement : une redirection côté client a eu le temps de se produire.
        string url = page.Url;
        bool onOddsPage = url.Contains($"/{routes.OddsSegment}/", StringComparison.OrdinalIgnoreCase);
        bool onMarket = url.Contains($"/{market}/", StringComparison.OrdinalIgnoreCase)
                        || (market == routes.Market1X2 && onOddsPage);

        if (!onOddsPage || !onMarket)
        {
            _logger.LogWarning("Marché « {Market} » inaccessible pour {MatchKey} : redirigé vers {Url}.", market, matchKey, url);
            return false;
        }

        return true;
    }

    /// <summary>
    /// Change d'onglet de cotes par un clic, comme l'ancienne application, puis attend que le
    /// CONTENU du tableau change. L'ancienne attente (présence de ODD_CELL_2) était satisfaite
    /// immédiatement par le tableau précédent : on pouvait lire l'onglet d'avant. Si le clic
    /// échoue ou ne change rien, repli sur l'adresse directe du marché.
    /// </summary>
    private async Task<bool> SwitchOddsTabAsync(IPage page, MatchRoutes routes, string alias, string urlName, string matchKey)
    {
        const string fingerprint = """
            () => Array.from(document.querySelectorAll('[data-analytics-element*="_ODD_CELL_"]'))
                    .slice(0, 9).map(e => (e.innerText || '').trim() + '|' + (e.getAttribute('title') || '')).join(';')
            """;

        string before = await page.EvaluateAsync<string>(fingerprint) ?? "";

        // Clic en JavaScript : ni le bandeau cookies ni une publicité ne peuvent l'intercepter.
        bool clicked = await page.EvaluateAsync<bool>("""
            (alias) => {
                const tab = document.querySelector('[data-analytics-alias="' + alias + '"]');
                if (!tab) return false;
                tab.scrollIntoView({ block: 'center' });
                tab.click();
                return true;
            }
            """, alias);

        if (clicked)
        {
            try
            {
                await page.WaitForFunctionAsync("""
                    (before) => {
                        const cells = document.querySelectorAll('[data-analytics-element*="_ODD_CELL_"]');
                        if (!cells.length) return false;
                        const now = Array.from(cells).slice(0, 9)
                            .map(e => (e.innerText || '').trim() + '|' + (e.getAttribute('title') || '')).join(';');
                        return now !== before;
                    }
                    """, before, new PageWaitForFunctionOptions { Timeout = 6000 });
                return true;
            }
            catch (TimeoutException)
            {
                _logger.LogDebug("Onglet « {Alias} » sans changement après clic pour {MatchKey} : repli par adresse.", alias, matchKey);
            }
        }

        return await OpenOddsMarketAsync(page, routes, urlName, matchKey);
    }

    /// <summary>
    /// Lit les lignes du tableau de cotes : libellé de ligne (1.5, 2.5…) et cellules 1 à 3,
    /// chacune avec sa cote d'ouverture et sa cote actuelle.
    /// </summary>
    private static async Task<List<OddsRow>> ReadOddsRowsAsync(IPage page)
    {
        var raw = await page.EvaluateAsync<string[][]>("""
            () => {
                const ANY = '[data-analytics-element*="_ODD_CELL_"]';
                const CELL = n => '[data-analytics-element$="_ODD_CELL_' + n + '"]';

                let rows = Array.from(document.querySelectorAll('.ui-table__row')).filter(r => r.querySelector(ANY));
                if (!rows.length) {
                    // Repli si la classe de ligne change : plus petit ancêtre portant au moins deux cellules.
                    const seen = new Set();
                    for (const c of document.querySelectorAll(ANY)) {
                        let n = c.parentElement;
                        while (n && n !== document.body && n.querySelectorAll(ANY).length < 2) n = n.parentElement;
                        if (n && !seen.has(n)) { seen.add(n); rows.push(n); }
                    }
                }

                const read = (r, n) => {
                    const e = r.querySelector(CELL(n));
                    return e ? [(e.innerText || '').trim(), e.getAttribute('title') || ''] : ['', ''];
                };

                return rows.slice(0, 80).map(r => {
                    // Libellé de ligne (Plus/Moins) : feuille numérique hors des cellules de cote.
                    const lineEl = Array.from(r.querySelectorAll('*')).find(e =>
                        e.children.length === 0 && !e.closest(ANY) && /^\d+[.,]\d+$/.test((e.innerText || '').trim()));
                    const [t1, h1] = read(r, 1), [t2, h2] = read(r, 2), [t3, h3] = read(r, 3);
                    const bk = r.querySelector('[data-analytics-bookmaker-id]');
                    const logo = r.querySelector('img[alt]');
                    return [lineEl ? lineEl.innerText.trim() : '', t1, h1, t2, h2, t3, h3,
                            bk ? bk.getAttribute('data-analytics-bookmaker-id') : '',
                            logo ? logo.getAttribute('alt') : ''];
                });
            }
            """) ?? Array.Empty<string[]>();

        return raw.Where(r => r.Length >= 9)
                  .Select(r => new OddsRow(
                      r[0].Replace(',', '.'),
                      ParseOddCell(r[1], r[2]),
                      ParseOddCell(r[3], r[4]),
                      ParseOddCell(r[5], r[6]),
                      r[7] ?? "",
                      r[8] ?? ""))
                  .ToList();
    }

    /// <summary>
    /// Décode une cellule : la valeur affichée est la cote actuelle, et l'attribut title contient
    /// « ouverture » actuelle ». C'est la valeur affichée qui fixe le sens du titre : votre
    /// ancienne application lisait le nul à l'envers (indices inversés), ce qui est impossible ici.
    /// </summary>
    private static OddCell ParseOddCell(string text, string title)
    {
        var shownMatch = Regex.Match(text ?? "", @"\d+[.,]\d+");
        decimal? shown = shownMatch.Success ? ParseDecimal(shownMatch.Value) : ParseDecimal(text ?? "");

        var nums = Regex.Matches(title ?? "", @"\d+[.,]\d+")
            .Select(m => ParseDecimal(m.Value))
            .Where(v => v.HasValue).Select(v => v!.Value).ToList();

        if (nums.Count >= 2)
        {
            decimal a = nums[0], b = nums[^1];
            if (shown == b) return new OddCell(a, b);
            if (shown == a) return new OddCell(b, a);
            return new OddCell(null, shown ?? b);
        }

        // Titre vide : Flashscore n'affiche pas d'historique car la cote n'a jamais bougé.
        // L'ouverture est donc égale à la cote actuelle.
        if (string.IsNullOrWhiteSpace(title) && shown.HasValue)
        {
            return new OddCell(shown, shown);
        }

        return new OddCell(null, shown);
    }

    /// <summary>
    /// Choisit la ligne d'un bookmaker donné plutôt que la première venue. Sans cela, le 1X2
    /// pouvait venir de Betclic et le BTTS de Winamax, sous un même identifiant en base, et
    /// d'un match à l'autre la source changeait au gré de l'ordre d'affichage.
    /// </summary>
    private static OddsRow? PickRow(IEnumerable<OddsRow> rows, Func<OddsRow, bool> usable, IReadOnlyList<string> preferred)
    {
        var candidates = rows.Where(usable).ToList();
        foreach (var bookmaker in preferred)
        {
            var row = candidates.FirstOrDefault(r => r.Bookmaker == bookmaker);
            if (row != null) return row;
        }
        return candidates.FirstOrDefault();
    }

    /// <summary>
    /// Bookmakers à privilégier, par ordre de préférence (ScraperSettings:PreferredBookmakerIds).
    /// Par défaut 141 = Betclic, premier affiché par Flashscore : c'est très probablement la
    /// source de votre historique, puisque l'ancienne application lisait la première ligne.
    /// </summary>
    private List<string> PreferredBookmakers() =>
        (_config["ScraperSettings:PreferredBookmakerIds"] ?? "141")
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .ToList();

    /// <summary>Deux colonnes d'un marché à deux issues : 2 et 3 (ancienne application), sinon 1 et 2.</summary>
    private static (OddCell A, OddCell B)? TwoWay(OddsRow r) =>
        r.C2.Current.HasValue && r.C3.Current.HasValue ? (r.C2, r.C3)
        : r.C1.Current.HasValue && r.C2.Current.HasValue ? (r.C1, r.C2)
        : null;

    /// <summary>
    /// Lit les 5 marchés de cotes d'un match pour le bookmaker préféré (ScraperSettings:
    /// PreferredBookmakerIds, 141 = Betclic par défaut) : 1X2, Plus/Moins 1.5/2.5/3.5, BTTS,
    /// Double chance, Remboursé si nul. withOpening = lire aussi la cote d'ouverture.
    /// Chaque marché passe des contrôles de cohérence ; une cote douteuse est laissée vide.
    /// </summary>
    private async Task<ScrapedOdds> ScrapeAllOddsMarketsAsync(IPage page, MatchRoutes routes, string matchKey, bool withOpening)
    {
        int bookmakerId = _config.GetValue<int>("ScraperSettings:DefaultBookmakerId", 1);
        var odds = new ScrapedOdds { BookmakerId = bookmakerId };
        var open = new ScrapedOdds { BookmakerId = bookmakerId };
        var preferred = PreferredBookmakers();

        // 1. 1X2 : la page de cotes est chargée par son adresse, document neuf.
        if (await OpenOddsMarketAsync(page, routes, routes.Market1X2, matchKey))
        {
            var row = PickRow(await ReadOddsRowsAsync(page),
                r => r.C1.Current.HasValue && r.C2.Current.HasValue && r.C3.Current.HasValue, preferred);

            if (row != null)
            {
                // Les autres marchés du match viendront en priorité du même bookmaker.
                preferred.Remove(row.Bookmaker);
                preferred.Insert(0, row.Bookmaker);
                odds.SourceBookmaker = row.BookmakerName;

                (odds.OddHome, odds.OddDraw, odds.OddAway) = (row.C1.Current, row.C2.Current, row.C3.Current);
                (open.OddHome, open.OddDraw, open.OddAway) = (row.C1.Opening, row.C2.Opening, row.C3.Opening);
            }

            if (!KeepIfCoherent("1X2", matchKey, odds.OddHome, odds.OddDraw, odds.OddAway))
            { odds.OddHome = null; odds.OddDraw = null; odds.OddAway = null; }
            if (!withOpening || !KeepIfCoherent("1X2 ouverture", matchKey, open.OddHome, open.OddDraw, open.OddAway))
            { open.OddHome = null; open.OddDraw = null; open.OddAway = null; }
        }

        // 2. PLUS / MOINS : Plus en colonne 2, Moins en colonne 3.
        if (await SwitchOddsTabAsync(page, routes, "under-over", routes.MarketOverUnder, matchKey))
        {
            var rows = await ReadOddsRowsAsync(page);

            OddsRow? Line(string l) => PickRow(rows, r => r.Line == l && r.C2.Current.HasValue && r.C3.Current.HasValue, preferred);
            var r15 = Line("1.5"); var r25 = Line("2.5"); var r35 = Line("3.5");

            if (r15 != null) { (odds.OddOv15, odds.OddUn15) = (r15.C2.Current, r15.C3.Current); (open.OddOv15, open.OddUn15) = (r15.C2.Opening, r15.C3.Opening); }
            if (r25 != null) { (odds.OddOv25, odds.OddUn25) = (r25.C2.Current, r25.C3.Current); (open.OddOv25, open.OddUn25) = (r25.C2.Opening, r25.C3.Opening); }
            if (r35 != null) { (odds.OddOv35, odds.OddUn35) = (r35.C2.Current, r35.C3.Current); (open.OddOv35, open.OddUn35) = (r35.C2.Opening, r35.C3.Opening); }

            ValidateOverUnder(odds, withOpening ? open : null, matchKey);

            if (r15 == null && r25 == null && r35 == null)
            {
                _logger.LogWarning("Aucune ligne Plus/Moins reconnue pour {MatchKey}.", matchKey);
                await SaveDiagnosticAsync(page, matchKey, "plus-moins");
            }
        }

        // 3. LES DEUX MARQUENT
        if (await SwitchOddsTabAsync(page, routes, "both-teams-to-score", routes.MarketBtts, matchKey))
        {
            var twoWayRow = PickRow(await ReadOddsRowsAsync(page), r => TwoWay(r).HasValue, preferred);
            var pair = twoWayRow is null ? null : TwoWay(twoWayRow);
            if (pair is { } p)
            {
                (odds.OddBTTS, odds.OddBTNS) = (p.A.Current, p.B.Current);
                (open.OddBTTS, open.OddBTNS) = (p.A.Opening, p.B.Opening);
            }
            if (!KeepIfCoherent("BTTS", matchKey, odds.OddBTTS, odds.OddBTNS)) { odds.OddBTTS = null; odds.OddBTNS = null; }
            if (!withOpening || !KeepIfCoherent("BTTS ouverture", matchKey, open.OddBTTS, open.OddBTNS)) { open.OddBTTS = null; open.OddBTNS = null; }
        }

        // 4. DOUBLE CHANCE : 1X, 12, X2 en colonnes 1, 2, 3.
        if (await SwitchOddsTabAsync(page, routes, "double-chance", routes.MarketDoubleChance, matchKey))
        {
            var row = PickRow(await ReadOddsRowsAsync(page),
                r => r.C1.Current.HasValue && r.C2.Current.HasValue && r.C3.Current.HasValue, preferred);

            if (row != null && IsCoherentDoubleChance(row.C1.Current, row.C2.Current, row.C3.Current, odds, matchKey))
            {
                (odds.OddHomeDraw, odds.OddAwayDraw) = (row.C1.Current, row.C3.Current);

                // L'ouverture est contrôlée contre le 1X2 d'ouverture, pas contre le 1X2 actuel.
                if (withOpening && IsCoherentDoubleChance(row.C1.Opening, row.C2.Opening, row.C3.Opening, open, matchKey, "Double chance ouverture"))
                {
                    (open.OddHomeDraw, open.OddAwayDraw) = (row.C1.Opening, row.C3.Opening);
                }
            }
        }

        // 5. REMBOURSÉ SI NUL
        if (await SwitchOddsTabAsync(page, routes, "draw-no-bet", routes.MarketDrawNoBet, matchKey))
        {
            var twoWayRow = PickRow(await ReadOddsRowsAsync(page), r => TwoWay(r).HasValue, preferred);
            var pair = twoWayRow is null ? null : TwoWay(twoWayRow);
            if (pair is { } p)
            {
                (odds.OddHomeDNB, odds.OddAwayDNB) = (p.A.Current, p.B.Current);
                (open.OddHomeDNB, open.OddAwayDNB) = (p.A.Opening, p.B.Opening);
            }
            if (!KeepIfCoherent("DNB", matchKey, odds.OddHomeDNB, odds.OddAwayDNB)) { odds.OddHomeDNB = null; odds.OddAwayDNB = null; }
            if (!withOpening || !KeepIfCoherent("DNB ouverture", matchKey, open.OddHomeDNB, open.OddAwayDNB)) { open.OddHomeDNB = null; open.OddAwayDNB = null; }
        }

        odds.Opening = withOpening && open.HasAnyOdd ? open : null;
        return odds;
    }

    /// <summary>
    /// Plus/Moins : marge plausible par ligne, puis contrôle d'orientation. Plus la ligne monte,
    /// plus « Plus de » est cher : des colonnes inversées passent le test de marge, pas celui-ci.
    /// </summary>
    private void ValidateOverUnder(ScrapedOdds odds, ScrapedOdds? open, string matchKey)
    {
        var targets = new[] { odds, open }.OfType<ScrapedOdds>().ToList();

        foreach (var target in targets)
        {
            bool isOpen = ReferenceEquals(target, open);
            string suffix = isOpen ? " ouverture" : "";
            if (!KeepIfCoherent("O/U 1.5" + suffix, matchKey, target.OddOv15, target.OddUn15)) { target.OddOv15 = null; target.OddUn15 = null; }
            if (!KeepIfCoherent("O/U 2.5" + suffix, matchKey, target.OddOv25, target.OddUn25)) { target.OddOv25 = null; target.OddUn25 = null; }
            if (!KeepIfCoherent("O/U 3.5" + suffix, matchKey, target.OddOv35, target.OddUn35)) { target.OddOv35 = null; target.OddUn35 = null; }
        }

        var overs = new[] { odds.OddOv15, odds.OddOv25, odds.OddOv35 }
            .Where(v => v.HasValue).Select(v => v!.Value).ToList();
        if (overs.Count < 2) return;

        var pairs = overs.Zip(overs.Skip(1)).ToList();
        if (pairs.All(p => p.First < p.Second)) return;

        if (pairs.All(p => p.First > p.Second))
        {
            _logger.LogWarning("Colonnes Plus/Moins inversées détectées pour {MatchKey} : correction appliquée.", matchKey);
            foreach (var t in targets)
            {
                (t.OddOv15, t.OddUn15) = (t.OddUn15, t.OddOv15);
                (t.OddOv25, t.OddUn25) = (t.OddUn25, t.OddOv25);
                (t.OddOv35, t.OddUn35) = (t.OddUn35, t.OddOv35);
            }
            return;
        }

        _logger.LogWarning("Cotes Plus/Moins incohérentes entre lignes pour {MatchKey} : rejetées.", matchKey);
        foreach (var t in targets)
        {
            t.OddOv15 = null; t.OddUn15 = null; t.OddOv25 = null; t.OddUn25 = null; t.OddOv35 = null; t.OddUn35 = null;
        }
    }

    /// <summary>
    /// Double chance : les trois cotes couvrent chacune deux issues sur trois, la somme des
    /// probabilités implicites vaut donc environ 2. Et une double chance est forcément plus
    /// courte que chacune des deux issues qu'elle couvre. Ce second test aurait détecté les
    /// cotes 1X2 enregistrées à tort comme double chance.
    /// </summary>
    private bool IsCoherentDoubleChance(decimal? dc1X, decimal? dc12, decimal? dcX2, ScrapedOdds o, string matchKey,
                                        string label = "Double chance")
    {
        // 1X et X2 sont les seules cotes stockées : elles doivent exister.
        bool ok = dc1X > 1m && dcX2 > 1m;

        // Marge : vérifiable seulement si la cote 12 est affichée. Plafond à 2,50 : les doubles
        // chances d'ouverture de Betclic montent à ~2,40, des cotes réelles et non des erreurs.
        if (ok && dc12 > 1m)
        {
            ok = (1m / dc1X!.Value + 1m / dc12!.Value + 1m / dcX2!.Value) is >= 1.95m and <= 2.50m;
        }

        // Une double chance est forcément plus courte que chacune des deux issues qu'elle couvre.
        bool hasLegs = o.OddHome.HasValue && o.OddDraw.HasValue && o.OddAway.HasValue;
        if (ok && hasLegs)
        {
            ok = dc1X < o.OddHome && dc1X < o.OddDraw && dcX2 < o.OddAway && dcX2 < o.OddDraw;
        }

        // Sans cote 12 ni 1X2 de référence, rien ne permet de vérifier : rejet par prudence.
        if (ok && !(dc12 > 1m) && !hasLegs) ok = false;

        if (!ok)
        {
            _logger.LogWarning("Cotes incohérentes rejetées ({Label}) pour {MatchKey} : {A} / {B} / {C}",
                label, matchKey, dc1X, dc12, dcX2);
        }
        return ok;
    }

    // ===== 7. SCORE À LA MI-TEMPS =================================================
    /// <summary>
    /// Score à la mi-temps, lu dans l'en-tête « 1. mi-temps   0 - 0 » du résumé.
    /// L'ancien sélecteur (.smv__incidentsHeader) n'existe plus : ce score n'était jamais extrait.
    /// </summary>
    private async Task ExtractHalfTimeScoreAsync(IPage page, ScrapedMatchData data)
    {
        const string htScript = """
            () => {
                const label = /(^|\s)1\s*(\.|ère|re|er)?\s*mi-temps/i;
                const nodes = document.querySelectorAll('[data-testid="wcl-headerSection-text"], .smv__incidentsHeader, [class*="headerSection"]');
                for (const n of nodes) {
                    const t = (n.innerText || '').replace(/\s+/g, ' ').trim();
                    if (!label.test(t)) continue;
                    const m = t.replace(label, ' ').match(/(\d+)\s*-\s*(\d+)/);
                    if (m) return m[1] + '-' + m[2];
                }
                return null;
            }
            """;

        try
        {
            // Le bloc des événements est chargé APRÈS le score : sans attente, il n'était présent
            // qu'une fois sur deux (45 % de mi-temps sur 75 matchs).
            try
            {
                await page.WaitForFunctionAsync(htScript, null, new PageWaitForFunctionOptions { Timeout = 6000 });
            }
            catch (TimeoutException) { }

            var ht = await page.EvaluateAsync<string?>(htScript);

            if (!string.IsNullOrEmpty(ht))
            {
                var parts = ht.Split('-');
                data.HomeScoreHT = int.Parse(parts[0]);
                data.AwayScoreHT = int.Parse(parts[1]);
            }
            else
            {
                _logger.LogWarning("Score mi-temps introuvable pour le match terminé {MatchKey}.", data.FlashscoreMatchKey);
                await SaveDiagnosticAsync(page, data.FlashscoreMatchKey, "mi-temps");
            }
        }
        catch (PlaywrightException ex)
        {
            _logger.LogDebug(ex, "Score mi-temps illisible pour {MatchKey}.", data.FlashscoreMatchKey);
        }
    }

    // ===== 8. NAVIGATEUR ==========================================================
    /// <summary>
    /// 1. ANTI-PUB & ANTI-POPUP :
    /// Bloque les régies publicitaires et ferme instantanément tout onglet externe qui tenterait de s'ouvrir.
    /// </summary>
    private async Task<IBrowserContext> CreateOptimizedContextAsync(IBrowser browser)
    {
        var context = await browser.NewContextAsync(new BrowserNewContextOptions
        {
            UserAgent = "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/130.0.0.0 Safari/537.36",
            ViewportSize = new ViewportSize { Width = 1366, Height = 900 },
            Locale = "fr-FR",
            // Fuseau imposé : les heures de match affichées ne dépendent plus de la machine
            // (PC de dev, PC fixe, compte LocalSystem du service).
            TimezoneId = ParisTime.IanaId
        });

        // Coupe-circuit absolu corrigé : on ignore "about:blank" qui est l'état par défaut de notre propre onglet
        context.Page += async (_, newPage) =>
        {
            try
            {
                await Task.Delay(500); // Laisse le temps à l'URL de la popup de s'afficher

                if (!newPage.IsClosed && !newPage.Url.Contains("flashscore") && !newPage.Url.Contains("about:blank"))
                {
                    _logger.LogWarning("Onglet publicitaire détecté et fermé automatiquement : {Url}", newPage.Url);
                    await newPage.CloseAsync();
                }
            }
            catch { /* Page déjà fermée */ }
        };

        await context.RouteAsync("**/*", async route =>
        {
            var resourceType = route.Request.ResourceType;
            var url = route.Request.Url.ToLowerInvariant();

            // Liste noire élargie des régies pub et bannières de paris qui détournent les clics
            if (resourceType is "image" or "media" or "font" ||
                url.Contains("googlesyndication") ||
                url.Contains("doubleclick") ||
                url.Contains("analytics") ||
                url.Contains("adsystem") ||
                url.Contains("adnxs") ||
                url.Contains("criteo") ||
                url.Contains("taboola") ||
                url.Contains("rubiconproject") ||
                url.Contains("content.livesportmedia.eu") || // Serveur de bannières internes Flashscore
                url.Contains("bet365") ||
                url.Contains("unibet") ||
                url.Contains("winamax") ||
                url.Contains("betclic"))
            {
                await route.AbortAsync();
            }
            else
            {
                await route.ContinueAsync();
            }
        });

        // Masque en CSS tous les conteneurs de bannières Flashscore qui décalent la page (Layout Shift)
        await context.AddInitScriptAsync(@"
            const style = document.createElement('style');
            style.innerHTML = `
                [class*='adsenvelope'], [id*='lsadvert'], .ban_envelope, 
                .otPlaceholder, iframe { display: none !important; pointer-events: none !important; }
            `;
            document.head.appendChild(style);
        ");

        return context;
    }

    /// <summary>
    /// 2. NAVIGATION CALENDRIER SANS CLIC PHYSIQUE :
    /// Utilise element.click() en JS pour qu'aucune pub ne puisse intercepter le clic souris.
    /// </summary>
    private async Task NavigateToDayOffsetAsync(IPage page, string baseUrl, int targetOffset)
    {
        await page.GotoAsync(baseUrl, new PageGotoOptions { WaitUntil = WaitUntilState.DOMContentLoaded });
        await page.WaitForSelectorAsync(".sportName.soccer", new PageWaitForSelectorOptions { Timeout = 15000 });

        if (targetOffset == 0) return;

        bool isPrev = targetOffset < 0;
        int clicks = Math.Abs(targetOffset);

        for (int c = 0; c < clicks; c++)
        {
            // Clic purement JavaScript sur la flèche du calendrier (contourne tout overlay publicitaire)
            await page.EvaluateAsync(@"([goBack]) => {
                const selector = goBack 
                    ? ""button[data-day-picker-arrow='prev'], .calendar__navigation--yesterday, .calendar__direction--yesterday""
                    : ""button[data-day-picker-arrow='next'], .calendar__navigation--tomorrow, .calendar__direction--tomorrow"";
                const btn = document.querySelector(selector);
                if (btn) btn.click();
            }", new object[] { isPrev });

            // Attend que la liste des matchs se rafraîchisse
            await Task.Delay(1500);
            await page.WaitForSelectorAsync(".sportName.soccer div.event__match", new PageWaitForSelectorOptions { Timeout = 10000 });
        }
    }

    /// <summary>Refuse les cookies non essentiels si la bannière est affichée.</summary>
    private async Task DismissCookiesIfPresentAsync(IPage page)
    {
        try
        {
            // Refus des traceurs non essentiels : le contenu sportif n'en dépend pas.
            await page.WaitForSelectorAsync("#onetrust-reject-all-handler", new PageWaitForSelectorOptions { Timeout = 3000 });
            await page.EvaluateAsync("document.querySelector('#onetrust-reject-all-handler')?.click()");
            await Task.Delay(500);
        }
        catch { /* Bandeau déjà fermé */ }
    }

/// <summary>Clé Flashscore d'une équipe, extraite du lien vers sa page.</summary>
private async Task<string> ExtractTeamKeyFromHrefAsync(IPage page, string selector)
    {
        var href = await page.Locator(selector).First.GetAttributeAsync("href");
        if (string.IsNullOrEmpty(href)) return Guid.NewGuid().ToString("N").Substring(0, 8);
        var parts = href.Split('/', StringSplitOptions.RemoveEmptyEntries);
        return parts.LastOrDefault() ?? href;
    }

    /// <summary>Journalise l'état de la page statistiques (option --dump-stats).</summary>
    public bool DumpStats { get; set; }

    /// <summary>Taux de remplissage du run, évalués à la fin (détection de panne silencieuse).</summary>
    private readonly ScrapeQualityReport _quality = new();

    /// <summary>Nombre de pages problématiques déjà sauvegardées (plafonné).</summary>
    private int _diagnosticsSaved;
    private const int MaxDiagnostics = 5;

    // ===== 9. DIAGNOSTIC ET CONTRÔLE QUALITÉ ======================================
    /// <summary>
    /// Sauvegarde le HTML et une capture de la page fautive dans un dossier "diagnostics".
    /// Sans cela, une anomalie constatée le lendemain est impossible à reproduire :
    /// la page a changé, et on n'a plus que le NULL en base.
    /// </summary>
    private async Task SaveDiagnosticAsync(IPage page, string matchKey, string reason)
    {
        if (_diagnosticsSaved >= MaxDiagnostics) return;
        _diagnosticsSaved++;

        try
        {
            string dir = Path.Combine(AppContext.BaseDirectory, "diagnostics");
            Directory.CreateDirectory(dir);

            string baseName = Path.Combine(dir, $"{ParisTime.Now:yyyyMMdd-HHmmss}_{reason}_{matchKey}");
            await File.WriteAllTextAsync(baseName + ".html", await page.ContentAsync());
            await page.ScreenshotAsync(new PageScreenshotOptions { Path = baseName + ".png", FullPage = true });

            _logger.LogWarning("Page problématique conservée : {File}.html et .png", baseName);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Diagnostic non enregistré pour {MatchKey}.", matchKey);
        }
    }

    // ===== JOURNAL : BILAN ET CLÔTURE D'UNE EXÉCUTION ===========================
    // Documentation : DOCUMENTATION.md, chapitre 4.5.

    /// <summary>Compteurs d'une exécution, remplis au fil de l'eau et écrits à la clôture.</summary>
    private sealed class RunStats
    {
        public readonly Stopwatch Watch = Stopwatch.StartNew();
        public int Found, Saved, Skipped, Failed, OutOfRange;

        /// <summary>Texte qui remplace le bilan chiffré (simulation, rien à faire...).</summary>
        public string? Note;

        /// <summary>Par ligue : matchs trouvés, enregistrés, en échec.</summary>
        public readonly SortedDictionary<string, (int Found, int Saved, int Failed)> ByLeague = new(StringComparer.OrdinalIgnoreCase);

        public void CountLeague(string league, int found = 0, int saved = 0, int failed = 0)
        {
            ByLeague.TryGetValue(league, out var v);
            ByLeague[league] = (v.Found + found, v.Saved + saved, v.Failed + failed);
        }

        /// <summary>« Ligue 1 9, Serie A 10, ... » : matchs trouvés par ligue.</summary>
        public string RenderLeagueFound() =>
            ByLeague.Count == 0 ? "aucun" : string.Join(", ", ByLeague.Select(kv => $"{kv.Key} {kv.Value.Found}"));

        /// <summary>« Ligue 1 9/9, Serie A 8/10 (2 échecs) » : enregistrés / trouvés par ligue.</summary>
        public string RenderLeagueSaved() =>
            string.Join(", ", ByLeague.Select(kv =>
                $"{kv.Key} {kv.Value.Saved}/{kv.Value.Found}" + (kv.Value.Failed > 0 ? $" ({kv.Value.Failed} échec(s))" : "")));
    }

    /// <summary>
    /// Clôture d'une exécution (journalier ou rattrapage), appelée dans le finally : rapport
    /// qualité, ligne « .Finished » dans AppExecutionLogs, puis statut final dans AppExecutionRuns.
    ///   Failed  : plantage, ou des échecs sans aucun match enregistré ;
    ///   Warning : des matchs en échec, contrôle qualité en alerte, ou aucun match trouvé ;
    ///   Success : sinon.
    /// Ne lève jamais d'exception (elle masquerait l'erreur d'origine).
    /// </summary>
    private async Task FinishRunAsync(int runId, RunStats stats, string sectorPrefix, string? crashError)
    {
        try
        {
            bool degraded = false;
            if (stats.Note == null)
            {
                try
                {
                    LogUnmappedStatCategories();
                    await LogQualityAsync(runId);
                    degraded = _quality.HasDegradation;
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Rapport de qualité non produit.");
                }
            }

            string status =
                crashError != null ? ExecutionRunResult.Failed
                : stats.Failed > 0 && stats.Saved == 0 ? ExecutionRunResult.Failed
                : stats.Failed > 0 || degraded || (stats.Found == 0 && stats.Note == null) ? ExecutionRunResult.Warning
                : ExecutionRunResult.Success;

            string summary = stats.Note ??
                $"Terminé en {stats.Watch.Elapsed:hh\\:mm\\:ss} : {stats.Saved} enregistrés sur {stats.Found} trouvés" +
                (stats.Skipped > 0 ? $", {stats.Skipped} non enregistrés (en cours / non terminés)" : "") +
                (stats.OutOfRange > 0 ? $", {stats.OutOfRange} hors fenêtre" : "") +
                $", {stats.Failed} en échec." +
                (stats.ByLeague.Count > 0 ? $" Par ligue (enregistrés/trouvés) : {stats.RenderLeagueSaved()}." : "") +
                (stats.Found == 0 ? " AUCUN MATCH TROUVÉ : trêve, ou page d'accueil Flashscore modifiée ?" : "") +
                (degraded ? " CONTRÔLE QUALITÉ EN ÉCHEC (voir la ligne .Quality)." : "");
            if (crashError != null) summary = $"PLANTAGE : {crashError} | {summary}";

            int lost = _repository.GetLogWriteFailures(runId);
            if (lost > 0) summary += $" {lost} ligne(s) de journal n'ont pas pu être écrites (voir le fichier log).";

            string level = status == ExecutionRunResult.Success ? "INFO"
                         : status == ExecutionRunResult.Warning ? "WARN" : "ERROR";
            if (level == "INFO") _logger.LogInformation("[{Status}] {Summary}", status, summary);
            else if (level == "WARN") _logger.LogWarning("[{Status}] {Summary}", status, summary);
            else _logger.LogError("[{Status}] {Summary}", status, summary);

            await _repository.LogExecutionAsync(runId, level, $"{sectorPrefix}.Finished", $"[{status}] {summary}");
            await _repository.CompleteExecutionRunAsync(runId, new ExecutionRunResult
            {
                Status = status,
                MatchesFound = stats.Found,
                MatchesSaved = stats.Saved,
                MatchesSkipped = stats.Skipped,
                MatchesFailed = stats.Failed,
                QualityDegraded = degraded,
                Summary = summary,
                Error = crashError
            });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Clôture de l'exécution {RunId} impossible.", runId);
        }
    }

    /// <summary>Journalise le contrôle qualité et le remonte en base, en ERROR si dégradé.</summary>
    private async Task LogQualityAsync(int runId)
    {
        string report = _quality.Render();

        if (_quality.HasDegradation)
        {
            _logger.LogError(
                "CONTRÔLE QUALITÉ EN ÉCHEC — Flashscore a probablement modifié ses pages.{Nl}{Report}{Nl}" +
                "Libellés de statistiques lus et colonne affectée :{Nl}{Map}{Nl}" +
                "Pages problématiques conservées dans le dossier diagnostics.",
                Environment.NewLine, report, Environment.NewLine, Environment.NewLine,
                RenderStatLabelMap(), Environment.NewLine);
            await _repository.LogExecutionAsync(runId, "ERROR", "Scraper.Quality", report);
        }
        else
        {
            _logger.LogInformation("{Report}", report);
            await _repository.LogExecutionAsync(runId, "INFO", "Scraper.Quality", report);
        }
    }

    // ===== 10. STATISTIQUES =======================================================
    /// <summary>
    /// Extraction des statistiques avec trois stratégies successives : identifiants
    /// data-testid actuels, classes historiques, puis heuristique sur la forme des lignes.
    /// Une seule stratégie codée en dur rendait l'extraction muette au moindre changement
    /// de DOM chez Flashscore, sans la moindre alerte.
    /// </summary>
    private async Task ExtractMatchStatsAsync(IPage page, string matchKey, ScrapedMatchData data)
    {
        const string readyScript =
            @"() => document.querySelectorAll('[data-testid=wcl-statistics]').length > 0
                 || document.querySelectorAll('.stat__row').length > 0
                 || document.querySelectorAll('[class*=statRow]').length > 0";

        // Toutes les stratégies sont appliquées et fusionnées, le premier libellé trouvé l'emporte.
        // La première est celle de l'ancienne application, éprouvée sur la page complète :
        // les éléments wcl-scores-simple se suivent par triplets [valeur dom., libellé, valeur ext.].
        const string extractScript =
            @"() => {
                const out = [];
                const seen = new Set();
                const clean = v => (v || '').replace(/\s+/g, ' ').trim();
                const push = (c, h, a) => {
                    c = clean(c); h = clean(h); a = clean(a);
                    const k = c.toLowerCase();
                    if (!c || !(h || a) || seen.has(k)) return;
                    seen.add(k);
                    out.push(c + '\u0001' + h + '\u0001' + a);
                };
                const isVal = v => /\d/.test(v) && /^[\d.,%\s()\/]+$/.test(v);
                const isLabel = v => !!v && v.length <= 40 && /[a-zà-ÿ]/i.test(v);

                // 1. Ancienne application : triplets wcl-scores-simple
                const simple = Array.from(document.querySelectorAll('[data-testid*=wcl-scores-simple]')).map(e => clean(e.innerText));
                for (let i = 1; i + 1 < simple.length; i++) {
                    if (isLabel(simple[i]) && isVal(simple[i - 1]) && isVal(simple[i + 1])) push(simple[i], simple[i - 1], simple[i + 1]);
                }

                // 2. Lignes wcl-statistics avec catégorie et valeurs identifiées
                for (const r of document.querySelectorAll('[data-testid=wcl-statistics]')) {
                    const cat = r.querySelector('[data-testid=wcl-statistics-category]');
                    const vals = r.querySelectorAll('[data-testid=wcl-statistics-value]');
                    if (cat && vals.length >= 2) push(cat.innerText, vals[0].innerText, vals[1].innerText);
                }

                // 3. Classes historiques
                for (const r of document.querySelectorAll('.stat__row, [class*=statRow]')) {
                    const cat = r.querySelector('.stat__categoryName, [class*=categoryName]');
                    const h = r.querySelector('.stat__homeValue, [class*=homeValue]');
                    const a = r.querySelector('.stat__awayValue, [class*=awayValue]');
                    if (cat && h && a) push(cat.innerText, h.innerText, a.innerText);
                }

                // 4. Heuristique : 3 enfants, libellé au centre, valeurs aux extrémités
                for (const el of document.querySelectorAll('div')) {
                    if (el.children.length !== 3) continue;
                    const t0 = clean(el.children[0].innerText), t1 = clean(el.children[1].innerText), t2 = clean(el.children[2].innerText);
                    if (isLabel(t1) && isVal(t0) && isVal(t2)) push(t1, t0, t2);
                }
                return out;
            }";

        const string dumpScript =
            @"() => {
                const probes = ['[data-testid=wcl-statistics]', '[data-testid=wcl-statistics-category]',
                                '[data-testid=wcl-statistics-value]', '.stat__row', '.stat__categoryName',
                                '[class*=statRow]', '[class*=categoryName]', '[class*=homeValue]', 'div[class*=stat]'];
                const out = probes.map(p => p + ' = ' + document.querySelectorAll(p).length);
                out.push('URL = ' + location.href);
                out.push('TEXTE = ' + (document.body ? document.body.innerText.replace(/\s+/g, ' ').slice(0, 500) : ''));
                return out;
            }";

        try
        {
            await page.WaitForFunctionAsync(readyScript, null,
                new PageWaitForFunctionOptions { Timeout = 9000 });
        }
        catch (TimeoutException)
        {
            // L'heuristique peut encore aboutir : on tente l'extraction malgré tout.
        }

        string[] rows;
        try
        {
            rows = await page.EvaluateAsync<string[]>(extractScript) ?? Array.Empty<string>();
        }
        catch (PlaywrightException ex)
        {
            _logger.LogWarning("Lecture des statistiques impossible pour {MatchKey} : {Message}", matchKey, ex.Message);
            rows = Array.Empty<string>();
        }

        if (DumpStats)
        {
            try
            {
                var dump = await page.EvaluateAsync<string[]>(dumpScript) ?? Array.Empty<string>();
                _logger.LogWarning("DIAGNOSTIC stats {MatchKey} :{Nl}{Dump}{Nl}LIGNES ({Count}) :{Nl}{Rows}",
                    matchKey, Environment.NewLine, string.Join(Environment.NewLine, dump),
                    rows.Length, Environment.NewLine,
                    string.Join(Environment.NewLine, rows.Select(r => r.Replace('\u0001', '='))));
            }
            catch { }
        }

        if (rows.Length == 0)
        {
            _logger.LogWarning("Aucune statistique lisible pour le match {MatchKey} (relancez avec --dump-stats).", matchKey);
            await SaveDiagnosticAsync(page, matchKey, "stats");
            return;
        }

        data.HomeStats = new ScrapedTeamStats();
        data.AwayStats = new ScrapedTeamStats();

        foreach (var row in rows)
        {
            var parts = row.Split('\u0001');
            if (parts.Length < 3) continue;
            MapStatRow(parts[0], parts[1], parts[2], data.HomeStats, data.AwayStats);
        }
    }

    /// <summary>
    /// Libellés Flashscore non reconnus, rencontrés durant l'exécution. Journalisés en fin de
    /// run : c'est le moyen de voir si Flashscore a renommé une statistique, ce qui ferait
    /// silencieusement remonter des NULL en base.
    /// </summary>
    private readonly HashSet<string> _unmappedStatCategories = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Libellé Flashscore → colonne cible et exemple de valeurs, pour le diagnostic.</summary>
    private readonly Dictionary<string, string> _statLabelLog = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Associe un libellé Flashscore à une colonne. L'ordre des tests compte : du plus
    /// spécifique au plus général.
    /// </summary>
    private static string ResolveStatTarget(string label)
    {
        string c = label.ToLowerInvariant().Trim();

        // xGOT AVANT xG : « xG sur tirs cadrés (xGOT) » contient « xg » et écrasait le vrai xG.
        if (c.Contains("xgot") || (c.Contains("xg") && c.Contains("cadr"))) return "Ignored";
        if (c.Contains("buts attendus") || c.Contains("expected goals") || c.Contains("(xg)") || c == "xg") return "ExpectedGoals";
        if (c.Contains("possession")) return "PossessionPct";

        // Non cadrés AVANT cadrés, cadrés AVANT le total.
        if (c.Contains("non cadr")) return "Ignored";
        if (c.Contains("cadrés") || c.Contains("cadres") || c.Contains("on target")) return "ShotsOnTarget";

        // « Tirs au but » est volontairement absent : ambigu en français, il reste signalé.
        if (c is "tirs" or "tentatives" or "total tirs" or "tirs totaux" or "tirs au total"
            || c.Contains("total des tirs") || c.Contains("total de tirs") || c.Contains("tentatives de but"))
            return "Shots";

        if (c.Contains("corner") || c.Contains("pied de coin")) return "Corners";
        if (c.Contains("faute")) return "Fouls";
        if (c.Contains("carton jaune") || c.Contains("cartons jaunes")) return "YellowCards";
        if (c.Contains("carton rouge") || c.Contains("cartons rouges")) return "RedCards";

        // Connues mais non stockées : pas de bruit dans les alertes.
        if (c.Contains("occasions") || c.Contains("arrêt") || c.Contains("hors-jeu") || c.Contains("hors jeu")
            || c.Contains("passe") || c.Contains("touche") || c.Contains("dégagement") || c.Contains("tacle")
            || c.Contains("duel") || c.Contains("remplacement") || c.Contains("centre") || c.Contains("contré")
            || c.Contains("surface") || c.Contains("interception") || c.Contains("récupération") || c.Contains("dribble")
            || c.Contains("6 mètres") || c.Contains("tête") || c.Contains("buts évités") || c.Contains("coup franc")
            || c.Contains("coups francs") || c.Contains("erreurs menant") || c.Contains("expected assists")
            || c.Contains("(xa)") || c.Contains("montant") || c.Contains("xgot"))
            return "Ignored";

        // Libellé d'un ou deux caractères (« X ») : artefact de lecture, pas une statistique.
        if (c.Length < 3) return "Ignored";

        return "Unmapped";
    }

    /// <summary>
    /// Range une ligne du tableau de statistiques (libellé, valeur domicile, valeur extérieur)
    /// dans la bonne colonne. La première valeur trouvée est conservée (la page affiche parfois
    /// la même statistique dans plusieurs blocs).
    /// </summary>
    private void MapStatRow(string category, string homeVal, string awayVal, ScrapedTeamStats home, ScrapedTeamStats away)
    {
        string label = category.Trim();
        string target = ResolveStatTarget(label);
        bool isDecimal = target is "ExpectedGoals" or "PossessionPct";

        // Valeur illisible sur une colonne reconnue : c'est un autre mode de panne silencieuse.
        bool readable = target is "Ignored" or "Unmapped"
            || (isDecimal ? ParseDecimal(homeVal.Replace("%", "")).HasValue : ParseInt(homeVal).HasValue);

        _statLabelLog.TryAdd(label,
            $"{target}{(readable ? "" : " — VALEUR ILLISIBLE")}  (ex. « {homeVal} » / « {awayVal} »)");

        // Première occurrence retenue (??=) : si un libellé apparaît deux fois, dans le résumé
        // puis dans la liste complète, une ligne mal routée ne peut plus écraser la bonne.
        switch (target)
        {
            case "ExpectedGoals":
                home.ExpectedGoals ??= ParseDecimal(homeVal);
                away.ExpectedGoals ??= ParseDecimal(awayVal);
                break;
            case "PossessionPct":
                home.PossessionPct ??= ParseDecimal(homeVal.Replace("%", ""));
                away.PossessionPct ??= ParseDecimal(awayVal.Replace("%", ""));
                break;
            case "ShotsOnTarget":
                home.ShotsOnTarget ??= ParseInt(homeVal);
                away.ShotsOnTarget ??= ParseInt(awayVal);
                break;
            case "Shots":
                home.Shots ??= ParseInt(homeVal);
                away.Shots ??= ParseInt(awayVal);
                break;
            case "Corners":
                home.Corners ??= ParseInt(homeVal);
                away.Corners ??= ParseInt(awayVal);
                break;
            case "Fouls":
                home.Fouls ??= ParseInt(homeVal);
                away.Fouls ??= ParseInt(awayVal);
                break;
            case "YellowCards":
                home.YellowCards ??= ParseInt(homeVal);
                away.YellowCards ??= ParseInt(awayVal);
                break;
            case "RedCards":
                home.RedCards ??= ParseInt(homeVal);
                away.RedCards ??= ParseInt(awayVal);
                break;
            case "Unmapped":
                _unmappedStatCategories.Add(label);
                break;
        }
    }

    /// <summary>Table complète libellé → colonne, jointe aux alertes qualité.</summary>
    private string RenderStatLabelMap()
    {
        if (_statLabelLog.Count == 0) return "  (aucun libellé de statistique lu)";
        return string.Join(Environment.NewLine,
            _statLabelLog.OrderBy(k => k.Key).Select(k => $"  {k.Key,-34} → {k.Value}"));
    }

    /// <summary>
    /// Vérifie qu'un marché est plausible : la somme des probabilités implicites dépasse 1
    /// (marge du bookmaker) sans être aberrante. Une cote lue sur la mauvaise ligne du
    /// tableau échoue presque toujours à ce test.
    /// </summary>
    private bool KeepIfCoherent(string market, string matchKey, params decimal?[] odds)
    {
        if (odds.All(o => !o.HasValue)) return false;

        bool coherent = odds.All(o => o.HasValue && o.Value > 1m)
            && odds.Sum(o => 1m / o!.Value) is >= 0.98m and <= 1.25m;

        if (!coherent)
        {
            _logger.LogWarning("Cotes incohérentes rejetées ({Market}) pour {MatchKey} : {Odds}",
                market, matchKey, string.Join(" / ", odds.Select(o => o?.ToString() ?? "∅")));
        }
        return coherent;
    }

    /// <summary>Signale en fin de run les libellés de statistiques non reconnus.</summary>
    private void LogUnmappedStatCategories()
    {
        if (DumpStats)
        {
            _logger.LogWarning("Libellés de statistiques lus et colonne affectée :{Nl}{Map}",
                Environment.NewLine, RenderStatLabelMap());
        }

        if (_unmappedStatCategories.Count == 0) return;

        _logger.LogWarning(
            "Libellés de statistiques non reconnus et donc NON enregistrés : {Categories}. " +
            "Si l'un correspond à une colonne attendue, complétez MapStatRow.",
            string.Join(" | ", _unmappedStatCategories.OrderBy(c => c)));
    }

    // ===== 11. PETITS OUTILS ======================================================
    /// <summary>
    /// Traduit le libellé de statut Flashscore en code stocké en base.
    /// "Unknown" signifie que le libellé n'a pas pu être lu : l'appelant ne doit alors
    /// déduire ni score ni statistique, et la base ne doit pas être dégradée.
    /// </summary>
    private static string MapMatchStatus(string statusText)
    {
        string s = statusText.Trim().ToLowerInvariant();
        if (s.Length == 0) return "Unknown";

        // Terminé en premier : "Terminé après prolongations" doit rester Finished.
        if (s.Contains("terminé") || s.Contains("termine")
            || s.Contains("après prolong") || s.Contains("apres prolong")
            || s.Contains("après pénalt") || s.Contains("apres penalt")
            || s.Contains("après tirs") || s.Contains("apres tirs"))
            return "Finished";

        if (s.Contains("reporté") || s.Contains("reporte")) return "Postponed";
        if (s.Contains("annulé") || s.Contains("annule")) return "Cancelled";

        if (s.Contains("interrompu") || s.Contains("abandonn")
            || s.Contains("suspendu") || s.Contains("à finir") || s.Contains("a finir"))
            return "Interrupted";

        // Match en cours : mi-temps, pause, ou une minute de jeu du type "67'".
        if (s.Contains("mi-temps") || s.Contains("pause") || s.Contains("en cours")
            || Regex.IsMatch(s, @"\d+\s*'"))
            return "Live";

        if (s.Contains("prévu") || s.Contains("prevu") || Regex.IsMatch(s, @"^\d{1,2}:\d{2}$"))
            return "Scheduled";

        // Libellé inconnu : on le signale plutôt que de le ranger en "Scheduled" par défaut.
        return "Unknown";
    }

    /// <summary>Nombre décimal lu sur la page (« 1,85 » ou « 1.85 »), null si illisible.</summary>
    private static decimal? ParseDecimal(string input)
    {
        if (string.IsNullOrWhiteSpace(input)) return null;
        string clean = input.Replace(",", ".").Trim();
        return decimal.TryParse(clean, NumberStyles.Any, CultureInfo.InvariantCulture, out var val) ? val : null;
    }

    /// <summary>Nombre entier lu sur la page (« 54% » -> 54), null si illisible.</summary>
    private static int? ParseInt(string input)
    {
        return int.TryParse(input.Trim(), out var val) ? val : null;
    }
}