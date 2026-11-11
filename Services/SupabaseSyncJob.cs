// =============================================================================
// SupabaseSyncJob.cs — COPIE DE LA BASE LOCALE VERS SUPABASE (application mobile)
// -----------------------------------------------------------------------------
// Rôle : chaque matin (06h15 par défaut, après le scraping de 03h00 et les calculs de
//        05h30), lire les vues d'export app.vx_Supa* de Juniro_Evo et recopier leur
//        contenu dans les tables Supabase du même nom. L'application MAUI lit Supabase.
//
// Tables envoyées (voir TABLES plus bas) :
//   matches, predictions, match_stats  -> mise à jour par clé (INSERT ... ON CONFLICT)
//   strategy_reports, signals          -> remplacées entièrement (petites tables)
//   execution_runs, execution_logs     -> remplacées entièrement (journal des 30 / 14 derniers
//                                         jours, affiché dans l'application). FACULTATIVES : si la
//                                         vue locale ou la table Supabase manque, avertissement
//                                         dans le journal et la copie continue.
// Les paris saisis dans l'application (user_bets) ne sont JAMAIS touchés.
//
// Les colonnes envoyées sont celles des vues (mêmes noms que dans Supabase) : pour
// ajouter une colonne, l'ajouter dans SQL\5_Analytique\A8_ExportSupabase.sql ET dans
// la table Supabase (SQL\6_Supabase\001_schema.sql). Aucun changement de code ici.
//
// Journal : une exécution « SupabaseSync » dans dbo.AppExecutionRuns (statut, durée,
// lignes envoyées) et une ligne par table dans dbo.AppExecutionLogs ; une ligne dans
// la table Supabase sync_log (date de dernière mise à jour affichée dans l'application).
//
// Configuration (appsettings.Production.json, section "Supabase") :
//   Enabled           false par défaut
//   ConnectionString  chaîne Npgsql du « Session pooler » Supabase (mot de passe compris)
//   Cron              par défaut "15 6 * * *"
//   BatchRows         lignes par requête, par défaut 500
// Documentation : DOCUMENTATION.md, chapitre 13.
// =============================================================================
using System.Data;
using System.Diagnostics;
using System.Text;
using System.Text.Json;
using Hangfire;
using Juniro.ScraperService.Data;
using Microsoft.Data.SqlClient;
using Npgsql;
using NpgsqlTypes;

namespace Juniro.ScraperService.Services;

/// <summary>Recopie les vues d'export de Juniro_Evo dans les tables Supabase.</summary>
public class SupabaseSyncJob
{
    /// <summary>
    /// Une table à envoyer : vue locale, table Supabase, clé, remplacement complet ou non.
    /// Optional : une erreur sur cette table est notée en avertissement sans arrêter la copie.
    /// </summary>
    private sealed record SyncTable(string LocalView, string RemoteTable, string[] Keys, bool ReplaceAll, bool Optional = false);

    // L'ordre compte : matches d'abord (les paris saisis y font référence).
    private static readonly SyncTable[] TABLES =
    {
        new("app.vx_SupaMatches",         "matches",          new[] { "match_id" },                                   false),
        new("app.vx_SupaPredictions",     "predictions",      new[] { "match_id" },                                   false),
        new("app.vx_SupaMatchStats",      "match_stats",      new[] { "match_id" },                                   false),
        new("app.vx_SupaStrategyReports", "strategy_reports", new[] { "family", "league", "season" },                 true),
        new("app.vx_SupaSignals",         "signals",          new[] { "match_id", "strategy", "market", "selection" }, true),
        new("app.vx_SupaExecutionRuns",   "execution_runs",   new[] { "run_id" },                                     true, Optional: true),
        new("app.vx_SupaExecutionLogs",   "execution_logs",   new[] { "run_id", "seq" },                              true, Optional: true),
    };

    private readonly IConfiguration _config;
    private readonly JuniroRepository _repository;
    private readonly ILogger<SupabaseSyncJob> _logger;

    /// <summary>Configuration (sections ConnectionStrings et "Supabase"), journal SQL et Serilog.</summary>
    public SupabaseSyncJob(IConfiguration config, JuniroRepository repository, ILogger<SupabaseSyncJob> logger)
    {
        _config = config;
        _repository = repository;
        _logger = logger;
    }

