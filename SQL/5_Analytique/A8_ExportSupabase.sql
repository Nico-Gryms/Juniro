/* =============================================================================
   Juniro_Evo - Vues d'export vers Supabase (application mobile)
   -----------------------------------------------------------------------------
   Le job « supabase-sync » du service Windows (Services\SupabaseSyncJob.cs) lit ces
   vues et recopie leur contenu dans les tables Supabase du même nom :

     app.vx_SupaMatches          -> matches           (tous les matchs, scores et stats)
     app.vx_SupaPredictions      -> predictions       (une prédiction par match)
     app.vx_SupaMatchStats       -> match_stats       (forme des équipes avant le match)
     app.vx_SupaStrategyReports  -> strategy_reports  (bilan des stratégies, dernier backtest)
     app.vx_SupaSignals          -> signals           (paris fictifs et leur résultat)
     app.vx_SupaExecutionRuns    -> execution_runs    (journal : exécutions des 30 derniers jours)
     app.vx_SupaExecutionLogs    -> execution_logs    (journal : événements des 14 derniers jours)

   RÈGLE : les noms de colonnes de ces vues sont EXACTEMENT ceux des tables Supabase
   (minuscules, mots séparés par _). Pour envoyer une colonne de plus : l'ajouter ici ET
   dans la table Supabase (SQL\6_Supabase\001_schema.sql), avec le même nom.
   Toutes les heures sont en UTC (l'application les affiche à l'heure du téléphone).

   Exige A1 à A7 et A9. Script rejouable, lecture seule (aucune table modifiée).
   Documentation : DOCUMENTATION.md (racine du projet), chapitre 13.
   ============================================================================= */
USE [Juniro_Evo];
GO

CREATE OR ALTER VIEW app.vx_SupaMatches
AS
SELECT m.MatchId                         AS match_id,
       m.LeagueName                      AS league,
       m.CountryName                     AS country,
       CAST(m.TierLevel AS SMALLINT)     AS tier,
       m.SeasonName                      AS season,
       -- MatchDate est l'heure de Paris : conversion en UTC
       CAST((CAST(m.MatchDate AS DATETIME2(0)) AT TIME ZONE 'Romance Standard Time') AT TIME ZONE 'UTC'
            AS DATETIME2(0))             AS kickoff_utc,
       m.RoundNumber                     AS round_number,
       m.MatchStatus                     AS status,
       m.HomeTeamName                    AS home_team,
       m.AwayTeamName                    AS away_team,
       CAST(m.HomeScoreFT AS SMALLINT)   AS home_goals,
       CAST(m.AwayScoreFT AS SMALLINT)   AS away_goals,
       CAST(m.HomeScoreHT AS SMALLINT)   AS home_goals_ht,
       CAST(m.AwayScoreHT AS SMALLINT)   AS away_goals_ht,
       CAST(m.Home_xG AS DECIMAL(5,2))   AS home_xg,
       CAST(m.Away_xG AS DECIMAL(5,2))   AS away_xg,
       CAST(m.Home_Shots AS SMALLINT)    AS home_shots,
       CAST(m.Away_Shots AS SMALLINT)    AS away_shots,
       CAST(m.Home_SoT AS SMALLINT)      AS home_sot,
       CAST(m.Away_SoT AS SMALLINT)      AS away_sot,
       CAST(m.Home_Corners AS SMALLINT)  AS home_corners,
       CAST(m.Away_Corners AS SMALLINT)  AS away_corners,
       CAST(m.Home_Possession AS DECIMAL(5,1)) AS home_possession,
       CAST(m.Away_Possession AS DECIMAL(5,1)) AS away_possession
FROM core.vw_Matches m;
GO

/* Une prédiction par match :
     1. la dernière prédiction quotidienne (LIVE) faite AVANT le coup d'envoi ;
     2. sinon celle du dernier backtest (walk-forward : faite sans connaître le match).
   Une prédiction LIVE faite après le coup d'envoi n'est jamais retenue.
   Cotes : dernier relevé avant le coup d'envoi, hors cote de clôture, pour tous les paris
   relevés : 1X2, double chance (1X, X2), remboursé si nul, BTTS, plus/moins 1,5 / 2,5 / 3,5.
   (colonnes odd_dc_*, odd_dnb_*, odd_*15, odd_*35 : SQL\6_Supabase\002_app_v2.sql ;
    mkt_p_over15 / mkt_p_over35 : SQL\6_Supabase\004_lignes_buts.sql).
   Exige A9_LignesButs.sql (colonnes POver15, POver35, Mkt_POver15, Mkt_POver35). */
