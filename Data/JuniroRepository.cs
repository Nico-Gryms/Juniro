// =============================================================================
// JuniroRepository.cs — ÉCRITURE DES DONNÉES SCRAPÉES DANS SQL SERVER
// -----------------------------------------------------------------------------
// Rôle : seul endroit du service qui écrit dans les tables brutes dbo.* (avec Dapper).
//
// Enregistrement d'un match (SaveFullMatchDataAsync), dans UNE transaction :
//   1. Ligue + saison  -> dbo.Leagues / dbo.Seasons  (saison « 2026-2027 », bascule au 1er juillet)
//   2. Équipes         -> dbo.Teams (repérées par leur clé Flashscore)
//   3. Match           -> dbo.Matches (date en heure de Paris, statut, scores, journée)
//                         Un match « Finished » n'est jamais rétrogradé ; les valeurs vides
//                         n'écrasent pas les valeurs déjà connues (COALESCE).
//   4. Statistiques    -> dbo.MatchTeamStats (une ligne par équipe)
//   5. Cotes           -> dbo.Odds (un relevé = une ligne de 15 cotes, seulement si elles ont changé)
//                      -> dbo.OddsQuotes (une ligne par sélection, avec son étiquette
//                         OPENING / J-3 / J-2 / J-1 / DAY / H-1 / CLOSING)
//   En cas d'erreur, tout est annulé : jamais de match à moitié écrit.
//
// Autres fonctions : journal d'exécution (AppExecutionRuns / AppExecutionLogs) et liste des
// matchs déjà complets (pour que le rattrapage ne refasse pas le travail).
//
// Journal d'exécution (dernière section du fichier) :
//   - StartExecutionRunAsync    ouvre une ligne AppExecutionRuns (Status = 'Running') ;
//   - LogExecutionAsync         une ligne AppExecutionLogs par événement. NE LÈVE JAMAIS
//                               d'exception : un journal en panne ne fait pas échouer un match ;
//   - CompleteExecutionRunAsync ferme la ligne : Status (Success / Warning / Failed), heure de
//                               fin, durée, compteurs, résumé par ligue.
//   Les colonnes sont lues dans la base au premier appel : le code s'adapte à la table existante
//   et remplit celles ajoutées par SQL/2_Corrections/C5_JournalExecutions.sql.
// Documentation : DOCUMENTATION.md, chapitre 4.
// =============================================================================
using Dapper;
using Juniro.ScraperService.Configuration;
using Juniro.ScraperService.Models;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Logging;
using System.Collections.Concurrent;

namespace Juniro.ScraperService.Data;

/// <summary>Accès SQL Server du scraper (lecture de la chaîne « JuniroDb » dans appsettings).</summary>
public class JuniroRepository
{
    private readonly string _connectionString;
    private readonly ILogger? _logger;

    /// <summary>
    /// Récupère la chaîne de connexion ; arrêt immédiat si elle est absente.
    /// Le logger sert à signaler dans le fichier Serilog qu'une écriture du journal SQL a échoué.
    /// </summary>
    public JuniroRepository(IConfiguration configuration, ILogger<JuniroRepository>? logger = null)
    {
        _connectionString = configuration.GetConnectionString("JuniroDb")
            ?? throw new InvalidOperationException("Connection string 'JuniroDb' introuvable.");
        _logger = logger;
    }

