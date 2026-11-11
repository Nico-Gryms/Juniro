using Juniro.App.Models;

namespace Juniro.App.Services;

/// <summary>
/// Toutes les lectures et écritures de l'application, regroupées ici. Chaque méthode
/// correspond à une table ou une vue Supabase (SQL\6_Supabase\001_schema.sql).
/// </summary>
public sealed class DataService
{
    private readonly SupabaseClient _api;

    public DataService(SupabaseClient api) => _api = api;

    public Guid? CurrentUserId => _api.UserId;
    public string? CurrentEmail => _api.Email;

    // ------------------------------------------------------------------ matchs

    /// <summary>Tous les matchs d'une journée (heure du téléphone) : terminés, joués et à venir.</summary>
    public Task<List<MatchItem>> GetMatchesForDayAsync(DateTime localDay)
    {
        var day = DateTime.SpecifyKind(localDay.Date, DateTimeKind.Unspecified);
        var from = new DateTimeOffset(day, TimeZoneInfo.Local.GetUtcOffset(day));
        var to = from.AddDays(1);
        return _api.GetAsync<MatchItem>("v_matches",
            $"select=*&kickoff_utc=gte.{SupabaseClient.Iso(from)}&kickoff_utc=lt.{SupabaseClient.Iso(to)}&order=kickoff_utc.asc,league.asc");
    }

    public async Task<MatchItem?> GetMatchItemAsync(long matchId) =>
        (await _api.GetAsync<MatchItem>("v_matches", $"select=*&match_id=eq.{matchId}")).FirstOrDefault();

    /// <summary>Forme avant le match de plusieurs matchs en une requête.</summary>
    public async Task<List<MatchStats>> GetMatchStatsForAsync(IEnumerable<long> matchIds)
    {
        var ids = matchIds.Distinct().ToList();
        if (ids.Count == 0) return new List<MatchStats>();
        return await _api.GetAsync<MatchStats>("match_stats", $"select=*&match_id=in.({string.Join(",", ids)})");
    }

    /// <summary>Derniers matchs terminés d'une équipe avant une date (forme récente).</summary>
    public Task<List<MatchItem>> GetTeamFormAsync(string team, DateTimeOffset before, int count = 5)
    {
        string t = SupabaseClient.Esc(team);
        return _api.GetAsync<MatchItem>("v_matches",
            $"select=match_id,league,kickoff_utc,status,state,home_team,away_team,home_goals,away_goals,home_xg,away_xg" +
            $"&status=eq.Finished&kickoff_utc=lt.{SupabaseClient.Iso(before)}" +
            $"&or=(home_team.eq.%22{t}%22,away_team.eq.%22{t}%22)&order=kickoff_utc.desc&limit={count}");
    }

    // ------------------------------------------------------------------ ligues, classements, Elo

    public Task<List<LeagueInfo>> GetLeaguesAsync() => _api.GetAsync<LeagueInfo>("v_leagues", "select=*");

    public Task<List<LeagueStats>> GetLeagueStatsAsync() => _api.GetAsync<LeagueStats>("v_league_stats", "select=*");

    /// <summary>Classement d'une ligue. scope : all, home, away, last5.</summary>
    public Task<List<StandingRow>> GetStandingsAsync(string league, string scope) =>
        _api.RpcAsync<StandingRow>("league_table", new { p_league = league, p_scope = scope });

    public Task<List<EloRow>> GetEloAsync() => _api.GetAsync<EloRow>("v_elo", "select=*&order=elo.desc&limit=1000");

    public async Task<MatchRow?> GetMatchAsync(long matchId) =>
        (await _api.GetAsync<MatchRow>("matches", $"select=*&match_id=eq.{matchId}")).FirstOrDefault();

    public async Task<PredictionRow?> GetPredictionAsync(long matchId) =>
        (await _api.GetAsync<PredictionRow>("predictions", $"select=*&match_id=eq.{matchId}")).FirstOrDefault();

    public async Task<MatchStats?> GetMatchStatsAsync(long matchId) =>
        (await _api.GetAsync<MatchStats>("match_stats", $"select=*&match_id=eq.{matchId}")).FirstOrDefault();

    public Task<List<SignalRow>> GetSignalsForMatchAsync(long matchId) =>
        _api.GetAsync<SignalRow>("v_signals", $"select=*&match_id=eq.{matchId}");

    // ------------------------------------------------------------------ résultats des prédictions

    public Task<List<PredictionKpi>> GetKpisAsync(DateTimeOffset? from, DateTimeOffset? to) =>
        _api.RpcAsync<PredictionKpi>("prediction_kpis", new
        {
            p_from = from?.ToUniversalTime().ToString("yyyy-MM-dd'T'HH:mm:ss'Z'"),
            p_to = to?.ToUniversalTime().ToString("yyyy-MM-dd'T'HH:mm:ss'Z'")
        });

    public Task<List<PredictionResult>> GetResultsAsync(DateTimeOffset? from, string? league, int limit = 150)
    {
        string q = $"select=*&order=kickoff_utc.desc&limit={limit}";
        if (from != null) q += $"&kickoff_utc=gte.{SupabaseClient.Iso(from.Value)}";
        if (!string.IsNullOrEmpty(league)) q += $"&league=eq.{SupabaseClient.Esc(league)}";
        return _api.GetAsync<PredictionResult>("v_prediction_results", q);
    }

