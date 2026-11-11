// =============================================================================
// BackfillCommand.cs — RATTRAPAGE HISTORIQUE EN LIGNE DE COMMANDE
// -----------------------------------------------------------------------------
// Rôle : lancer, depuis une console, la récupération de tous les matchs déjà joués
//        d'une période (début de saison par défaut), sans démarrer le service.
// Exemple : Juniro.ScraperService.exe --backfill --from 2026-08-01 --leagues "Ligue 1,Serie A"
//
// Étapes :
//   1. IsRequested : détecte l'argument --backfill (appelé depuis Program.cs).
//   2. RunAsync : construit la configuration (appsettings + variables d'environnement),
//      les logs, le dépôt SQL et le job, puis appelle
//      FlashscoreScraperJob.ExecuteSeasonBackfillAsync.
//   3. TryBuildOptions : transforme les arguments (--from, --to, --leagues, --dry-run,
//      --force, --limit, --dump-stats...) en SeasonBackfillOptions (Models\).
//
// Différence avec le service journalier : le rattrapage relève la cote d'OUVERTURE et la
// cote FINALE ; le journalier ne relève que la cote ACTUELLE.
// Documentation : DOCUMENTATION.md, chapitre 3.3.
// =============================================================================
using Juniro.ScraperService.Data;
using Juniro.ScraperService.Models;
using Juniro.ScraperService.Services;
using Serilog;

namespace Juniro.ScraperService.Configuration;

/// <summary>
/// Mode console de l'application : rattrapage des matchs manquants depuis le début de saison.
/// Invoqué par "Juniro.ScraperService.exe --backfill", il court-circuite l'hébergement web
/// et Hangfire, s'exécute en avant-plan puis rend la main.
/// </summary>
public static class BackfillCommand
{
    public const string Switch = "--backfill";