    /// <summary>Point d'entrée Hangfire (job récurrent « supabase-sync », ou à la main depuis le tableau de bord).</summary>
    [AutomaticRetry(Attempts = 2)]
    [DisableConcurrentExecution(timeoutInSeconds: 1800)]
    public async Task RunAsync()
    {
        var section = _config.GetSection("Supabase");
        string remote = section["ConnectionString"] ?? "";
        string local = _config.GetConnectionString("JuniroDb") ?? "";
        int batchRows = Math.Max(1, section.GetValue("BatchRows", 500));
        if (string.IsNullOrWhiteSpace(remote) || remote.Contains("A_RENSEIGNER"))
            throw new InvalidOperationException("Supabase:ConnectionString est vide : voir DOCUMENTATION.md, chapitre 13.");

        int runId = await _repository.StartExecutionRunAsync("SupabaseSync");
        await _repository.LogExecutionAsync(runId, "INFO", "Supabase.Startup", $"Envoi vers Supabase de {TABLES.Length} tables.");
        var watch = Stopwatch.StartNew();
        var startedUtc = DateTime.UtcNow;
        var done = new Dictionary<string, int>();
        string? error = null;

        try
        {
            await using var sql = new SqlConnection(local);
            await sql.OpenAsync();
            await using var pg = new NpgsqlConnection(remote);
            await pg.OpenAsync();

            foreach (var t in TABLES)
            {
                var tw = Stopwatch.StartNew();
                int n;
                if (t.Optional)
                {
                    try
                    {
                        var (oc, ot, orows) = await ReadLocalAsync(sql, t.LocalView);
                        n = await WriteRemoteAsync(pg, t, oc, ot, orows, batchRows);
                    }
                    catch (Exception ex)
                    {
                        // Journal facultatif : vue A8 pas encore rejouée, ou table Supabase absente
                        string warn = $"{t.RemoteTable} non envoyée (facultative) : {ex.Message}";
                        _logger.LogWarning("{Message}", warn);
                        await _repository.LogExecutionAsync(runId, "WARN", "Supabase.Table", warn);
                        continue;
                    }
                }
                else
                {
                    var (columns, types, rows) = await ReadLocalAsync(sql, t.LocalView);
                    (columns, types, rows, var missing) = await KeepRemoteColumnsAsync(pg, t.RemoteTable, columns, types, rows);
                    if (missing.Count > 0)
                    {
                        // Colonne ajoutée dans la vue locale (A8) mais pas encore dans Supabase : on envoie
                        // le reste plutôt que d'échouer. Rejouer le script SQL\6_Supabase manquant.
                        string warn = $"{t.RemoteTable} : colonne(s) absente(s) de Supabase, non envoyée(s) : " +
                                      $"{string.Join(", ", missing)}. Rejouer le dernier script de SQL\\6_Supabase.";
                        _logger.LogWarning("{Message}", warn);
                        await _repository.LogExecutionAsync(runId, "WARN", "Supabase.MissingColumns", warn);
                    }
                    n = await WriteRemoteAsync(pg, t, columns, types, rows, batchRows);
                }
                done[t.RemoteTable] = n;
                string msg = $"{t.RemoteTable} : {n} lignes envoyées ({(t.ReplaceAll ? "remplacement" : "mise à jour")}) en {tw.Elapsed.TotalSeconds:0} s.";
                _logger.LogInformation("{Message}", msg);
                await _repository.LogExecutionAsync(runId, "INFO", "Supabase.Table", msg);
            }

            await WriteSyncLogAsync(pg, startedUtc, "Success", done);
        }
        catch (Exception ex)
        {
            error = ex.Message;
            _logger.LogError(ex, "Envoi vers Supabase en échec.");
            await _repository.LogExecutionAsync(runId, "FATAL", "Supabase.Crash", "Envoi vers Supabase en échec.", null, ex.ToString());
            throw;
        }
        finally
        {
            int total = done.Values.Sum();
            string summary = error == null
                ? $"Supabase à jour en {watch.Elapsed:hh\\:mm\\:ss} : {total} lignes ({string.Join(", ", done.Select(kv => $"{kv.Key} {kv.Value}"))})."
                : $"ÉCHEC après {watch.Elapsed:hh\\:mm\\:ss} : {error} | tables déjà envoyées : " +
                  (done.Count == 0 ? "aucune" : string.Join(", ", done.Select(kv => $"{kv.Key} {kv.Value}")));
            await _repository.LogExecutionAsync(runId, error == null ? "INFO" : "ERROR", "Supabase.Finished", summary);
            await _repository.CompleteExecutionRunAsync(runId, new ExecutionRunResult
            {
                Status = error == null ? ExecutionRunResult.Success
                       : done.Count > 0 ? ExecutionRunResult.Warning : ExecutionRunResult.Failed,
                MatchesFound = total,
                MatchesSaved = total,
                MatchesFailed = error == null ? 0 : TABLES.Length - done.Count,
                Summary = summary,
                Error = error
            });
        }
    }

