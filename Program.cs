// =============================================================================
// Program.cs — POINT D'ENTRÉE DE L'APPLICATION
// -----------------------------------------------------------------------------
// Rôle : démarrer Juniro.ScraperService, soit en SERVICE WINDOWS (mode normal),
//        soit en LIGNE DE COMMANDE pour le rattrapage historique (--backfill).
//
// Étapes, dans l'ordre :
//   1. Journalisation Serilog (console + fichier logs\scraper-AAAAMMJJ.log).
//   2. Si l'argument --backfill est présent : rattrapage puis sortie
//      (voir Configuration\BackfillCommand.cs).
//   3. Sinon : construction de l'hôte web, lecture de la configuration
//      (appsettings.json + appsettings.{Environnement}.json), vérification de la
//      chaîne de connexion « JuniroDb ».
//   4. Hangfire (planificateur de tâches) stocké dans la base SQL, 1 seul worker.
//   5. Tableau de bord Hangfire sur http://<pc>:5000/hangfire (réseau local seulement).
//   6. Tâches planifiées :
//        - « daily-flashscore-scrape » à 03h00  -> Services\FlashscoreScraperJob.cs
//        - « daily-analytics » à 05h30 (si Analytics:Enabled) -> Services\AnalyticsJob.cs
//        - « supabase-sync » à 06h15 (si Supabase:Enabled)    -> Services\SupabaseSyncJob.cs
//
// Pour modifier : l'heure du scraping (cronExpression "0 3 * * *"), le port (Hosting:Urls),
// l'activation des calculs (section "Analytics" de appsettings.Production.json).
// Documentation complète : DOCUMENTATION.md, chapitre 2.
// =============================================================================
using Hangfire;
using Juniro.ScraperService.Configuration;
using Juniro.ScraperService.Data;
using Juniro.ScraperService.Services;
using Microsoft.Extensions.Hosting.WindowsServices;
using Serilog;

// Les logs sont configurés AVANT tout le reste : une erreur au démarrage doit laisser une trace.
// Le chemin part de AppContext.BaseDirectory et non du répertoire courant, qui vaut
// C:\Windows\system32 lorsque l'application tourne comme service Windows.
Log.Logger = new LoggerConfiguration()
    .MinimumLevel.Information()
    .WriteTo.Console()
    .WriteTo.File(
        Path.Combine(AppContext.BaseDirectory, "logs", "scraper-.log"),
        rollingInterval: RollingInterval.Day)
    .CreateLogger();