    public static bool IsRequested(string[] args) =>
        args.Any(a => a.Equals(Switch, StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// Exécute le rattrapage complet et renvoie le code de sortie du programme
    /// (0 = succès ou aide affichée, 1 = erreur ou arguments invalides).
    /// </summary>
    public static async Task<int> RunAsync(string[] args)
    {
        if (HasFlag(args, "--help") || HasFlag(args, "-h"))
        {
            PrintUsage();
            return 0;
        }

        string environment =
            GetValue(args, "--environment")
            ?? Environment.GetEnvironmentVariable("ASPNETCORE_ENVIRONMENT")
            ?? "Production";

        // Base path explicite : la console peut être lancée depuis n'importe quel répertoire.
        IConfiguration config = new ConfigurationBuilder()
            .SetBasePath(AppContext.BaseDirectory)
            .AddJsonFile("appsettings.json", optional: true)
            .AddJsonFile($"appsettings.{environment}.json", optional: true)
            .AddEnvironmentVariables()
            .Build();

        if (string.IsNullOrWhiteSpace(config.GetConnectionString("JuniroDb")))
        {
            Log.Error(
                "Chaîne de connexion 'JuniroDb' absente. Vérifiez appsettings.{Env}.json dans {Dir} " +
                "ou passez --environment avec le bon nom.", environment, AppContext.BaseDirectory);
            return 1;
        }

        if (!TryBuildOptions(args, out var options, out string? error))
        {
            Log.Error("{Error}", error);
            Console.WriteLine();
            PrintUsage();
            return 1;
        }

        var leagueNames = options.LeagueFilter.Length == 0
            ? "les 10 ligues"
            : string.Join(", ", options.LeagueFilter);

        Log.Information("=== Backfill Juniro ===");
        Log.Information("Environnement : {Env}", environment);
        Log.Information("Fenêtre       : {From:yyyy-MM-dd} → {To:yyyy-MM-dd}", options.From, options.To);
        Log.Information("Ligues        : {Leagues}", leagueNames);
        Log.Information("Reprise       : {Skip}", options.SkipExisting ? "oui (matchs complets ignorés)" : "non (--force)");
        Log.Information("Navigateur    : {Mode}", (options.Headless ?? true) ? "headless" : "visible");
        if (options.DryRun) Log.Information("Mode          : simulation (--dry-run), aucune écriture");
        Console.WriteLine();

        using var loggerFactory = LoggerFactory.Create(b => b.AddSerilog(Log.Logger));

        var repository = new JuniroRepository(config, loggerFactory.CreateLogger<JuniroRepository>());
        var job = new FlashscoreScraperJob(
            loggerFactory.CreateLogger<FlashscoreScraperJob>(), repository, config)
        {
            DumpStats = HasFlag(args, "--dump-stats")
        };

        try
        {
            int saved = await job.ExecuteSeasonBackfillAsync(options);
            Log.Information("Terminé : {Saved} matchs enregistrés.", saved);
            return 0;
        }
        catch (Exception ex)
        {
            Log.Fatal(ex, "Le backfill a échoué.");
            return 1;
        }
    }

    /// <summary>
    /// Lit les arguments de la ligne de commande et remplit les options du rattrapage.
    /// Renvoie false avec un message d'erreur si un argument est invalide.
    /// </summary>
    private static bool TryBuildOptions(string[] args, out SeasonBackfillOptions options, out string? error)
    {
        options = new SeasonBackfillOptions();
        error = null;

        string? fromRaw = GetValue(args, "--from");
        if (!string.IsNullOrEmpty(fromRaw))
        {
            if (!TryParseDate(fromRaw, out var from))
            {
                error = $"Date de début invalide : '{fromRaw}'. Format attendu : yyyy-MM-dd.";
                return false;
            }
            options.From = from;
        }

        string? toRaw = GetValue(args, "--to");
        if (!string.IsNullOrEmpty(toRaw))
        {
            if (!TryParseDate(toRaw, out var to))
            {
                error = $"Date de fin invalide : '{toRaw}'. Format attendu : yyyy-MM-dd.";
                return false;
            }
            // Fin de journée incluse.
            options.To = to.AddDays(1).AddTicks(-1);
        }

        if (options.To <= options.From)
        {
            error = "La date de fin doit être postérieure à la date de début.";
            return false;
        }

        string? leaguesRaw = GetValue(args, "--leagues");
        if (!string.IsNullOrEmpty(leaguesRaw))
        {
            var requested = leaguesRaw
                .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .ToArray();

            var unknown = requested
                .Where(r => !FlashscoreScraperJob.TargetLeagues.Any(l =>
                    l.LeagueName.Equals(r, StringComparison.OrdinalIgnoreCase) ||
                    l.DisplayAliases.Any(a => a.Equals(r, StringComparison.OrdinalIgnoreCase))))
                .ToArray();

            if (unknown.Length > 0)
            {
                error = $"Ligue(s) inconnue(s) : {string.Join(", ", unknown)}. " +
                        $"Valeurs acceptées : {string.Join(", ", FlashscoreScraperJob.TargetLeagues.Select(l => l.LeagueName))}.";
                return false;
            }

            options.LeagueFilter = requested;
        }

        options.SkipExisting = !HasFlag(args, "--force");
        options.IncludeUnfinished = HasFlag(args, "--include-unfinished");
        options.DryRun = HasFlag(args, "--dry-run");

        string? headlessRaw = GetValue(args, "--headless");
        if (!string.IsNullOrEmpty(headlessRaw))
        {
            if (!bool.TryParse(headlessRaw, out bool headless))
            {
                error = $"Valeur invalide pour --headless : '{headlessRaw}'. Attendu : true ou false.";
                return false;
            }
            options.Headless = headless;
        }

        string? limitRaw = GetValue(args, "--limit");
        if (!string.IsNullOrEmpty(limitRaw))
        {
            if (!int.TryParse(limitRaw, out int limit) || limit < 1)
            {
                error = $"Valeur invalide pour --limit : '{limitRaw}'.";
                return false;
            }
            options.Limit = limit;
        }

        string? maxClicksRaw = GetValue(args, "--max-more-clicks");
        if (!string.IsNullOrEmpty(maxClicksRaw))
        {
            if (!int.TryParse(maxClicksRaw, out int maxClicks) || maxClicks < 1)
            {
                error = $"Valeur invalide pour --max-more-clicks : '{maxClicksRaw}'.";
                return false;
            }
            options.MaxMoreClicks = maxClicks;
        }

        string? delayRaw = GetValue(args, "--delay-ms");
        if (!string.IsNullOrEmpty(delayRaw))
        {
            if (!int.TryParse(delayRaw, out int delay) || delay < 0)
            {
                error = $"Valeur invalide pour --delay-ms : '{delayRaw}'.";
                return false;
            }
            options.DelayMsMin = delay;
            options.DelayMsMax = delay + 700;
        }

        return true;
    }

    /// <summary>Lit une date AAAA-MM-JJ, interprétée en heure de Paris.</summary>
    private static bool TryParseDate(string raw, out DateTime utc)
    {
        if (DateTime.TryParseExact(raw, "yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture,
                System.Globalization.DateTimeStyles.None, out var parsed))
        {
            // Heure de Paris, comme MatchDate en base.
            utc = DateTime.SpecifyKind(parsed, DateTimeKind.Unspecified);
            return true;
        }

        utc = default;
        return false;
    }

    /// <summary>Valeur qui suit un argument (ex. « --from 2026-08-01 » -> « 2026-08-01 »).</summary>
    private static string? GetValue(string[] args, string name)
    {
        for (int i = 0; i < args.Length; i++)
        {
            if (!args[i].Equals(name, StringComparison.OrdinalIgnoreCase)) continue;
            if (i + 1 < args.Length && !args[i + 1].StartsWith("--", StringComparison.Ordinal))
                return args[i + 1];
            return string.Empty;
        }
        return null;
    }

    private static bool HasFlag(string[] args, string name) =>
        args.Any(a => a.Equals(name, StringComparison.OrdinalIgnoreCase));

    /// <summary>Affiche l'aide de la commande (toutes les options disponibles).</summary>
    private static void PrintUsage()
    {
        var seasonStart = SeasonBackfillOptions.CurrentSeasonStart(ParisTime.Now);

        Console.WriteLine("Rattrapage des matchs manquants depuis le début de saison.");
        Console.WriteLine();
        Console.WriteLine("  Juniro.ScraperService.exe --backfill [options]");
        Console.WriteLine();
        Console.WriteLine("Options :");
        Console.WriteLine($"  --from yyyy-MM-dd      Début de la fenêtre (défaut : {seasonStart:yyyy-MM-dd}, début de la saison en cours)");
        Console.WriteLine("  --to yyyy-MM-dd        Fin de la fenêtre (défaut : aujourd'hui)");
        Console.WriteLine("  --leagues \"A,B\"        Ligues à traiter, séparées par des virgules (défaut : les 10)");
        Console.WriteLine("  --dry-run              Compte les matchs sans rien scraper ni écrire");
        Console.WriteLine("  --limit N              N'traite que les N premiers matchs (test rapide)");
        Console.WriteLine("  --dump-stats           Journalise l'état de la page statistiques (diagnostic)");
        Console.WriteLine("  --force                Retraite aussi les matchs déjà complets en base");
        Console.WriteLine("  --include-unfinished   Enregistre aussi les matchs non terminés");
        Console.WriteLine("  --headless true|false  Surcharge ScraperSettings:Headless");
        Console.WriteLine("  --delay-ms 700         Pause minimale entre deux matchs");
        Console.WriteLine("  --max-more-clicks 60   Plafond de clics \"Afficher plus de matchs\" par ligue");
        Console.WriteLine("  --environment Nom      Fichier appsettings.<Nom>.json à charger");
        Console.WriteLine();
        Console.WriteLine("Ligues disponibles :");
        foreach (var l in FlashscoreScraperJob.TargetLeagues)
        {
            Console.WriteLine($"  {l.LeagueName,-16} {l.CountryName} (D{l.TierLevel})");
        }
        Console.WriteLine();
        Console.WriteLine("Exemples :");
        Console.WriteLine("  --backfill --dry-run");
        Console.WriteLine("  --backfill --limit 3 --force --headless false   (sonde : voir les libellés de stats)");
        Console.WriteLine("  --backfill --leagues \"Ligue 1\" --headless false");
        Console.WriteLine("  --backfill --from 2026-08-01 --to 2026-09-30");
    }
}