    /// <summary>
    /// Clés Flashscore des matchs déjà complets en base : terminés, score renseigné et
    /// statistiques présentes pour les deux équipes. Sert au backfill pour reprendre là
    /// où il s'est arrêté sans rescraper ce qui est déjà acquis.
    /// </summary>
    public async Task<HashSet<string>> GetCompletedMatchKeysAsync()
    {
        await using var conn = new SqlConnection(_connectionString);

        // "Complet" = tout ce que le scraper sait collecter est présent : scores, mi-temps,
        // statistiques détaillées des deux équipes et marchés de cotes principaux. Un match
        // incomplet est donc repris automatiquement au prochain backfill, sans --force.
        // Les matchs annulés ou interrompus ne produiront jamais de statistiques : rangés ici
        // pour ne pas être rescrapés à chaque exécution. Les reportés seront rejoués : absents.
        const string sql = @"
            SELECT m.[FlashscoreMatchKey]
            FROM [dbo].[Matches] m
            WHERE (
                      m.[MatchStatus] = 'Finished'
                  AND m.[HomeScoreFT] IS NOT NULL AND m.[AwayScoreFT] IS NOT NULL
                  AND m.[HomeScoreHT] IS NOT NULL AND m.[AwayScoreHT] IS NOT NULL
                  AND (SELECT COUNT(*) FROM [dbo].[MatchTeamStats] s
                       WHERE s.[MatchId] = m.[MatchId]
                         AND s.[ShotsOnTarget] IS NOT NULL
                         AND s.[Corners] IS NOT NULL) = 2
                  AND EXISTS (SELECT 1 FROM [dbo].[Odds] o
                              WHERE o.[MatchId] = m.[MatchId]
                                AND o.[OddHome] IS NOT NULL
                                AND o.[OddOv25] IS NOT NULL
                                AND o.[OddBTTS] IS NOT NULL)
                  )
               OR m.[MatchStatus] IN ('Cancelled', 'Interrupted');";

        var keys = await conn.QueryAsync<string>(sql);
        return new HashSet<string>(keys, StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Toutes les clés de match connues en base, avec leur date (heure de Paris).
    /// Sert au rattrapage automatique : une clé absente de ce dictionnaire est un match jamais
    /// enregistré (trou de données), qu'il faut récupérer quelle que soit sa date.
    /// </summary>
    public async Task<Dictionary<string, DateTime>> GetKnownMatchDatesAsync()
    {
        await using var conn = new SqlConnection(_connectionString);
        var rows = await conn.QueryAsync<RepairCandidate>(
            "SELECT [FlashscoreMatchKey] AS [Key], [MatchDate] FROM [dbo].[Matches] WHERE [FlashscoreMatchKey] IS NOT NULL;");
        var result = new Dictionary<string, DateTime>(StringComparer.OrdinalIgnoreCase);
        foreach (var r in rows) result[r.Key] = r.MatchDate;
        return result;
    }

    /// <summary>
    /// Matchs déjà en base qu'il faut repasser : coup d'envoi entre <paramref name="from"/> et
    /// <paramref name="to"/> (heure de Paris), et incomplets au sens de GetCompletedMatchKeysAsync
    /// (statut non terminé alors que le match est passé, score, mi-temps, statistiques ou cotes
    /// manquants). Les matchs reportés, annulés ou interrompus sont exclus : ils seront repris par
    /// le journalier à leur nouvelle date. Reason résume ce qui manque (journal).
    /// </summary>
    public async Task<List<RepairCandidate>> GetRepairCandidatesAsync(DateTime from, DateTime to)
    {
        await using var conn = new SqlConnection(_connectionString);
        const string sql = @"
            SELECT m.[FlashscoreMatchKey] AS [Key], l.[LeagueName], m.[MatchDate],
                   CONCAT_WS(', ',
                       CASE WHEN m.[MatchStatus] <> 'Finished' THEN CONCAT('statut ', m.[MatchStatus]) END,
                       CASE WHEN m.[HomeScoreFT] IS NULL OR m.[AwayScoreFT] IS NULL THEN 'score' END,
                       CASE WHEN m.[HomeScoreHT] IS NULL OR m.[AwayScoreHT] IS NULL THEN 'mi-temps' END,
                       CASE WHEN st.Nb < 2 THEN 'statistiques' END,
                       CASE WHEN od.Nb = 0 THEN 'cotes' END) AS [Reason]
            FROM [dbo].[Matches] m
            JOIN [dbo].[Seasons] s ON s.[SeasonId] = m.[SeasonId]
            JOIN [dbo].[Leagues] l ON l.[LeagueId] = s.[LeagueId]
            CROSS APPLY (SELECT COUNT(*) AS Nb FROM [dbo].[MatchTeamStats] t
                         WHERE t.[MatchId] = m.[MatchId]
                           AND t.[ShotsOnTarget] IS NOT NULL AND t.[Corners] IS NOT NULL) st
            CROSS APPLY (SELECT COUNT(*) AS Nb FROM [dbo].[Odds] o
                         WHERE o.[MatchId] = m.[MatchId]
                           AND o.[OddHome] IS NOT NULL AND o.[OddOv25] IS NOT NULL AND o.[OddBTTS] IS NOT NULL) od
            WHERE m.[MatchDate] >= @From AND m.[MatchDate] <= @To
              AND m.[FlashscoreMatchKey] IS NOT NULL
              AND m.[MatchStatus] NOT IN ('Postponed', 'Cancelled', 'Interrupted')
              AND (   m.[MatchStatus] <> 'Finished'
                   OR m.[HomeScoreFT] IS NULL OR m.[AwayScoreFT] IS NULL
                   OR m.[HomeScoreHT] IS NULL OR m.[AwayScoreHT] IS NULL
                   OR st.Nb < 2 OR od.Nb = 0)
            ORDER BY m.[MatchDate];";
        var rows = await conn.QueryAsync<RepairCandidate>(sql, new { From = from, To = to });
        return rows.ToList();
    }

    /// <summary>
    /// Enregistre un match complet (ligue, saison, équipes, match, statistiques, cotes) dans une
    /// seule transaction. Appelé pour chaque match par le job journalier et par le rattrapage.
    /// </summary>
    public async Task SaveFullMatchDataAsync(ScrapedMatchData data)
    {
        await using var conn = new SqlConnection(_connectionString);
        await conn.OpenAsync();
        await using var transaction = await conn.BeginTransactionAsync();

        try
        {
            // 1. Ligue & Saison courante
            int seasonId = await GetOrCreateLeagueSeasonIdAsync(
                conn, transaction, data.CountryName, data.LeagueName, data.TierLevel, data.MatchDate);

            // 2. Équipes Domicile & Extérieur
            int homeTeamId = await GetOrCreateTeamAsync(conn, transaction, data.HomeTeamName, data.HomeTeamKey, data.CountryName);
            int awayTeamId = await GetOrCreateTeamAsync(conn, transaction, data.AwayTeamName, data.AwayTeamKey, data.CountryName);

            // 3. Match (Scores FT, HT, Journée, Statut)
            int matchId = await UpsertMatchAsync(conn, transaction, data, seasonId, homeTeamId, awayTeamId);

            // 4. Statistiques du match (si terminé)
            if (data.HomeStats != null && data.AwayStats != null)
            {
                await UpsertTeamStatsAsync(conn, transaction, matchId, homeTeamId, isHome: true, data.HomeStats);
                await UpsertTeamStatsAsync(conn, transaction, matchId, awayTeamId, isHome: false, data.AwayStats);
            }

            // 5. Cotes dans [dbo].[Odds] ET [dbo].[OddsQuotes]
            if (data.Odds != null && data.Odds.HasAnyOdd)
            {
                int bookmakerId = await EnsureBookmakerExistsAsync(conn, transaction, data.Odds.BookmakerId);
                data.Odds.BookmakerId = bookmakerId;

                // Rattrapage : la table [Odds] suit la convention OddIndex 1 = ouverture, N = clôture.
                // L'ouverture n'y est donc écrite que si le match n'a encore aucun relevé : insérée
                // après un relevé existant, elle prendrait l'index 2 et casserait l'ordre.
                // Elle est de toute façon toujours enregistrée dans [OddsQuotes] (étiquette OPENING).
                if (data.Odds.Opening is { HasAnyOdd: true } opening)
                {
                    opening.BookmakerId = bookmakerId;

                    bool hasSnapshot = await conn.ExecuteScalarAsync<int>(
                        "SELECT CASE WHEN EXISTS (SELECT 1 FROM [dbo].[Odds] WHERE [MatchId] = @MatchId AND [BookmakerId] = @BookmakerId) THEN 1 ELSE 0 END",
                        new { MatchId = matchId, BookmakerId = bookmakerId }, transaction) == 1;

                    if (!hasSnapshot)
                    {
                        await InsertOddsSnapshotAsync(conn, transaction, matchId, opening);
                    }
                }

                await InsertOddsSnapshotAsync(conn, transaction, matchId, data.Odds);
                await InsertNormalizedOddsQuotesAsync(conn, transaction, matchId, data.Odds, ResolveSnapshotLabel(data));
            }

            await transaction.CommitAsync();
        }
        catch
        {
            await transaction.RollbackAsync();
            throw;
        }
    }

    /// <summary>
    /// Identifiant du bookmaker à utiliser : celui demandé s'il existe (141 = Betclic par défaut),
    /// sinon le premier actif, sinon création d'un bookmaker « Flashscore Consensus ».
    /// </summary>
    private async Task<int> EnsureBookmakerExistsAsync(SqlConnection conn, System.Data.IDbTransaction tx, int preferredBookmakerId)
    {
        const string sql = @"
            DECLARE @BkId INT;
            SELECT @BkId = [BookmakerId] FROM [dbo].[Bookmakers] WHERE [BookmakerId] = @PreferredId;

            IF @BkId IS NULL
                SELECT TOP 1 @BkId = [BookmakerId] FROM [dbo].[Bookmakers] WHERE [IsActive] = 1 ORDER BY [BookmakerId];

            IF @BkId IS NULL
            BEGIN
                INSERT INTO [dbo].[Bookmakers] ([BookmakerName], [IsActive], [CreatedAt])
                VALUES ('Flashscore Consensus', 1, SYSUTCDATETIME());
                SET @BkId = SCOPE_IDENTITY();
            END

            SELECT @BkId;";

        return await conn.ExecuteScalarAsync<int>(sql, new { PreferredId = preferredBookmakerId }, tx);
    }

    /// <summary>
    /// Retrouve (ou crée) la ligue et la saison du match. Nom de saison « AAAA-AAAA » ; l'ancien
    /// format « AAAA/AAAA » est reconnu. IsCurrent = saison qui contient la date du jour.
    /// </summary>
    private async Task<int> GetOrCreateLeagueSeasonIdAsync(
        SqlConnection conn, System.Data.IDbTransaction tx,
        string countryName, string leagueName, int tierLevel, DateTime matchDate)
    {
        // La saison est déduite UNIQUEMENT de la date du match (cut-off au 1er juillet).
        int startYear = matchDate.Month >= 7 ? matchDate.Year : matchDate.Year - 1;
        // Format de l'historique : "2025-2026". Le scraper écrivait "2026/2027", ce qui aurait
        // créé un doublon pour toute saison passée. L'ancien format reste reconnu en lecture.
        string seasonName = $"{startYear}-{startYear + 1}";
        string legacySeasonName = $"{startYear}/{startYear + 1}";

        // IsCurrent est déduit de la date du jour, jamais de la date du match :
        // un match de 2022 rechargé en 2026 ne doit pas devenir la saison courante.
        DateTime today = ParisTime.Now;
        int currentStartYear = today.Month >= 7 ? today.Year : today.Year - 1;
        bool isCurrentSeason = startYear == currentStartYear;

        const string sql = @"
            DECLARE @LeagueId INT;
            SELECT @LeagueId = [LeagueId]
            FROM [dbo].[Leagues]
            WHERE [CountryName] = @CountryName AND [LeagueName] = @LeagueName;

            IF @LeagueId IS NULL
            BEGIN
                INSERT INTO [dbo].[Leagues] ([LeagueName], [CountryName], [TierLevel], [IsActive], [CreatedAt])
                VALUES (@LeagueName, @CountryName, @TierLevel, 1, SYSUTCDATETIME());
                SET @LeagueId = SCOPE_IDENTITY();
            END

            -- Résolution déterministe : on ne matche QUE sur le nom de la saison.
            DECLARE @SeasonId INT;
            SELECT @SeasonId = [SeasonId]
            FROM [dbo].[Seasons]
            WHERE [LeagueId] = @LeagueId AND [SeasonName] IN (@SeasonName, @LegacySeasonName);

            IF @SeasonId IS NULL
            BEGIN
                IF @IsCurrentSeason = 1
                    UPDATE [dbo].[Seasons] SET [IsCurrent] = 0
                    WHERE [LeagueId] = @LeagueId AND [IsCurrent] = 1;

                INSERT INTO [dbo].[Seasons] ([LeagueId], [SeasonName], [StartDate], [EndDate], [IsCurrent], [CreatedAt])
                VALUES (@LeagueId, @SeasonName, DATEFROMPARTS(@StartYear, 7, 1), DATEFROMPARTS(@StartYear + 1, 6, 30), @IsCurrentSeason, SYSUTCDATETIME());
                SET @SeasonId = SCOPE_IDENTITY();
            END
            ELSE IF @IsCurrentSeason = 1
            BEGIN
                -- Bascule du flag lors d'un changement de saison (une seule saison courante par ligue).
                UPDATE [dbo].[Seasons] SET [IsCurrent] = 0
                WHERE [LeagueId] = @LeagueId AND [IsCurrent] = 1 AND [SeasonId] <> @SeasonId;

                UPDATE [dbo].[Seasons] SET [IsCurrent] = 1
                WHERE [SeasonId] = @SeasonId AND [IsCurrent] = 0;
            END

            SELECT @SeasonId;";

        return await conn.ExecuteScalarAsync<int>(sql, new
        {
            CountryName = countryName,
            LeagueName = leagueName,
            TierLevel = tierLevel,
            SeasonName = seasonName,
            LegacySeasonName = legacySeasonName,
            StartYear = startYear,
            IsCurrentSeason = isCurrentSeason
        }, tx);
    }

    /// <summary>Retrouve une équipe par sa clé Flashscore (ou son nom), la crée sinon.</summary>
    private async Task<int> GetOrCreateTeamAsync(
        SqlConnection conn, System.Data.IDbTransaction tx, string teamName, string teamKey, string countryName)
    {
        const string sql = @"
            DECLARE @TeamId INT;
            SELECT @TeamId = [TeamId] FROM [dbo].[Teams] WHERE [FlashscoreTeamKey] = @TeamKey OR [TeamName] = @TeamName;
            
            IF @TeamId IS NULL
            BEGIN
                INSERT INTO [dbo].[Teams] ([TeamName], [CountryName], [FlashscoreTeamKey], [CreatedAt], [UpdatedAt])
                VALUES (@TeamName, @CountryName, @TeamKey, SYSUTCDATETIME(), SYSUTCDATETIME());
                SET @TeamId = SCOPE_IDENTITY();
            END
            SELECT @TeamId;";

        return await conn.ExecuteScalarAsync<int>(sql, new { TeamName = teamName, TeamKey = teamKey, CountryName = countryName }, tx);
    }

    /// <summary>
    /// Crée ou met à jour le match. Règles : la date n'est écrite que si l'heure a été lue ;
    /// un match terminé ne redevient jamais « à venir » ; un statut Unknown est ignoré ;
    /// scores et journée ne remplacent jamais une valeur connue par du vide.
    /// </summary>
    private async Task<int> UpsertMatchAsync(
        SqlConnection conn, System.Data.IDbTransaction tx, ScrapedMatchData m, int seasonId, int homeTeamId, int awayTeamId)
    {
        const string sql = @"
            DECLARE @MatchId INT;
            SELECT @MatchId = [MatchId] FROM [dbo].[Matches] WHERE [FlashscoreMatchKey] = @FlashscoreMatchKey;

            IF @MatchId IS NULL
            BEGIN
                INSERT INTO [dbo].[Matches] 
                    ([FlashscoreMatchKey], [SeasonId], [MatchDate], [HomeTeamId], [AwayTeamId], 
                     [MatchStatus], [HomeScoreFT], [AwayScoreFT], [HomeScoreHT], [AwayScoreHT], 
                     [RoundNumber], [RoundName], [CreatedAt], [UpdatedAt])
                VALUES 
                    (@FlashscoreMatchKey, @SeasonId, @MatchDate, @HomeTeamId, @AwayTeamId, 
                     @MatchStatus, @HomeScoreFT, @AwayScoreFT, @HomeScoreHT, @AwayScoreHT, 
                     @RoundNumber, @RoundName, SYSUTCDATETIME(), SYSUTCDATETIME());
                SET @MatchId = SCOPE_IDENTITY();
            END
            ELSE
            BEGIN
                UPDATE [dbo].[Matches]
                SET [SeasonId] = @SeasonId,

                    -- La date n'est écrasée que si le coup d'envoi a été lu correctement.
                    -- Sinon le scraper remonte la date du jour, qui écraserait la vraie date.
                    [MatchDate] = CASE WHEN @KickoffParsed = 1 THEN @MatchDate ELSE [MatchDate] END,

                    -- Un match déjà terminé ne redevient jamais Scheduled, Live ou Unknown :
                    -- un statut mal lu ne doit pas faire régresser l'historique.
                    [MatchStatus] = CASE
                        WHEN [MatchStatus] = 'Finished' AND @MatchStatus <> 'Finished' THEN [MatchStatus]
                        WHEN @MatchStatus = 'Unknown' THEN [MatchStatus]
                        ELSE @MatchStatus
                    END,

                    [HomeScoreFT] = COALESCE(@HomeScoreFT, [HomeScoreFT]),
                    [AwayScoreFT] = COALESCE(@AwayScoreFT, [AwayScoreFT]),
                    [HomeScoreHT] = COALESCE(@HomeScoreHT, [HomeScoreHT]),
                    [AwayScoreHT] = COALESCE(@AwayScoreHT, [AwayScoreHT]),
                    [RoundNumber] = COALESCE(@RoundNumber, [RoundNumber]),
                    [RoundName] = COALESCE(@RoundName, [RoundName]),
                    [UpdatedAt] = SYSUTCDATETIME()
                WHERE [MatchId] = @MatchId;
            END
            SELECT @MatchId;";

        return await conn.ExecuteScalarAsync<int>(sql, new
        {
            m.FlashscoreMatchKey,
            SeasonId = seasonId,
            m.MatchDate,
            HomeTeamId = homeTeamId,
            AwayTeamId = awayTeamId,
            m.MatchStatus,
            m.HomeScoreFT,
            m.AwayScoreFT,
            m.HomeScoreHT,
            m.AwayScoreHT,
            m.RoundNumber,
            m.RoundName,
            KickoffParsed = m.KickoffParsed
        }, tx);
    }

    /// <summary>Crée ou met à jour la ligne de statistiques d'une équipe pour ce match.</summary>
    private async Task UpsertTeamStatsAsync(SqlConnection conn, System.Data.IDbTransaction tx, int matchId, int teamId, bool isHome, ScrapedTeamStats s)
    {
        const string sql = @"
            IF EXISTS (SELECT 1 FROM [dbo].[MatchTeamStats] WHERE [MatchId] = @MatchId AND [IsHome] = @IsHome)
            BEGIN
                UPDATE [dbo].[MatchTeamStats]
                SET [ExpectedGoals] = @ExpectedGoals, [PossessionPct] = @PossessionPct,
                    [Shots] = @Shots, [ShotsOnTarget] = @ShotsOnTarget, [Corners] = @Corners,
                    [Fouls] = @Fouls, [YellowCards] = @YellowCards, [RedCards] = @RedCards,
                    [UpdatedAt] = SYSUTCDATETIME()
                WHERE [MatchId] = @MatchId AND [IsHome] = @IsHome;
            END
            ELSE
            BEGIN
                INSERT INTO [dbo].[MatchTeamStats]
                    ([MatchId], [TeamId], [IsHome], [ExpectedGoals], [PossessionPct], [Shots], 
                     [ShotsOnTarget], [Corners], [Fouls], [YellowCards], [RedCards], [CreatedAt], [UpdatedAt])
                VALUES
                    (@MatchId, @TeamId, @IsHome, @ExpectedGoals, @PossessionPct, @Shots, 
                     @ShotsOnTarget, @Corners, @Fouls, @YellowCards, @RedCards, SYSUTCDATETIME(), SYSUTCDATETIME());
            END";

        await conn.ExecuteAsync(sql, new
        {
            MatchId = matchId,
            TeamId = teamId,
            IsHome = isHome,
            s.ExpectedGoals,
            s.PossessionPct,
            s.Shots,
            s.ShotsOnTarget,
            s.Corners,
            s.Fouls,
            s.YellowCards,
            s.RedCards
        }, tx);
    }

    /// <summary>
    /// Enregistre toutes les cotes dans [dbo].[Odds] uniquement si elles ont évolué depuis le dernier relevé.
    /// Incrémente automatiquement [OddIndex] (1 = Opening, N = Closing).
    /// </summary>
    private async Task InsertOddsSnapshotAsync(SqlConnection conn, System.Data.IDbTransaction tx, int matchId, ScrapedOdds o)
    {
        const string sql = @"
            -- Empreinte de TOUS les marchés : auparavant seuls 1X2 et Plus 2.5 étaient comparés,
            -- si bien qu'une cote BTTS, Double chance ou DNB corrigée n'était jamais enregistrée.
            -- CAST explicite : CHECKSUM dépend du type, colonne et paramètre doivent coïncider.
            DECLARE @LastSig INT, @NextIndex INT;

            SELECT TOP 1
                @LastSig = CHECKSUM(
                    CAST([OddHome] AS DECIMAL(10,3)),
                    CAST([OddDraw] AS DECIMAL(10,3)),
                    CAST([OddAway] AS DECIMAL(10,3)),
                    CAST([OddHomeDNB] AS DECIMAL(10,3)),
                    CAST([OddAwayDNB] AS DECIMAL(10,3)),
                    CAST([OddHomeDraw] AS DECIMAL(10,3)),
                    CAST([OddAwayDraw] AS DECIMAL(10,3)),
                    CAST([OddBTTS] AS DECIMAL(10,3)),
                    CAST([OddBTNS] AS DECIMAL(10,3)),
                    CAST([OddUn15] AS DECIMAL(10,3)),
                    CAST([OddOv15] AS DECIMAL(10,3)),
                    CAST([OddUn25] AS DECIMAL(10,3)),
                    CAST([OddOv25] AS DECIMAL(10,3)),
                    CAST([OddUn35] AS DECIMAL(10,3)),
                    CAST([OddOv35] AS DECIMAL(10,3))),
                @NextIndex = ISNULL([OddIndex], 0) + 1
            FROM [dbo].[Odds]
            WHERE [MatchId] = @MatchId AND [BookmakerId] = @BookmakerId
            ORDER BY [OddsQuoteId] DESC;

            IF @NextIndex IS NULL SET @NextIndex = 1;

            IF @NextIndex = 1
               OR @LastSig <> CHECKSUM(
                  CAST(@OddHome AS DECIMAL(10,3)),
                  CAST(@OddDraw AS DECIMAL(10,3)),
                  CAST(@OddAway AS DECIMAL(10,3)),
                  CAST(@OddHomeDNB AS DECIMAL(10,3)),
                  CAST(@OddAwayDNB AS DECIMAL(10,3)),
                  CAST(@OddHomeDraw AS DECIMAL(10,3)),
                  CAST(@OddAwayDraw AS DECIMAL(10,3)),
                  CAST(@OddBTTS AS DECIMAL(10,3)),
                  CAST(@OddBTNS AS DECIMAL(10,3)),
                  CAST(@OddUn15 AS DECIMAL(10,3)),
                  CAST(@OddOv15 AS DECIMAL(10,3)),
                  CAST(@OddUn25 AS DECIMAL(10,3)),
                  CAST(@OddOv25 AS DECIMAL(10,3)),
                  CAST(@OddUn35 AS DECIMAL(10,3)),
                  CAST(@OddOv35 AS DECIMAL(10,3)))
            BEGIN
                INSERT INTO [dbo].[Odds]
                    ([MatchId], [BookmakerId], 
                     [OddHome], [OddDraw], [OddAway], 
                     [OddHomeDNB], [OddAwayDNB], 
                     [OddHomeDraw], [OddAwayDraw], 
                     [OddBTTS], [OddBTNS], 
                     [OddUn15], [OddOv15], 
                     [OddUn25], [OddOv25], 
                     [OddUn35], [OddOv35], 
                     [DateAdded], [OddIndex])
                VALUES
                    (@MatchId, @BookmakerId, 
                     @OddHome, @OddDraw, @OddAway, 
                     @OddHomeDNB, @OddAwayDNB, 
                     @OddHomeDraw, @OddAwayDraw, 
                     @OddBTTS, @OddBTNS, 
                     @OddUn15, @OddOv15, 
                     @OddUn25, @OddOv25, 
                     @OddUn35, @OddOv35, 
                     SYSUTCDATETIME(), @NextIndex);
            END";

        await conn.ExecuteAsync(sql, new
        {
            MatchId = matchId,
            o.BookmakerId,
            o.OddHome,
            o.OddDraw,
            o.OddAway,
            o.OddHomeDNB,
            o.OddAwayDNB,
            o.OddHomeDraw,
            o.OddAwayDraw,
            o.OddBTTS,
            o.OddBTNS,
            o.OddUn15,
            o.OddOv15,
            o.OddUn25,
            o.OddOv25,
            o.OddUn35,
            o.OddOv35
        }, tx);
    }

    /// <summary>
    /// Étiquette du relevé, alignée sur le vocabulaire DÉJÀ présent en base :
    /// OPENING, J-2, J-1, DAY, H-1, CLOSING. Le scraper écrivait "PreMatch"/"PostMatch",
    /// un vocabulaire parallèle inexploitable avec les 1,2 million de lignes historiques.
    /// </summary>
    private static string ResolveSnapshotLabel(ScrapedMatchData data)
    {
        // Match déjà joué : la cote affichée par Flashscore est la cote de clôture.
        if (data.MatchStatus == "Finished") return "CLOSING";

        DateTime now = ParisTime.Now;
        TimeSpan avantCoupDEnvoi = data.MatchDate - now;

        if (avantCoupDEnvoi <= TimeSpan.Zero) return "CLOSING";
        if (avantCoupDEnvoi <= TimeSpan.FromHours(1)) return "H-1";
        if (data.MatchDate.Date == now.Date) return "DAY";
        if (avantCoupDEnvoi < TimeSpan.FromDays(2)) return "J-1";
        if (avantCoupDEnvoi < TimeSpan.FromDays(3)) return "J-2";

        // OPENING est désormais réservé à la VRAIE cote d'ouverture, lue dans l'attribut title
        // de Flashscore. Un relevé fait trois jours avant le match n'en est pas une.
        return "J-3";
    }

    /// <summary>
    /// Alimente la table normalisée [dbo].[OddsQuotes] avec gestion automatique de IsOpening et IsClosing.
    /// </summary>
    private static List<(string Market, string Selection, decimal? Line, decimal? Odd)> BuildQuotes(ScrapedOdds o) => new()
    {
        ("1X2", "HOME", null, o.OddHome),
        ("1X2", "DRAW", null, o.OddDraw),
        ("1X2", "AWAY", null, o.OddAway),
        ("DNB", "HOME", null, o.OddHomeDNB),
        ("DNB", "AWAY", null, o.OddAwayDNB),
        ("DOUBLE_CHANCE", "1X", null, o.OddHomeDraw),
        ("DOUBLE_CHANCE", "X2", null, o.OddAwayDraw),
        ("BTTS", "YES", null, o.OddBTTS),
        ("BTTS", "NO", null, o.OddBTNS),
        ("OVER_UNDER", "OVER", 1.5m, o.OddOv15),
        ("OVER_UNDER", "UNDER", 1.5m, o.OddUn15),
        ("OVER_UNDER", "OVER", 2.5m, o.OddOv25),
        ("OVER_UNDER", "UNDER", 2.5m, o.OddUn25),
        ("OVER_UNDER", "OVER", 3.5m, o.OddOv35),
        ("OVER_UNDER", "UNDER", 3.5m, o.OddUn35)
    };

    /// <summary>
    /// Écrit les cotes dans dbo.OddsQuotes, une ligne par sélection (ex. 1X2/HOME, OVER_UNDER/OVER 2.5).
    /// La cote d'ouverture n'est insérée qu'une fois ; une cote actuelle n'est insérée que si elle
    /// diffère du dernier relevé. IsOpening / IsClosing sont recalculés pour le match.
    /// </summary>
    private async Task InsertNormalizedOddsQuotesAsync(
        SqlConnection conn, System.Data.IDbTransaction tx, int matchId, ScrapedOdds o, string snapshotLabel)
    {
        // 1. Cote d'ouverture, insérée une seule fois par sélection et datée AVANT tout autre
        //    relevé de la série, pour que l'ordre chronologique reste juste.
        if (o.Opening != null)
        {
            const string openingSql = @"
                IF NOT EXISTS (SELECT 1 FROM [dbo].[OddsQuotes]
                               WHERE [MatchId] = @MatchId AND [BookmakerId] = @BookmakerId
                                 AND [MarketCode] = @MarketCode AND [SelectionCode] = @SelectionCode
                                 AND ISNULL([LineValue], -1) = ISNULL(@LineValue, -1)
                                 AND [SnapshotLabel] = 'OPENING')
                BEGIN
                    DECLARE @Earliest DATETIME2, @Count INT;

                    SELECT @Earliest = MIN([QuoteDateUtc]), @Count = COUNT(*)
                    FROM [dbo].[OddsQuotes]
                    WHERE [MatchId] = @MatchId AND [BookmakerId] = @BookmakerId
                      AND [MarketCode] = @MarketCode AND [SelectionCode] = @SelectionCode
                      AND ISNULL([LineValue], -1) = ISNULL(@LineValue, -1);

                    UPDATE [dbo].[OddsQuotes] SET [IsOpening] = 0
                    WHERE [MatchId] = @MatchId AND [BookmakerId] = @BookmakerId
                      AND [MarketCode] = @MarketCode AND [SelectionCode] = @SelectionCode
                      AND ISNULL([LineValue], -1) = ISNULL(@LineValue, -1)
                      AND [IsOpening] = 1;

                    INSERT INTO [dbo].[OddsQuotes]
                        ([MatchId], [BookmakerId], [MarketCode], [SelectionCode], [LineValue],
                         [OddValue], [QuoteDateUtc], [IsOpening], [IsClosing], [IsLive], [CreatedAt], [SnapshotLabel])
                    VALUES
                        (@MatchId, @BookmakerId, @MarketCode, @SelectionCode, @LineValue,
                         @OddValue,
                         CASE WHEN @Count > 0 THEN DATEADD(MINUTE, -1, @Earliest) ELSE DATEADD(SECOND, -1, SYSUTCDATETIME()) END,
                         1,
                         CASE WHEN @Count > 0 THEN 0 ELSE 1 END,
                         0, SYSUTCDATETIME(), 'OPENING');
                END";

            foreach (var q in BuildQuotes(o.Opening).Where(x => x.Odd.HasValue))
            {
                await conn.ExecuteAsync(openingSql, new
                {
                    MatchId = matchId,
                    o.BookmakerId,
                    MarketCode = q.Market,
                    SelectionCode = q.Selection,
                    LineValue = q.Line,
                    OddValue = q.Odd!.Value
                }, tx);
            }
        }

        // 2. Cote actuelle, étiquetée selon le délai avant le coup d'envoi.
        var quotes = BuildQuotes(o);

        const string sql = @"
            DECLARE @Exists BIT = 0;
            DECLARE @LastOdd DECIMAL(8,3);

            -- Dernier relevé CHRONOLOGIQUE : la ligne OPENING, insérée juste avant avec l'identifiant
            -- le plus élevé mais une date antérieure, ne doit pas être prise pour le dernier relevé.
            SELECT TOP 1 @Exists = 1, @LastOdd = [OddValue]
            FROM [dbo].[OddsQuotes]
            WHERE [MatchId] = @MatchId 
              AND [BookmakerId] = @BookmakerId 
              AND [MarketCode] = @MarketCode 
              AND [SelectionCode] = @SelectionCode
              AND ISNULL([LineValue], -1) = ISNULL(@LineValue, -1)
            ORDER BY [QuoteDateUtc] DESC, [OddsQuoteId] DESC;

            -- Si la cote a changé ou que c'est la toute première fois (Opening)
            IF @Exists = 0 OR @LastOdd <> @OddValue
            BEGIN
                -- L'ancienne cote n'est plus la Closing Line
                IF @Exists = 1
                BEGIN
                    UPDATE [dbo].[OddsQuotes]
                    SET [IsClosing] = 0
                    WHERE [MatchId] = @MatchId 
                      AND [BookmakerId] = @BookmakerId 
                      AND [MarketCode] = @MarketCode 
                      AND [SelectionCode] = @SelectionCode
                      AND ISNULL([LineValue], -1) = ISNULL(@LineValue, -1)
                      AND [IsClosing] = 1;
                END

                INSERT INTO [dbo].[OddsQuotes]
                    ([MatchId], [BookmakerId], [MarketCode], [SelectionCode], [LineValue], 
                     [OddValue], [QuoteDateUtc], [IsOpening], [IsClosing], [IsLive], [CreatedAt], [SnapshotLabel])
                VALUES
                    (@MatchId, @BookmakerId, @MarketCode, @SelectionCode, @LineValue, 
                     @OddValue, SYSUTCDATETIME(), CASE WHEN @Exists = 0 THEN 1 ELSE 0 END, 1, 0, SYSUTCDATETIME(), @SnapshotLabel);
            END";

        foreach (var q in quotes.Where(x => x.Odd.HasValue))
        {
            await conn.ExecuteAsync(sql, new
            {
                MatchId = matchId,
                o.BookmakerId,
                MarketCode = q.Market,
                SelectionCode = q.Selection,
                LineValue = q.Line,
                OddValue = q.Odd!.Value,
                SnapshotLabel = snapshotLabel
            }, tx);
        }
    }

    // =========================================================================
    // JOURNAL D'EXÉCUTION
    //   dbo.AppExecutionRuns : une ligne par exécution (journalier ou rattrapage)
    //   dbo.AppExecutionLogs : une ligne par événement (match enregistré, erreur, bilan...)
    //
    // Cycle de vie d'une exécution :
    //   StartExecutionRunAsync    -> Status = 'Running'
    //   LogExecutionAsync (n fois)
    //   CompleteExecutionRunAsync -> Status = 'Success' | 'Warning' | 'Failed' + fin, durée, compteurs
    //   Une exécution restée 'Running' (PC éteint, service arrêté en plein travail) passe à
    //   'Interrupted' au démarrage de l'exécution suivante de la même source.
    //
    // Aucune de ces méthodes, sauf StartExecutionRunAsync, ne lève d'exception : une panne du
    // journal est signalée dans le fichier Serilog et comptée, mais n'interrompt jamais l'import.
    // Documentation : DOCUMENTATION.md, chapitre 4.5.
    // =========================================================================

    /// <summary>Statuts possibles d'une exécution dans dbo.AppExecutionRuns.</summary>
    public const string RunRunning = "Running";
    public const string RunInterrupted = "Interrupted";

    // Noms acceptés pour chaque information de fin d'exécution : le premier qui existe dans la
    // table est utilisé. Les noms « standard » (premiers de chaque liste) sont ceux créés par
    // SQL/2_Corrections/C5_JournalExecutions.sql ; les suivants couvrent une table déjà existante.
    private static readonly string[] EndColumns = { "FinishedAtUtc", "EndedAtUtc", "CompletedAtUtc", "EndAtUtc", "EndTimeUtc" };
    private static readonly string[] SummaryColumns = { "Summary", "Message", "Details", "Comment", "Comments" };
    private static readonly string[] ErrorColumns = { "ErrorMessage", "Error", "ErrorDetails" };

    /// <summary>Colonnes réelles des deux tables du journal (nom -> longueur max ; -1 = MAX ; 0 = non texte).</summary>
    private sealed class JournalSchema
    {
        public string RunIdColumn = "RunId";
        public readonly Dictionary<string, int> Runs = new(StringComparer.OrdinalIgnoreCase);
        public readonly Dictionary<string, int> Logs = new(StringComparer.OrdinalIgnoreCase);

        /// <summary>Premier nom de la liste qui existe dans AppExecutionRuns, sinon null.</summary>
        public string? PickRun(params string[] names) => names.FirstOrDefault(n => Runs.ContainsKey(n));
    }

    /// <summary>Ligne lue dans sys.columns (structure des tables du journal).</summary>
    private sealed class JournalColumnRow
    {
        public string TableName { get; set; } = "";
        public string ColumnName { get; set; } = "";
        public int? MaxLen { get; set; }
        public bool IsIdentity { get; set; }
    }

    private JournalSchema? _journal;
    private readonly SemaphoreSlim _journalLock = new(1, 1);
    private readonly ConcurrentDictionary<int, int> _logWriteFailures = new();

    /// <summary>
    /// Lit une seule fois la structure des tables du journal : nom de la colonne identifiant
    /// (RunId ou Id), colonnes disponibles et longueurs maximales (pour tronquer au lieu d'échouer).
    /// En cas d'échec, renvoie une structure par défaut et réessaiera au prochain appel.
    /// </summary>
    private async Task<JournalSchema> GetJournalSchemaAsync()
    {
        if (_journal != null) return _journal;
        await _journalLock.WaitAsync();
        try
        {
            if (_journal != null) return _journal;
            var schema = new JournalSchema();
            try
            {
                await using var conn = new SqlConnection(_connectionString);
                const string sql = @"
                    SELECT t.name AS TableName, c.name AS ColumnName,
                           CASE WHEN ty.name IN ('nvarchar','nchar') AND c.max_length > 0 THEN c.max_length / 2
                                WHEN ty.name IN ('varchar','char')   AND c.max_length > 0 THEN c.max_length
                                WHEN ty.name IN ('nvarchar','varchar','nchar','char')    THEN -1
                                ELSE 0 END AS MaxLen,
                           c.is_identity AS IsIdentity
                    FROM sys.columns c
                    JOIN sys.tables t ON t.object_id = c.object_id
                    JOIN sys.types ty ON ty.user_type_id = c.user_type_id
                    WHERE SCHEMA_NAME(t.schema_id) = 'dbo'
                      AND t.name IN ('AppExecutionRuns', 'AppExecutionLogs')
                      AND c.is_computed = 0;";

                bool identityFound = false;
                foreach (var row in await conn.QueryAsync<JournalColumnRow>(sql))
                {
                    bool isRuns = row.TableName.Equals("AppExecutionRuns", StringComparison.OrdinalIgnoreCase);
                    (isRuns ? schema.Runs : schema.Logs)[row.ColumnName] = row.MaxLen ?? 0;
                    if (isRuns && row.IsIdentity)
                    {
                        schema.RunIdColumn = row.ColumnName;
                        identityFound = true;
                    }
                }
                if (!identityFound)
                    schema.RunIdColumn = schema.Runs.ContainsKey("RunId") ? "RunId" : schema.Runs.ContainsKey("Id") ? "Id" : "RunId";
            }
            catch (Exception ex)
            {
                _logger?.LogWarning(ex, "Structure des tables du journal d'exécution illisible : valeurs par défaut utilisées.");
            }

            if (schema.Runs.Count > 0) _journal = schema;   // mise en cache seulement si la lecture a réussi
            return schema;
        }
        finally
        {
            _journalLock.Release();
        }
    }

    /// <summary>Tronque un texte à la longueur de sa colonne (au lieu de faire échouer l'INSERT).</summary>
    private static string? Fit(string? value, Dictionary<string, int> columns, string column)
    {
        if (value == null) return null;
        if (!columns.TryGetValue(column, out int max) || max <= 0 || value.Length <= max) return value;
        return value[..Math.Max(0, max - 1)] + "…";
    }

    /// <summary>
    /// Ouvre une exécution dans dbo.AppExecutionRuns (Status = 'Running') et retourne son identifiant.
    /// triggerSource : 'ScheduledTask' (job journalier Hangfire) ou 'Backfill' (rattrapage).
    /// Avant cela, les exécutions de la même source restées 'Running' depuis longtemps (12 h pour
    /// le journalier, 48 h pour le rattrapage) passent à 'Interrupted' : le processus s'est arrêté
    /// sans pouvoir écrire sa fin (PC éteint, service arrêté, plantage de Windows).
    /// C'est la seule méthode du journal qui lève une exception : sans base, inutile de scraper.
    /// </summary>
    public async Task<int> StartExecutionRunAsync(string triggerSource)
    {
        var schema = await GetJournalSchemaAsync();
        int staleHours = triggerSource == "Backfill" ? 48 : 12;

        try
        {
            await using var conn = new SqlConnection(_connectionString);
            string? summaryCol = schema.PickRun(SummaryColumns);
            string summarySet = summaryCol != null ? $", [{summaryCol}] = COALESCE([{summaryCol}], @Note)" : "";
            string sql = $@"
                UPDATE [dbo].[AppExecutionRuns]
                SET [Status] = @Interrupted{summarySet}
                WHERE [Status] = @Running
                  AND [TriggerSource] = @Source
                  AND [StartedAtUtc] < DATEADD(HOUR, -@Hours, SYSUTCDATETIME());";
            int n = await conn.ExecuteAsync(sql, new
            {
                Interrupted = Fit(RunInterrupted, schema.Runs, "Status"),
                Running = RunRunning,
                Source = triggerSource,
                Hours = staleHours,
                Note = summaryCol == null ? null : Fit(
                    "Interrompue : aucune fin enregistrée (service arrêté, PC éteint ou plantage).",
                    schema.Runs, summaryCol)
            });
            if (n > 0)
                _logger?.LogWarning("{Count} exécution(s) précédente(s) « {Source} » jamais terminée(s) : marquée(s) Interrupted.", n, triggerSource);
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "Impossible de marquer les anciennes exécutions interrompues.");
        }

        await using var insertConn = new SqlConnection(_connectionString);
        const string insertSql = @"
            INSERT INTO [dbo].[AppExecutionRuns] ([ApplicationName], [TriggerSource], [StartedAtUtc], [Status])
            VALUES ('FlashScore.ScraperService', @Source, SYSUTCDATETIME(), @Running);

            SELECT CAST(SCOPE_IDENTITY() AS INT);";
        return await insertConn.ExecuteScalarAsync<int>(insertSql, new { Source = triggerSource, Running = RunRunning });
    }

    /// <summary>
    /// Ferme une exécution : statut final, heure de fin, durée, compteurs et résumé.
    /// Seules les colonnes présentes dans la table sont écrites (voir C5_JournalExecutions.sql).
    /// Ne lève jamais d'exception : en dernier recours, seul le statut est mis à jour.
    /// </summary>
    public async Task CompleteExecutionRunAsync(int runId, ExecutionRunResult result)
    {
        if (runId <= 0) return;
        var schema = await GetJournalSchemaAsync();
        var runs = schema.Runs;
        string idCol = schema.RunIdColumn;
        string status = Fit(result.Status, runs, "Status") ?? ExecutionRunResult.Failed;

        var sets = new List<string> { "[Status] = @Status" };
        var p = new DynamicParameters();
        p.Add("RunId", runId);
        p.Add("Status", status);

        // Ajoute « [colonne] = valeur » si l'une des colonnes de la liste existe.
        void Set(string[] names, string param, object? value, string? sqlExpression = null)
        {
            string? col = schema.PickRun(names);
            if (col == null) return;
            if (sqlExpression != null)
            {
                sets.Add($"[{col}] = {sqlExpression}");
                return;
            }
            if (value is string text) value = Fit(text, runs, col);
            sets.Add($"[{col}] = @{param}");
            p.Add(param, value);
        }

        Set(EndColumns, "End", null, "SYSUTCDATETIME()");
        Set(new[] { "DurationSeconds" }, "Duration", null, "DATEDIFF(SECOND, [StartedAtUtc], SYSUTCDATETIME())");
        Set(new[] { "MatchesFound" }, "Found", result.MatchesFound);
        Set(new[] { "MatchesSaved", "ItemsProcessed", "ProcessedCount" }, "Saved", result.MatchesSaved);
        Set(new[] { "MatchesSkipped" }, "Skipped", result.MatchesSkipped);
        Set(new[] { "MatchesFailed", "ErrorCount", "ErrorsCount" }, "Failed", result.MatchesFailed);
        Set(new[] { "QualityDegraded" }, "Quality", result.QualityDegraded);
        Set(new[] { "LogWriteFailures" }, "LogFailures", GetLogWriteFailures(runId));
        Set(SummaryColumns, "Summary", result.Summary);
        Set(ErrorColumns, "Error", result.Error);

        string sql = $"UPDATE [dbo].[AppExecutionRuns] SET {string.Join(", ", sets)} WHERE [{idCol}] = @RunId;";

        for (int attempt = 1; attempt <= 2; attempt++)
        {
            try
            {
                await using var conn = new SqlConnection(_connectionString);
                await conn.ExecuteAsync(sql, p);
                return;
            }
            catch (Exception ex)
            {
                _logger?.LogWarning(ex, "Clôture de l'exécution {RunId} impossible (essai {Attempt}/2).", runId, attempt);
                if (attempt == 1) await Task.Delay(1000);
            }
        }

        // Dernier recours : au moins le statut, pour ne pas laisser 'Running'.
        try
        {
            await using var conn = new SqlConnection(_connectionString);
            await conn.ExecuteAsync(
                $"UPDATE [dbo].[AppExecutionRuns] SET [Status] = @Status WHERE [{idCol}] = @RunId;",
                new { Status = status, RunId = runId });
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "L'exécution {RunId} reste « Running » en base : clôture impossible.", runId);
        }
    }