CREATE OR ALTER VIEW app.vx_SupaPredictions
AS
WITH P AS (
    SELECT p.*, r.RunType,
           CASE WHEN r.RunType = 'LIVE'
                     AND CAST((r.CreatedAtUtc AT TIME ZONE 'UTC') AT TIME ZONE 'Romance Standard Time' AS DATETIME2(0))
                         < m.MatchDate THEN 0
                WHEN r.RunType = 'BACKTEST' THEN 1
                ELSE 2 END AS Priority
    FROM ml.MatchPredictions p
    JOIN ml.ModelRuns r ON r.RunId = p.RunId
    JOIN core.vw_Matches m ON m.MatchId = p.MatchId
), Best AS (
    SELECT P.*, ROW_NUMBER() OVER (PARTITION BY P.MatchId ORDER BY P.Priority, P.RunId DESC) AS Rn
    FROM P
    WHERE P.Priority < 2
), LastOdds AS (
    SELECT o.MatchId, o.MarketCode, o.SelectionCode, o.LineValue, o.OddValue, o.SnapshotLabel,
           ROW_NUMBER() OVER (PARTITION BY o.MatchId, o.MarketCode, o.SelectionCode, o.LineValue
                              ORDER BY o.QuoteDateUtc DESC, o.BookmakerId) AS Rn
    FROM core.vw_OddsByLabel o
    JOIN core.vw_Matches m ON m.MatchId = o.MatchId
    WHERE o.SnapshotLabel <> 'CLOSING'
      AND CAST((o.QuoteDateUtc AT TIME ZONE 'UTC') AT TIME ZONE 'Romance Standard Time' AS DATETIME2(0)) < m.MatchDate
      AND (o.MarketCode IN ('1X2', 'BTTS', 'DNB', 'DOUBLE_CHANCE')
           OR (o.MarketCode = 'OVER_UNDER' AND o.LineValue IN (1.5, 2.5, 3.5)))
), Odds AS (
    SELECT MatchId,
           MAX(CASE WHEN MarketCode = '1X2'  AND SelectionCode = 'HOME' THEN OddValue END) AS OddHome,
           MAX(CASE WHEN MarketCode = '1X2'  AND SelectionCode = 'DRAW' THEN OddValue END) AS OddDraw,
           MAX(CASE WHEN MarketCode = '1X2'  AND SelectionCode = 'AWAY' THEN OddValue END) AS OddAway,
           MAX(CASE WHEN MarketCode = 'BTTS' AND SelectionCode = 'YES'  THEN OddValue END) AS OddBttsYes,
           MAX(CASE WHEN MarketCode = 'BTTS' AND SelectionCode = 'NO'   THEN OddValue END) AS OddBttsNo,
           MAX(CASE WHEN MarketCode = 'OVER_UNDER' AND LineValue = 2.5 AND SelectionCode = 'OVER'  THEN OddValue END) AS OddOver25,
           MAX(CASE WHEN MarketCode = 'OVER_UNDER' AND LineValue = 2.5 AND SelectionCode = 'UNDER' THEN OddValue END) AS OddUnder25,
           MAX(CASE WHEN MarketCode = 'OVER_UNDER' AND LineValue = 1.5 AND SelectionCode = 'OVER'  THEN OddValue END) AS OddOver15,
           MAX(CASE WHEN MarketCode = 'OVER_UNDER' AND LineValue = 1.5 AND SelectionCode = 'UNDER' THEN OddValue END) AS OddUnder15,
           MAX(CASE WHEN MarketCode = 'OVER_UNDER' AND LineValue = 3.5 AND SelectionCode = 'OVER'  THEN OddValue END) AS OddOver35,
           MAX(CASE WHEN MarketCode = 'OVER_UNDER' AND LineValue = 3.5 AND SelectionCode = 'UNDER' THEN OddValue END) AS OddUnder35,
           MAX(CASE WHEN MarketCode = 'DNB' AND SelectionCode = 'HOME' THEN OddValue END) AS OddDnbHome,
           MAX(CASE WHEN MarketCode = 'DNB' AND SelectionCode = 'AWAY' THEN OddValue END) AS OddDnbAway,
           MAX(CASE WHEN MarketCode = 'DOUBLE_CHANCE' AND SelectionCode = '1X' THEN OddValue END) AS OddDc1X,
           MAX(CASE WHEN MarketCode = 'DOUBLE_CHANCE' AND SelectionCode = 'X2' THEN OddValue END) AS OddDcX2,
           MAX(CASE WHEN MarketCode = '1X2' THEN SnapshotLabel END) AS OddsLabel
    FROM LastOdds WHERE Rn = 1
    GROUP BY MatchId
)
SELECT b.MatchId                               AS match_id,
       b.RunType                               AS source,
       b.RunId                                 AS model_run_id,
       b.TrainedUntil                          AS trained_until,
       CAST(b.ExpHomeGoals  AS DECIMAL(5,2))   AS exp_home_goals,
       CAST(b.ExpAwayGoals  AS DECIMAL(5,2))   AS exp_away_goals,
       CAST(b.ExpTotalGoals AS DECIMAL(5,2))   AS exp_total_goals,
       b.MostLikelyScore                       AS most_likely_score,
       b.PHome AS p_home, b.PDraw AS p_draw, b.PAway AS p_away,
       b.PBttsYes AS p_btts_yes,
       -- ±1,5 / ±3,5 : probabilité finale (mélange, depuis le 10/11/2026, A9), sinon modèle de score
       COALESCE(b.POver15, b.Score_POver15) AS p_over15, b.POver25 AS p_over25,
       COALESCE(b.POver35, b.Score_POver35) AS p_over35,
       b.Mkt_PHome AS mkt_p_home, b.Mkt_PDraw AS mkt_p_draw, b.Mkt_PAway AS mkt_p_away,
       b.Mkt_PBttsYes AS mkt_p_btts_yes, b.Mkt_POver25 AS mkt_p_over25,
       CAST(o.OddHome    AS DECIMAL(6,2)) AS odd_home,
       CAST(o.OddDraw    AS DECIMAL(6,2)) AS odd_draw,
       CAST(o.OddAway    AS DECIMAL(6,2)) AS odd_away,
       CAST(o.OddBttsYes AS DECIMAL(6,2)) AS odd_btts_yes,
       CAST(o.OddBttsNo  AS DECIMAL(6,2)) AS odd_btts_no,
       CAST(o.OddOver25  AS DECIMAL(6,2)) AS odd_over25,
       CAST(o.OddUnder25 AS DECIMAL(6,2)) AS odd_under25,
       CAST(o.OddDc1X    AS DECIMAL(6,2)) AS odd_dc_1x,
       CAST(o.OddDcX2    AS DECIMAL(6,2)) AS odd_dc_x2,
       CAST(o.OddDnbHome AS DECIMAL(6,2)) AS odd_dnb_home,
       CAST(o.OddDnbAway AS DECIMAL(6,2)) AS odd_dnb_away,
       CAST(o.OddOver15  AS DECIMAL(6,2)) AS odd_over15,
       CAST(o.OddUnder15 AS DECIMAL(6,2)) AS odd_under15,
       CAST(o.OddOver35  AS DECIMAL(6,2)) AS odd_over35,
       CAST(o.OddUnder35 AS DECIMAL(6,2)) AS odd_under35,
       o.OddsLabel AS odds_label,
       -- Probabilité du marché ±1,5 / ±3,5 : celle du moteur, sinon déduite des deux cotes (marge retirée)
       COALESCE(b.Mkt_POver15, (1.0 / o.OddOver15) / NULLIF(1.0 / o.OddOver15 + 1.0 / o.OddUnder15, 0)) AS mkt_p_over15,
       COALESCE(b.Mkt_POver35, (1.0 / o.OddOver35) / NULLIF(1.0 / o.OddOver35 + 1.0 / o.OddUnder35, 0)) AS mkt_p_over35
