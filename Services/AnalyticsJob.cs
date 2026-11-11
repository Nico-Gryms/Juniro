// =============================================================================
// AnalyticsJob.cs — LANCEMENT DU MOTEUR DE CALCUL PYTHON
// -----------------------------------------------------------------------------
// Rôle : chaque matin (05h30 par défaut), exécuter
//          C:\Juniro\analytics\.venv\Scripts\python.exe -m juniro_analytics daily
//        et recopier sa sortie dans les logs du service.
// Le travail réel (variables, modèles, prédictions, paris proposés) est fait en Python :
// voir le dossier Analytics\ et DOCUMENTATION.md, chapitres 5 à 8.
// Échec si Python est introuvable, si le calcul dépasse TimeoutMinutes, ou si Python
// renvoie un code d'erreur (les 4 dernières lignes de sa sortie sont reprises).
// =============================================================================
using System.Diagnostics;
using System.Text;
using Hangfire;

namespace Juniro.ScraperService.Services;

/// <summary>
/// Lance le moteur Python (dossier Analytics) : recalcul des variables, prédictions des
/// matchs à venir et paris proposés. Le service ne fait qu'orchestrer ; tous les calculs
/// sont dans Python, qui lit et écrit directement dans Juniro_Evo.
///
/// Configuration (appsettings.Production.json, section "Analytics") :
///   Enabled         false par défaut : rien ne tourne tant que Python n'est pas installé
///   Directory       dossier du moteur, par défaut C:\Juniro\analytics
///   PythonExe       par défaut .venv\Scripts\python.exe dans ce dossier
///   Cron            par défaut "30 5 * * *" (après le scraping de 03h00)
///   TimeoutMinutes  par défaut 90
///
/// Avec un seul worker Hangfire, ce job attend la fin du scraping s'il tourne encore :
/// les deux ne se chevauchent jamais.
/// </summary>
public class AnalyticsJob
{
    private readonly IConfiguration _config;
    private readonly ILogger<AnalyticsJob> _logger;

    /// <summary>Reçoit la configuration (section "Analytics") et le journal du service.</summary>
    public AnalyticsJob(IConfiguration config, ILogger<AnalyticsJob> logger)
    {
        _config = config;
        _logger = logger;
    }

    [AutomaticRetry(Attempts = 1)]
    [DisableConcurrentExecution(timeoutInSeconds: 3600)]
    public Task RunDailyAsync() => RunAsync("daily");

    /// <summary>
    /// Lance « python -m juniro_analytics &lt;command&gt; » dans le dossier du moteur et attend sa fin.
    /// command = daily (quotidien), mais aussi backtest, scan... si on l'appelle à la main.
    /// </summary>
    public async Task RunAsync(string command)
    {
        var section = _config.GetSection("Analytics");
        string dir = section["Directory"] ?? @"C:\Juniro\analytics";
        string python = section["PythonExe"] ?? Path.Combine(".venv", "Scripts", "python.exe");
        if (!Path.IsPathRooted(python)) python = Path.Combine(dir, python);
        int timeoutMinutes = section.GetValue("TimeoutMinutes", 90);

        if (!File.Exists(python))
            throw new InvalidOperationException(
                $"Python introuvable : {python}. Lancez Deploy\\install-analytics.ps1 sur ce PC.");

        var psi = new ProcessStartInfo(python)
        {
            WorkingDirectory = dir,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };
        foreach (var arg in new[] { "-X", "utf8", "-m", "juniro_analytics", command })
            psi.ArgumentList.Add(arg);
        foreach (var arg in (section["ExtraArguments"] ?? "").Split(' ', StringSplitOptions.RemoveEmptyEntries))
            psi.ArgumentList.Add(arg);
        psi.Environment["PYTHONIOENCODING"] = "utf-8";

        var tail = new Queue<string>();
        void Keep(string line)
        {
            lock (tail)
            {
                tail.Enqueue(line);
                if (tail.Count > 15) tail.Dequeue();
            }
        }

        using var proc = new Process { StartInfo = psi };
        proc.OutputDataReceived += (_, e) =>
        {
            if (e.Data is null) return;
            _logger.LogInformation("[analytics] {Line}", e.Data);
            Keep(e.Data);
        };
        proc.ErrorDataReceived += (_, e) =>
        {
            if (e.Data is null) return;
            _logger.LogWarning("[analytics] {Line}", e.Data);
            Keep(e.Data);
        };

        _logger.LogInformation("Analytics : {Python} -m juniro_analytics {Command} (dossier {Dir})", python, command, dir);
        var watch = Stopwatch.StartNew();
        proc.Start();
        proc.BeginOutputReadLine();
        proc.BeginErrorReadLine();

        using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(timeoutMinutes));
        try
        {
            await proc.WaitForExitAsync(cts.Token);
        }
        catch (OperationCanceledException)
        {
            try { proc.Kill(entireProcessTree: true); } catch { /* déjà terminé */ }
            throw new TimeoutException($"Analytics interrompu après {timeoutMinutes} min.");
        }

        if (proc.ExitCode != 0)
        {
            string last;
            lock (tail) last = string.Join(" | ", tail.TakeLast(4));
            throw new InvalidOperationException($"Analytics a échoué (code {proc.ExitCode}) : {last}");
        }

        _logger.LogInformation("Analytics terminé en {Minutes:0.0} min.", watch.Elapsed.TotalMinutes);
    }
}