try
{
    // Mode console : rattrapage des matchs manquants puis sortie.
    // Ni serveur web ni Hangfire ne sont démarrés, le job tourne en avant-plan.
    if (BackfillCommand.IsRequested(args))
    {
        return await BackfillCommand.RunAsync(args);
    }

    bool isWindowsService = WindowsServiceHelpers.IsWindowsService();

    // ContentRootPath doit être passé ici, à CreateBuilder : la configuration est lue
    // immédiatement, donc un builder.Host.UseWindowsService() placé après arriverait trop tard
    // et les fichiers appsettings*.json ne seraient jamais trouvés (service démarré depuis
    // C:\Windows\system32). C'est la cause classique d'un service qui refuse de démarrer.
    var builder = WebApplication.CreateBuilder(new WebApplicationOptions
    {
        Args = args,
        ContentRootPath = isWindowsService ? AppContext.BaseDirectory : default
    });

    builder.Host.UseWindowsService(options =>
    {
        options.ServiceName = "JuniroScraperService";
    });

    builder.Host.UseSerilog();

    // Injection de nos services
    builder.Services.AddSingleton<JuniroRepository>();
    builder.Services.AddTransient<FlashscoreScraperJob>();
    builder.Services.AddTransient<AnalyticsJob>();
    builder.Services.AddTransient<SupabaseSyncJob>();

    string connString = builder.Configuration.GetConnectionString("JuniroDb") ?? string.Empty;
    if (string.IsNullOrWhiteSpace(connString))
    {
        throw new InvalidOperationException(
            "La chaîne de connexion 'JuniroDb' est absente ou vide. Vérifiez la présence de " +
            $"appsettings.Production.json dans {AppContext.BaseDirectory} et la variable " +
            "ASPNETCORE_ENVIRONMENT du service (doit valoir Production).");
    }

    Log.Information(
        "Démarrage — service={IsService}, environnement={Env}, racine={Root}",
        isWindowsService, builder.Environment.EnvironmentName, builder.Environment.ContentRootPath);

    // Hangfire branché sur la base SQL Server [Juniro_Evo]
    builder.Services.AddHangfire(config => config
        .SetDataCompatibilityLevel(CompatibilityLevel.Version_180)
        .UseSimpleAssemblyNameTypeSerializer()
        .UseRecommendedSerializerSettings()
        .UseSqlServerStorage(connString));

    // Un seul worker : évite que deux instances Chromium se chevauchent
    builder.Services.AddHangfireServer(options => options.WorkerCount = 1);

    // Adresse d'écoute surchargeable par configuration (utile si le port 5000 est déjà pris)
    string urls = builder.Configuration["Hosting:Urls"] ?? "http://0.0.0.0:5000";
    builder.WebHost.UseUrls(urls);

    var app = builder.Build();

    // Dashboard Web sur http://IP_PC_FIXE:5000/hangfire
    app.UseHangfireDashboard("/hangfire", new DashboardOptions
    {
        Authorization = new[] { new LocalNetworkAuthFilter() }
    });

    // Tâche planifiée journalière (J-1 à J+3), 03h00 heure locale.
    // Hangfire réessaie automatiquement jusqu'à 3 fois en cas d'échec.
    using (var scope = app.Services.CreateScope())
    {
        var recurringJobManager = scope.ServiceProvider.GetRequiredService<IRecurringJobManager>();

        recurringJobManager.AddOrUpdate<FlashscoreScraperJob>(
            recurringJobId: "daily-flashscore-scrape",
            methodCall: job => job.ExecuteDailyScrapeAsync(),
            cronExpression: "0 3 * * *",
            options: new RecurringJobOptions { TimeZone = TimeZoneInfo.Local });

        // Rattrapage automatique des trous de données, après le journalier et avant les calculs :
        // matchs jamais enregistrés de la saison + matchs incomplets des 10 derniers jours.
        // Actif par défaut ; "Repair:Enabled": false dans appsettings.Production.json pour l'arrêter.
        var repair = app.Configuration.GetSection("Repair");
        if (repair.GetValue("Enabled", true))
        {
            recurringJobManager.AddOrUpdate<FlashscoreScraperJob>(
                recurringJobId: "repair-recent-matches",
                methodCall: job => job.ExecuteRecentRepairAsync(),
                cronExpression: repair["Cron"] ?? "30 4 * * *",
                options: new RecurringJobOptions { TimeZone = TimeZoneInfo.Local });
            Log.Information("Rattrapage automatique planifié ({Cron}).", repair["Cron"] ?? "30 4 * * *");
        }
        else
        {
            recurringJobManager.RemoveIfExists("repair-recent-matches");
        }

        // Calculs (variables, prédictions, paris proposés), après le scraping.
        // Désactivé tant que "Analytics:Enabled" n'est pas à true dans appsettings.Production.json.
        var analytics = app.Configuration.GetSection("Analytics");
        if (analytics.GetValue("Enabled", false))
        {
            recurringJobManager.AddOrUpdate<AnalyticsJob>(
                recurringJobId: "daily-analytics",
                methodCall: job => job.RunDailyAsync(),
                cronExpression: analytics["Cron"] ?? "30 5 * * *",
                options: new RecurringJobOptions { TimeZone = TimeZoneInfo.Local });
            Log.Information("Calculs Analytics planifiés ({Cron}).", analytics["Cron"] ?? "30 5 * * *");
        }
        else
        {
            recurringJobManager.RemoveIfExists("daily-analytics");
        }

        // Copie vers Supabase (application mobile), après les calculs.
        // Désactivé tant que "Supabase:Enabled" n'est pas à true (DOCUMENTATION.md, chapitre 13).
        var supabase = app.Configuration.GetSection("Supabase");
        if (supabase.GetValue("Enabled", false))
        {
            recurringJobManager.AddOrUpdate<SupabaseSyncJob>(
                recurringJobId: "supabase-sync",
                methodCall: job => job.RunAsync(),
                cronExpression: supabase["Cron"] ?? "15 6 * * *",
                options: new RecurringJobOptions { TimeZone = TimeZoneInfo.Local });
            Log.Information("Envoi vers Supabase planifié ({Cron}).", supabase["Cron"] ?? "15 6 * * *");
        }
        else
        {
            recurringJobManager.RemoveIfExists("supabase-sync");
        }
    }

    app.MapGet("/", () => Results.Redirect("/hangfire"));

    Log.Information("Écoute sur {Urls}", urls);
    await app.RunAsync();
    return 0;
}
catch (Exception ex)
{
    Log.Fatal(ex, "Arrêt anormal au démarrage de JuniroScraperService.");
    return 1;
}
finally
{
    Log.CloseAndFlush();
}