FROM Best b
LEFT JOIN Odds o ON o.MatchId = b.MatchId
WHERE b.Rn = 1;
GO

/* Forme des deux équipes AVANT le match (variables du moteur, feat.MatchFeatures),
   pour les deux dernières années seulement (taille de la base Supabase). */
CREATE OR ALTER VIEW app.vx_SupaMatchStats
AS
SELECT f.MatchId                                   AS match_id,
       CAST(f.home_elo AS DECIMAL(7,1))            AS home_elo,
       CAST(f.away_elo AS DECIMAL(7,1))            AS away_elo,
       CAST(f.home_elo_trend5 AS DECIMAL(6,1))     AS home_elo_trend5,
       CAST(f.away_elo_trend5 AS DECIMAL(6,1))     AS away_elo_trend5,
       CAST(f.home_rank_pts AS SMALLINT)           AS home_rank,
       CAST(f.away_rank_pts AS SMALLINT)           AS away_rank,
       CAST(f.home_ssn_played AS SMALLINT)         AS home_played,
       CAST(f.away_ssn_played AS SMALLINT)         AS away_played,
       CAST(f.home_ssn_avg_pts AS DECIMAL(4,2))    AS home_ppg,
       CAST(f.away_ssn_avg_pts AS DECIMAL(4,2))    AS away_ppg,
       CAST(f.home_ssn_avg_xpts AS DECIMAL(4,2))   AS home_xppg,
       CAST(f.away_ssn_avg_xpts AS DECIMAL(4,2))   AS away_xppg,
       CAST(f.home_all_pts_5 AS DECIMAL(4,2))      AS home_pts5,
       CAST(f.away_all_pts_5 AS DECIMAL(4,2))      AS away_pts5,
       CAST(f.home_all_gf_5 AS DECIMAL(4,2))       AS home_gf5,
       CAST(f.home_all_ga_5 AS DECIMAL(4,2))       AS home_ga5,
       CAST(f.away_all_gf_5 AS DECIMAL(4,2))       AS away_gf5,
       CAST(f.away_all_ga_5 AS DECIMAL(4,2))       AS away_ga5,
       CAST(f.home_all_xgf_5 AS DECIMAL(4,2))      AS home_xgf5,
       CAST(f.home_all_xga_5 AS DECIMAL(4,2))      AS home_xga5,
       CAST(f.away_all_xgf_5 AS DECIMAL(4,2))      AS away_xgf5,
       CAST(f.away_all_xga_5 AS DECIMAL(4,2))      AS away_xga5,
       CAST(f.home_all_btts_10 AS DECIMAL(4,2))    AS home_btts10,
       CAST(f.away_all_btts_10 AS DECIMAL(4,2))    AS away_btts10,
       CAST(f.home_all_over25_10 AS DECIMAL(4,2))  AS home_over25_10,
       CAST(f.away_all_over25_10 AS DECIMAL(4,2))  AS away_over25_10,
       CAST(f.home_rest_days AS DECIMAL(5,1))      AS home_rest_days,
       CAST(f.away_rest_days AS DECIMAL(5,1))      AS away_rest_days