    /// <summary>Lit toute une vue locale : noms de colonnes, type PostgreSQL de chacune, lignes.</summary>
    private static async Task<(string[] columns, NpgsqlDbType[] types, List<object?[]> rows)> ReadLocalAsync(
        SqlConnection sql, string view)
    {
        await using var cmd = new SqlCommand($"SELECT * FROM {view};", sql) { CommandTimeout = 600 };
        await using var reader = await cmd.ExecuteReaderAsync();
        int n = reader.FieldCount;
        var columns = new string[n];
        var types = new NpgsqlDbType[n];
        for (int i = 0; i < n; i++)
        {
            columns[i] = reader.GetName(i);
            types[i] = MapType(reader.GetFieldType(i), reader.GetDataTypeName(i));
        }
        var rows = new List<object?[]>();
        while (await reader.ReadAsync())
        {
            var values = new object?[n];
            for (int i = 0; i < n; i++)
                values[i] = reader.IsDBNull(i) ? null : Convert(reader.GetValue(i), types[i]);
            rows.Add(values);
        }
        return (columns, types, rows);
    }

    /// <summary>
    /// Ne garde que les colonnes qui existent dans la table Supabase. Renvoie aussi la liste des
    /// colonnes écartées (vue locale plus récente que le schéma Supabase).
    /// </summary>
    private static async Task<(string[] columns, NpgsqlDbType[] types, List<object?[]> rows, List<string> missing)>
        KeepRemoteColumnsAsync(NpgsqlConnection pg, string table, string[] columns, NpgsqlDbType[] types, List<object?[]> rows)
    {
        var remote = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        await using (var cmd = new NpgsqlCommand(
            "SELECT column_name FROM information_schema.columns WHERE table_schema = 'public' AND table_name = @t;", pg))
        {
            cmd.Parameters.Add(new NpgsqlParameter("t", NpgsqlDbType.Text) { Value = table });
            await using var reader = await cmd.ExecuteReaderAsync();
            while (await reader.ReadAsync()) remote.Add(reader.GetString(0));
        }
        var keep = Enumerable.Range(0, columns.Length).Where(i => remote.Contains(columns[i])).ToArray();
        var missing = columns.Where(c => !remote.Contains(c)).ToList();
        if (missing.Count == 0 || remote.Count == 0) return (columns, types, rows, new List<string>());
        return (keep.Select(i => columns[i]).ToArray(),
                keep.Select(i => types[i]).ToArray(),
                rows.Select(r => keep.Select(i => r[i]).ToArray()).ToList(),
                missing);
    }

    /// <summary>Type PostgreSQL correspondant à une colonne SQL Server.</summary>
    private static NpgsqlDbType MapType(Type t, string sqlTypeName)
    {
        if (t == typeof(long)) return NpgsqlDbType.Bigint;
        if (t == typeof(int)) return NpgsqlDbType.Integer;
        if (t == typeof(short) || t == typeof(byte)) return NpgsqlDbType.Smallint;
        if (t == typeof(decimal)) return NpgsqlDbType.Numeric;
        if (t == typeof(double)) return NpgsqlDbType.Double;
        if (t == typeof(float)) return NpgsqlDbType.Real;
        if (t == typeof(bool)) return NpgsqlDbType.Boolean;
        if (t == typeof(Guid)) return NpgsqlDbType.Uuid;
        if (t == typeof(DateTimeOffset)) return NpgsqlDbType.TimestampTz;
        if (t == typeof(DateTime))
            return string.Equals(sqlTypeName, "date", StringComparison.OrdinalIgnoreCase)
                ? NpgsqlDbType.Date : NpgsqlDbType.TimestampTz;
        return NpgsqlDbType.Text;
    }