    // ------------------------------------------------------------------ stratégies

    public Task<List<StrategyOverview>> GetStrategyOverviewAsync() =>
        _api.GetAsync<StrategyOverview>("v_strategy_overview", "select=*&order=roi_test.desc.nullslast");

    public Task<List<StrategyReport>> GetStrategySeasonsAsync(string family, string league) =>
        _api.GetAsync<StrategyReport>("strategy_reports",
            $"select=*&family=eq.{SupabaseClient.Esc(family)}&league=eq.{SupabaseClient.Esc(league)}&order=sort_order.asc,season.asc");

    public Task<List<PaperSummary>> GetPaperSummaryAsync() =>
        _api.GetAsync<PaperSummary>("v_paper_summary", "select=*&order=strategy.asc");

    /// <summary>Tous les paris fictifs (historique des stratégies).</summary>
    public Task<List<SignalRow>> GetAllSignalsAsync() =>
        _api.GetAsync<SignalRow>("v_signals", "select=*&order=kickoff_utc.desc&limit=5000");

    public Task<List<SignalRow>> GetSignalsForStrategyAsync(string strategy) =>
        _api.GetAsync<SignalRow>("v_signals",
            $"select=*&strategy=eq.{SupabaseClient.Esc(strategy)}&order=kickoff_utc.desc&limit=300");

    // ------------------------------------------------------------------ paris saisis

    public Task<List<UserBetRow>> GetBetsAsync() =>
        _api.GetAsync<UserBetRow>("v_user_bets", "select=*&order=kickoff_utc.asc,placed_at.asc&limit=5000");

    public Task<List<UserBetRow>> GetBetsForMatchAsync(long matchId) =>
        _api.GetAsync<UserBetRow>("v_user_bets", $"select=*&match_id=eq.{matchId}&order=placed_at.asc");

    public async Task<UserBetRow?> GetBetAsync(Guid id) =>
        (await _api.GetAsync<UserBetRow>("v_user_bets", $"select=*&id=eq.{id}")).FirstOrDefault();

    public Task AddBetAsync(UserBetInput bet) => _api.InsertAsync("user_bets", bet);

    public Task UpdateBetAsync(Guid id, UserBetInput bet) => _api.UpdateAsync("user_bets", $"id=eq.{id}", bet);

    public Task DeleteBetAsync(Guid id) => _api.DeleteAsync("user_bets", $"id=eq.{id}");

    // ------------------------------------------------------------------ comptes et suivi

    public Task<List<Profile>> GetProfilesAsync() => _api.GetAsync<Profile>("profiles", "select=*");

    public Task UpdateDisplayNameAsync(string name) =>
        _api.UpdateAsync("profiles", $"id=eq.{_api.UserId}", new { display_name = name });

    public Task<List<SyncInfo>> GetSyncsAsync(int count = 20) =>
        _api.GetAsync<SyncInfo>("sync_log", $"select=*&order=id.desc&limit={count}");

    // ------------------------------------------------------------------ journal du service

    /// <summary>Exécutions récentes du service (scrap, rattrapage, copie), la plus récente en haut.</summary>
    public Task<List<ExecutionRun>> GetRunsAsync(int count = 60) =>
        _api.GetAsync<ExecutionRun>("execution_runs", $"select=*&order=started_utc.desc&limit={count}");

    /// <summary>Dernier scrap quotidien.</summary>
    public async Task<ExecutionRun?> GetLastScrapeAsync() =>
        (await _api.GetAsync<ExecutionRun>("execution_runs", "select=*&source=eq.ScheduledTask&order=started_utc.desc&limit=1")).FirstOrDefault();

    public async Task<ExecutionRun?> GetRunAsync(int runId) =>
        (await _api.GetAsync<ExecutionRun>("execution_runs", $"select=*&run_id=eq.{runId}")).FirstOrDefault();

    /// <summary>Événements d'une exécution, dans l'ordre.</summary>
    public Task<List<ExecutionLog>> GetRunLogsAsync(int runId) =>
        _api.GetAsync<ExecutionLog>("execution_logs", $"select=*&run_id=eq.{runId}&order=seq.asc&limit=5000");

    /// <summary>
    /// État du service pour l'en-tête : dernier scrap et dernière copie. Si le journal n'est pas
    /// encore dans Supabase, l'état est calculé sur la copie seule.
    /// </summary>
    public async Task<ServiceHealth> GetHealthAsync()
    {
        var sync = await GetLastSyncAsync();
        ExecutionRun? scrape = null;
        bool journal = true;
        try { scrape = await GetLastScrapeAsync(); }
        catch (InvalidOperationException) { journal = false; }
        return Journal.Health(scrape, sync, journal);
    }

    public async Task<SyncInfo?> GetLastSyncAsync() =>
        (await _api.GetAsync<SyncInfo>("sync_log", "select=*&order=id.desc&limit=1")).FirstOrDefault();

    /// <summary>Texte « Données du lundi 9 novembre à 06:16 » pour les en-têtes.</summary>
    public async Task<string> GetSyncTextAsync()
    {
        try
        {
            var s = await GetLastSyncAsync();
            if (s?.FinishedAt is not { } f) return "Données : jamais synchronisées";
            var local = f.ToLocalTime();
            return $"Données du {local.ToString("dddd d MMMM 'à' HH:mm", Fmt.Fr)}";
        }
        catch
        {
            return "";
        }
    }
}