FROM feat.MatchFeatures f
WHERE f.MatchDate >= DATEADD(YEAR, -2, CAST(GETDATE() AS DATE));
GO

/* Bilan des stratégies du dernier backtest simulé (une ligne par famille, ligue, saison). */
CREATE OR ALTER VIEW app.vx_SupaStrategyReports
AS
WITH R AS (
    SELECT s.*, ROW_NUMBER() OVER (PARTITION BY s.StrategyFamily, s.Ligue, s.Saison ORDER BY s.Paris DESC) AS Rn
    FROM sim.vw_SeasonReport s
    WHERE s.ModelRunId = (SELECT MAX(ModelRunId) FROM sim.Runs)
)
SELECT StrategyFamily AS family,
       Ligue          AS league,
       Saison         AS season,
       StrategyName   AS strategy_name,
       Periode        AS period,
       ModelRunId     AS model_run_id,
       Paris          AS bets,
       Mises          AS stakes,
       Profit         AS profit,
       RoiPct         AS roi_pct,
       ReussitePct    AS hit_pct,
       CoteMoy        AS avg_odd,
       CAST(Ordre AS SMALLINT) AS sort_order
FROM R
WHERE Rn = 1;
GO

/* Paris fictifs (stratégies de config.json > live_strategies) avec leur résultat. */
CREATE OR ALTER VIEW app.vx_SupaSignals
AS
SELECT MatchId                        AS match_id,
       StrategyName                   AS strategy,
       Market                         AS market,
       Selection                      AS selection,
       LineValue                      AS line_value,
       CAST(ProbaPct / 100.0 AS FLOAT) AS probability,
       CAST(Cote AS DECIMAL(6,2))     AS odd,
       OddLabel                       AS odd_label,
       CAST(Mise AS DECIMAL(8,2))     AS stake,
       Resultat                       AS result,
       Profit                         AS profit,
       ModelRunId                     AS model_run_id