    /// <summary>Valeur prête pour Npgsql : dates en UTC, octets en entier court, texte pour le reste.</summary>
    private static object Convert(object v, NpgsqlDbType type) => type switch
    {
        NpgsqlDbType.Date => DateOnly.FromDateTime((DateTime)v),
        // Les vues d'export donnent toutes leurs heures en UTC
        NpgsqlDbType.TimestampTz when v is DateTime d => DateTime.SpecifyKind(d, DateTimeKind.Utc),
        NpgsqlDbType.TimestampTz when v is DateTimeOffset o => o.UtcDateTime,
        NpgsqlDbType.Smallint when v is byte b => (short)b,
        NpgsqlDbType.Text when v is not string => System.Convert.ToString(v, System.Globalization.CultureInfo.InvariantCulture) ?? "",
        _ => v
    };

    /// <summary>
    /// Écrit les lignes dans Supabase, par paquets, dans une transaction :
    /// remplacement complet (DELETE puis INSERT) ou mise à jour par clé (ON CONFLICT DO UPDATE).
    /// </summary>
    private static async Task<int> WriteRemoteAsync(NpgsqlConnection pg, SyncTable t, string[] columns,
        NpgsqlDbType[] types, List<object?[]> rows, int batchRows)
    {
        string colList = string.Join(", ", columns.Select(Quote));
        var updatable = columns.Where(c => !t.Keys.Contains(c, StringComparer.OrdinalIgnoreCase)).ToArray();
        string conflict = $" ON CONFLICT ({string.Join(", ", t.Keys.Select(Quote))}) DO UPDATE SET "
                          + string.Join(", ", updatable.Select(c => $"{Quote(c)} = EXCLUDED.{Quote(c)}"))
                          + (updatable.Length > 0 ? ", " : "") + "synced_at = now()";
        // Une requête PostgreSQL accepte au plus 65 535 paramètres
        int perBatch = Math.Max(1, Math.Min(batchRows, 60000 / Math.Max(1, columns.Length)));

        await using var tx = await pg.BeginTransactionAsync();
        if (t.ReplaceAll)
        {
            await using var del = new NpgsqlCommand($"DELETE FROM public.{Quote(t.RemoteTable)};", pg, tx);
            await del.ExecuteNonQueryAsync();
        }

        int written = 0;
        for (int start = 0; start < rows.Count; start += perBatch)
        {
            var batch = rows.Skip(start).Take(perBatch).ToList();
            var sb = new StringBuilder($"INSERT INTO public.{Quote(t.RemoteTable)} ({colList}) VALUES ");
            await using var cmd = new NpgsqlCommand { Connection = pg, Transaction = tx, CommandTimeout = 300 };
            for (int r = 0; r < batch.Count; r++)
            {
                sb.Append(r == 0 ? "(" : ", (");
                for (int c = 0; c < columns.Length; c++)
                {
                    string name = $"p{r}_{c}";
                    sb.Append(c == 0 ? "@" : ", @").Append(name);
                    cmd.Parameters.Add(new NpgsqlParameter(name, types[c]) { Value = batch[r][c] ?? DBNull.Value });
                }
                sb.Append(')');
            }
            sb.Append(conflict).Append(';');
            cmd.CommandText = sb.ToString();
            written += await cmd.ExecuteNonQueryAsync();
        }
        await tx.CommitAsync();
        return written;
    }

    /// <summary>Ligne de suivi dans la table Supabase sync_log (affichée dans l'application).</summary>
    private static async Task WriteSyncLogAsync(NpgsqlConnection pg, DateTime startedUtc, string status, Dictionary<string, int> done)
    {
        await using var cmd = new NpgsqlCommand(
            "INSERT INTO public.sync_log (started_at, finished_at, status, details) VALUES (@s, now(), @st, @d);", pg);
        cmd.Parameters.Add(new NpgsqlParameter("s", NpgsqlDbType.TimestampTz) { Value = startedUtc });
        cmd.Parameters.Add(new NpgsqlParameter("st", NpgsqlDbType.Text) { Value = status });
        cmd.Parameters.Add(new NpgsqlParameter("d", NpgsqlDbType.Jsonb) { Value = JsonSerializer.Serialize(done) });
        await cmd.ExecuteNonQueryAsync();
    }

    /// <summary>Nom de colonne ou de table entre guillemets PostgreSQL.</summary>
    private static string Quote(string name) => "\"" + name.Replace("\"", "\"\"") + "\"";
}