    /// <summary>Nombre d'écritures du journal perdues pour cette exécution (base injoignable...).</summary>
    public int GetLogWriteFailures(int runId) => _logWriteFailures.TryGetValue(runId, out int n) ? n : 0;

    /// <summary>
    /// Insère un événement dans dbo.AppExecutionLogs. Ne lève JAMAIS d'exception : un journal en
    /// panne ne doit pas faire échouer l'enregistrement d'un match. Textes trop longs : tronqués à
    /// la taille de la colonne. Un échec est retenté une fois, puis signalé dans le fichier Serilog
    /// et compté (colonne LogWriteFailures de l'exécution).
    /// </summary>
    public async Task LogExecutionAsync(int runId, string level, string sector, string message, string? matchIdent = null, string? exception = null)
    {
        var schema = await GetJournalSchemaAsync();
        var logs = schema.Logs;
        const string sql = @"
            INSERT INTO [dbo].[AppExecutionLogs]
                ([RunId], [CreatedAtUtc], [ApplicationName], [Level], [Sector], [MatchIdent], [Message], [Exception])
            VALUES
                (@RunId, SYSUTCDATETIME(), 'Juniro.ScraperService', @Level, @Sector, @MatchIdent, @Message, @Exception)";
        var args = new
        {
            RunId = runId,
            Level = Fit(level, logs, "Level"),
            Sector = Fit(sector, logs, "Sector"),
            MatchIdent = Fit(matchIdent, logs, "MatchIdent"),
            Message = Fit(message, logs, "Message"),
            Exception = Fit(exception, logs, "Exception")
        };

        for (int attempt = 1; attempt <= 2; attempt++)
        {
            try
            {
                await using var conn = new SqlConnection(_connectionString);
                await conn.ExecuteAsync(sql, args);
                return;
            }
            catch (Exception) when (attempt == 1)
            {
                await Task.Delay(500);
            }
            catch (Exception ex)
            {
                _logWriteFailures.AddOrUpdate(runId, 1, (_, n) => n + 1);
                _logger?.LogWarning(ex, "Journal SQL non écrit (RunId {RunId}, {Level} {Sector}) : {Message}",
                    runId, level, sector, message);
            }
        }
    }
}

/// <summary>
/// Bilan d'une exécution, écrit dans dbo.AppExecutionRuns par CompleteExecutionRunAsync.
///   Success : tout s'est bien passé.
///   Warning : terminé, mais des matchs ont échoué, le contrôle qualité a alerté, ou aucun match trouvé.
///   Failed  : plantage, ou des échecs sans aucun match enregistré.
/// </summary>
public sealed class ExecutionRunResult
{
    public const string Success = "Success";
    public const string Warning = "Warning";
    public const string Failed = "Failed";

    public string Status { get; set; } = Success;
    public int? MatchesFound { get; set; }
    public int? MatchesSaved { get; set; }
    public int? MatchesSkipped { get; set; }
    public int? MatchesFailed { get; set; }
    public bool QualityDegraded { get; set; }
    public string? Summary { get; set; }
    public string? Error { get; set; }
}

/// <summary>Match déjà en base à repasser (rattrapage automatique), et ce qui lui manque.</summary>
public sealed class RepairCandidate
{
    public string Key { get; set; } = "";
    public string LeagueName { get; set; } = "";
    public DateTime MatchDate { get; set; }
    public string? Reason { get; set; }
}