FROM sim.vw_PaperBets;
GO

/* Journal d'exécution du service (affiché dans l'application : état du scrap, écran « Journal »).
   Exige SQL\2_Corrections\C5_JournalExecutions.sql (vue dbo.vw_ScraperRuns).
   Exécutions des 30 derniers jours ; événements des 14 derniers jours. */
CREATE OR ALTER VIEW app.vx_SupaExecutionRuns
AS
SELECT r.RunId                                                     AS run_id,
       r.TriggerSource                                             AS source,
       CAST((r.DebutParis AT TIME ZONE 'Romance Standard Time') AT TIME ZONE 'UTC' AS DATETIME2(0)) AS started_utc,
       CAST((r.FinParis   AT TIME ZONE 'Romance Standard Time') AT TIME ZONE 'UTC' AS DATETIME2(0)) AS finished_utc,
       DATEDIFF(SECOND, r.DebutParis, r.FinParis)                  AS duration_seconds,
       r.[Status]                                                  AS status,
       r.MatchesFound                                              AS matches_found,
       r.MatchesSaved                                              AS matches_saved,
       r.MatchesSkipped                                            AS matches_skipped,
       r.MatchesFailed                                             AS matches_failed,
       CAST(r.QualityDegraded AS BIT)                              AS quality_degraded,
       r.NbErreursJournal                                          AS error_count,
       CAST(LEFT(r.Summary, 2000) AS NVARCHAR(2000))               AS summary,
       CAST(LEFT(r.ErrorMessage, 2000) AS NVARCHAR(2000))          AS error_message
FROM dbo.vw_ScraperRuns r
WHERE r.DebutParis >= DATEADD(DAY, -30, SYSDATETIME());
GO

CREATE OR ALTER VIEW app.vx_SupaExecutionLogs
AS
SELECT l.RunId                                                     AS run_id,
       CAST(ROW_NUMBER() OVER (PARTITION BY l.RunId ORDER BY l.CreatedAtUtc) AS INT) AS seq,
       l.CreatedAtUtc                                              AS created_utc,
       l.Level                                                     AS level,
       l.Sector                                                    AS sector,
       l.MatchIdent                                                AS match_ident,
       CAST(LEFT(l.Message, 1000) AS NVARCHAR(1000))               AS message,
       CAST(LEFT(l.Exception, 2000) AS NVARCHAR(2000))             AS exception
FROM dbo.AppExecutionLogs l
WHERE l.CreatedAtUtc >= DATEADD(DAY, -14, SYSUTCDATETIME());
GO

-- Le service Windows se connecte avec le compte « scraper » (appsettings) : droit de lecture sur ces vues.
IF USER_ID('scraper') IS NOT NULL
    GRANT SELECT ON SCHEMA::app TO [scraper];
GO

-- Contrôle : nombre de lignes que le job enverra
SELECT 'matches' AS TableSupabase, COUNT(*) AS Lignes FROM app.vx_SupaMatches
UNION ALL SELECT 'predictions', COUNT(*) FROM app.vx_SupaPredictions
UNION ALL SELECT 'match_stats', COUNT(*) FROM app.vx_SupaMatchStats
UNION ALL SELECT 'strategy_reports', COUNT(*) FROM app.vx_SupaStrategyReports
UNION ALL SELECT 'signals', COUNT(*) FROM app.vx_SupaSignals
UNION ALL SELECT 'execution_runs', COUNT(*) FROM app.vx_SupaExecutionRuns
UNION ALL SELECT 'execution_logs', COUNT(*) FROM app.vx_SupaExecutionLogs;
