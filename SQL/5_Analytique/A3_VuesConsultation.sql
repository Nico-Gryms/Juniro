/* =============================================================================
   Juniro_Evo - Vues de consultation (résultats des modèles et des simulations)
   -----------------------------------------------------------------------------
   À exécuter APRÈS le premier passage du moteur Python (commande "features" ou
   "backtest"), car app.vw_TeamForm lit la table feat.MatchFeatures qu'il crée.

     app.vw_UpcomingPredictions   matchs à venir : buts attendus, probabilités, cotes justes, cotes du marché
     app.vw_LiveSignals           paris proposés par les stratégies actives (dernier passage quotidien)
     app.vw_TeamForm              état de forme actuel de chaque équipe
     sim.vw_StrategySummary       variante retenue par stratégie ET par ligue : TUNING, TEST, saison en cours
     sim.vw_VariantsByLeague      toutes les variantes essayées, ligue par ligue
     sim.vw_BetsBreakdown         paris simulés par saison et par ligue
     ml.vw_Calibration            les probabilités annoncées se réalisent-elles ? (par tranche de 5 %)

   Script rejouable (CREATE OR ALTER). Lecture seule.
   Documentation : DOCUMENTATION.md (racine du projet), chapitre 8.5.
   ============================================================================= */
USE [Juniro_Evo];
GO

/* Dernière prédiction LIVE de chaque match à venir, avec la cote la plus récente avant le match. */
CREATE OR ALTER VIEW app.vw_UpcomingPredictions
AS
WITH LastPred AS (
    SELECT p.*, ROW_NUMBER() OVER (PARTITION BY p.MatchId ORDER BY p.RunId DESC) AS Rn
    FROM ml.MatchPredictions p
    JOIN ml.ModelRuns r ON r.RunId = p.RunId AND r.RunType = 'LIVE'
),
LastOdds AS (
    SELECT MatchId, MarketCode, SelectionCode, OddValue, SnapshotLabel,
           ROW_NUMBER() OVER (PARTITION BY MatchId, MarketCode, SelectionCode, LineValue
                              ORDER BY QuoteDateUtc DESC, BookmakerId) AS Rn
    FROM core.vw_OddsByLabel
    WHERE SnapshotLabel <> 'CLOSING'
      AND (MarketCode IN ('1X2', 'BTTS') OR (MarketCode = 'OVER_UNDER' AND LineValue = 2.5))
),
Odds AS (
    SELECT MatchId,
           MAX(CASE WHEN MarketCode = '1X2'  AND SelectionCode = 'HOME'  THEN OddValue END) AS OddHome,
           MAX(CASE WHEN MarketCode = '1X2'  AND SelectionCode = 'DRAW'  THEN OddValue END) AS OddDraw,
           MAX(CASE WHEN MarketCode = '1X2'  AND SelectionCode = 'AWAY'  THEN OddValue END) AS OddAway,
           MAX(CASE WHEN MarketCode = 'BTTS' AND SelectionCode = 'YES'   THEN OddValue END) AS OddBttsYes,
           MAX(CASE WHEN MarketCode = 'OVER_UNDER' AND SelectionCode = 'OVER'  THEN OddValue END) AS OddOver25,
           MAX(CASE WHEN MarketCode = 'OVER_UNDER' AND SelectionCode = 'UNDER' THEN OddValue END) AS OddUnder25,
           MAX(CASE WHEN MarketCode = '1X2' THEN SnapshotLabel END) AS OddsLabel
    FROM LastOdds WHERE Rn = 1
    GROUP BY MatchId
)
SELECT m.MatchDate, m.LeagueName, m.RoundNumber, m.HomeTeamName, m.AwayTeamName,
       CAST(p.ExpHomeGoals  AS DECIMAL(4,2)) AS ButsDom,
       CAST(p.ExpAwayGoals  AS DECIMAL(4,2)) AS ButsExt,
       CAST(p.ExpTotalGoals AS DECIMAL(4,2)) AS ButsTotal,
       p.MostLikelyScore                     AS ScoreProbable,
       CAST(100 * p.PHome    AS DECIMAL(5,1)) AS PctDom,
       CAST(100 * p.PDraw    AS DECIMAL(5,1)) AS PctNul,
       CAST(100 * p.PAway    AS DECIMAL(5,1)) AS PctExt,
       CAST(100 * p.PBttsYes AS DECIMAL(5,1)) AS PctBtts,
       CAST(100 * p.POver25  AS DECIMAL(5,1)) AS PctPlus25,
       -- Cote juste = 1 / probabilité du modèle ; parier seulement si la cote offerte est au-dessus
       CAST(1 / NULLIF(p.PHome, 0)     AS DECIMAL(6,2)) AS CoteJusteDom,
       CAST(1 / NULLIF(p.PDraw, 0)     AS DECIMAL(6,2)) AS CoteJusteNul,
       CAST(1 / NULLIF(p.PAway, 0)     AS DECIMAL(6,2)) AS CoteJusteExt,
       CAST(1 / NULLIF(p.PBttsYes, 0)  AS DECIMAL(6,2)) AS CoteJusteBtts,
       CAST(1 / NULLIF(p.POver25, 0)   AS DECIMAL(6,2)) AS CoteJustePlus25,
       CAST(1 / NULLIF(1 - p.POver25, 0) AS DECIMAL(6,2)) AS CoteJusteMoins25,
       o.OddHome, o.OddDraw, o.OddAway, o.OddBttsYes, o.OddOver25, o.OddUnder25, o.OddsLabel,
       CAST(100 * (p.PHome   * o.OddHome    - 1) AS DECIMAL(6,1)) AS EdgeDomPct,
       CAST(100 * (p.PDraw   * o.OddDraw    - 1) AS DECIMAL(6,1)) AS EdgeNulPct,
       CAST(100 * (p.PAway   * o.OddAway    - 1) AS DECIMAL(6,1)) AS EdgeExtPct,
       CAST(100 * (p.PBttsYes* o.OddBttsYes - 1) AS DECIMAL(6,1)) AS EdgeBttsPct,
       CAST(100 * (p.POver25 * o.OddOver25  - 1) AS DECIMAL(6,1)) AS EdgePlus25Pct,
       CAST(100 * ((1 - p.POver25) * o.OddUnder25 - 1) AS DECIMAL(6,1)) AS EdgeMoins25Pct,
       m.MatchId, p.RunId, p.TrainedUntil
FROM LastPred p
JOIN core.vw_Matches m ON m.MatchId = p.MatchId
LEFT JOIN Odds o ON o.MatchId = p.MatchId
WHERE p.Rn = 1
  AND m.MatchStatus = 'Scheduled'
  AND m.MatchDate >= CAST(GETDATE() AS DATE);
GO

CREATE OR ALTER VIEW app.vw_LiveSignals
AS
SELECT m.MatchDate, m.LeagueName, m.HomeTeamName, m.AwayTeamName,
       s.StrategyName, s.Market, s.Selection, s.LineValue,
       CAST(100 * s.Probability AS DECIMAL(5,1)) AS ProbaPct,
       s.OddSeen, s.OddLabel,
       CAST(s.EdgePct AS DECIMAL(6,1)) AS EdgePct,
       CAST(s.StakeUnits AS DECIMAL(6,2)) AS Mise,
       s.CreatedAtUtc, s.ModelRunId, s.MatchId
FROM sim.LiveSignals s
JOIN core.vw_Matches m ON m.MatchId = s.MatchId
WHERE s.ModelRunId = (SELECT MAX(RunId) FROM ml.ModelRuns WHERE RunType = 'LIVE')
  AND m.MatchStatus = 'Scheduled';
GO

CREATE OR ALTER VIEW app.vw_TeamForm
AS
WITH Sides AS (
    SELECT MatchId, MatchDate, league_name AS LeagueName, home_id AS TeamId, home_name AS TeamName,
           home_elo AS Elo, home_elo_trend5 AS EloEvol5, home_rank_pts AS Classement,
           home_rank_xpts AS ClassementXPts, home_rank_form AS ClassementForme,
           home_ssn_played AS Joues, home_ssn_avg_pts AS PtsParMatch, home_ssn_avg_xpts AS XPtsParMatch,
           home_ssn_luck AS Chance, home_all_pts_5 AS Pts5, home_all_gf_5 AS ButsPour5,
           home_all_ga_5 AS ButsContre5, home_all_xgf_5 AS XgPour5, home_all_xga_5 AS XgContre5,
           home_all_btts_10 AS Btts10, home_all_over25_10 AS Plus25_10, home_momentum_pts AS Momentum
    FROM feat.MatchFeatures
    UNION ALL
    SELECT MatchId, MatchDate, league_name, away_id, away_name,
           away_elo, away_elo_trend5, away_rank_pts,
           away_rank_xpts, away_rank_form,
           away_ssn_played, away_ssn_avg_pts, away_ssn_avg_xpts,
           away_ssn_luck, away_all_pts_5, away_all_gf_5,
           away_all_ga_5, away_all_xgf_5, away_all_xga_5,
           away_all_btts_10, away_all_over25_10, away_momentum_pts
    FROM feat.MatchFeatures
),
Latest AS (
    SELECT *, ROW_NUMBER() OVER (PARTITION BY TeamId ORDER BY MatchDate DESC) AS Rn
    FROM Sides
    WHERE MatchDate <= DATEADD(DAY, 7, GETDATE())
)
SELECT LeagueName, TeamName, MatchDate AS ProchainOuDernierMatch,
       CAST(Elo AS DECIMAL(6,0)) AS Elo, CAST(EloEvol5 AS DECIMAL(6,1)) AS EloEvol5,
       Classement, ClassementXPts, ClassementForme, Joues,
       CAST(PtsParMatch AS DECIMAL(4,2)) AS PtsParMatch, CAST(XPtsParMatch AS DECIMAL(4,2)) AS XPtsParMatch,
       CAST(Chance AS DECIMAL(4,2)) AS Chance,
       CAST(Pts5 AS DECIMAL(4,2)) AS Pts5, CAST(ButsPour5 AS DECIMAL(4,2)) AS ButsPour5,
       CAST(ButsContre5 AS DECIMAL(4,2)) AS ButsContre5, CAST(XgPour5 AS DECIMAL(4,2)) AS XgPour5,
       CAST(XgContre5 AS DECIMAL(4,2)) AS XgContre5, CAST(100 * Btts10 AS DECIMAL(5,0)) AS PctBtts10,
       CAST(100 * Plus25_10 AS DECIMAL(5,0)) AS PctPlus25_10, CAST(Momentum AS DECIMAL(4,2)) AS Momentum,
       TeamId
FROM Latest
WHERE Rn = 1;
GO

/* Pour chaque backtest, chaque famille et chaque ligue : la variante retenue sur TUNING,
   puis son résultat en TEST et sur la saison en cours. Ligue « Portefeuille » = les variantes
   retenues de toutes les ligues jouées ensemble. Exige A5_SimulationParLigue.sql. */
CREATE OR ALTER VIEW sim.vw_StrategySummary
AS
SELECT r.ModelRunId, mr.Label AS Backtest, mr.TrainWindow, mr.UseMarketFeatures,
       r.StrategyFamily,
       CASE WHEN r.IsPortfolio = 1 THEN N'Portefeuille' ELSE ISNULL(r.LeagueName, N'Toutes ligues') END AS Ligue,
       r.IsPortfolio, r.StrategyName, r.NbStrategiesTested,
       t.NbBets AS ParisTuning, CAST(t.RoiPct AS DECIMAL(6,2)) AS RoiTuning,
       r.NbBets AS ParisTest,  CAST(r.RoiPct AS DECIMAL(6,2)) AS RoiTest,
       CAST(r.RoiCiLowPct AS DECIMAL(6,2)) AS RoiTestBasIC90,
       CAST(r.RoiCiHighPct AS DECIMAL(6,2)) AS RoiTestHautIC90,
       CAST(r.Profit AS DECIMAL(8,2)) AS ProfitTest,
       CAST(r.HitRatePct AS DECIMAL(5,1)) AS ReussiteTest,
       CAST(r.AvgOdd AS DECIMAL(5,2)) AS CoteMoyTest,
       CAST(r.AvgClvPct AS DECIMAL(6,2)) AS ClvTest,
       CAST(r.MaxDrawdown AS DECIMAL(8,1)) AS PireBaisseTest,
       CASE WHEN r.NbBets = 0 THEN N'AUCUN PARI'
            WHEN r.RoiCiLowPct > 0 AND t.RoiPct > 0 THEN N'PROMETTEUSE'
            WHEN r.RoiPct > 0 THEN N'POSITIVE MAIS NON PROUVÉE'
            ELSE N'NON RENTABLE' END AS Verdict,
       -- Saison en cours (réserve), seulement si le backtest l'a incluse (--include-holdout)
       h.NbBets AS ParisSaisonEnCours, CAST(h.RoiPct AS DECIMAL(6,2)) AS RoiSaisonEnCours,
       CAST(h.Profit AS DECIMAL(8,2)) AS ProfitSaisonEnCours,
       r.CreatedAtUtc
FROM sim.Runs r
JOIN ml.ModelRuns mr ON mr.RunId = r.ModelRunId
LEFT JOIN sim.Runs t ON t.ModelRunId = r.ModelRunId AND t.StrategyName = r.StrategyName AND t.Phase = 'TUNING'
     AND ISNULL(t.LeagueName, N'') = ISNULL(r.LeagueName, N'') AND t.IsPortfolio = r.IsPortfolio AND t.IsSelected = 1
LEFT JOIN sim.Runs h ON h.ModelRunId = r.ModelRunId AND h.StrategyName = r.StrategyName AND h.Phase = 'HOLDOUT'
     AND ISNULL(h.LeagueName, N'') = ISNULL(r.LeagueName, N'') AND h.IsPortfolio = r.IsPortfolio AND h.IsSelected = 1
WHERE r.Phase = 'TEST' AND r.IsSelected = 1;
GO

/* Toutes les variantes essayées, ligue par ligue (pas seulement les retenues) : pour voir
   si une ligue réagit bien à toute une famille, ou seulement à une variante isolée. */
CREATE OR ALTER VIEW sim.vw_VariantsByLeague
AS
SELECT r.ModelRunId, r.StrategyFamily, ISNULL(r.LeagueName, N'Toutes ligues') AS Ligue, r.StrategyName,
       t.NbBets AS ParisTuning, CAST(t.RoiPct AS DECIMAL(6,2)) AS RoiTuning,
       r.NbBets AS ParisTest, CAST(r.RoiPct AS DECIMAL(6,2)) AS RoiTest,
       CAST(r.HitRatePct AS DECIMAL(5,1)) AS ReussiteTest, CAST(r.AvgOdd AS DECIMAL(5,2)) AS CoteMoyTest,
       r.IsSelected
FROM sim.Runs r
LEFT JOIN sim.Runs t ON t.ModelRunId = r.ModelRunId AND t.StrategyName = r.StrategyName AND t.Phase = 'TUNING'
     AND ISNULL(t.LeagueName, N'') = ISNULL(r.LeagueName, N'') AND t.IsPortfolio = 0
WHERE r.Phase = 'TEST' AND r.IsPortfolio = 0;
GO

CREATE OR ALTER VIEW sim.vw_BetsBreakdown
AS
SELECT r.ModelRunId, r.StrategyFamily, r.StrategyName, r.Phase,
       m.SeasonName, m.LeagueName, b.Market, b.Selection,
       COUNT(*) AS Paris,
       CAST(SUM(b.Profit) AS DECIMAL(10,2)) AS Profit,
       CAST(100 * SUM(b.Profit) / NULLIF(SUM(b.Stake), 0) AS DECIMAL(6,2)) AS RoiPct,
       CAST(100.0 * SUM(CASE WHEN b.Outcome = 'WIN' THEN 1 ELSE 0 END)
            / NULLIF(SUM(CASE WHEN b.Outcome <> 'PUSH' THEN 1 ELSE 0 END), 0) AS DECIMAL(5,1)) AS ReussitePct,
       CAST(AVG(b.OddTaken) AS DECIMAL(5,2)) AS CoteMoy,
       CAST(AVG(b.ClvPct) AS DECIMAL(6,2)) AS ClvPct
FROM sim.Bets b
JOIN sim.Runs r ON r.SimRunId = b.SimRunId
JOIN core.vw_Matches m ON m.MatchId = b.MatchId
GROUP BY r.ModelRunId, r.StrategyFamily, r.StrategyName, r.Phase, m.SeasonName, m.LeagueName, b.Market, b.Selection;
GO

/* Calibration : quand le modèle annonce 60 %, l'événement arrive-t-il 60 % du temps ? */
CREATE OR ALTER VIEW ml.vw_Calibration
AS
WITH Obs AS (
    SELECT p.RunId, 'Victoire dom.' AS Marche, p.PHome AS Proba,
           CASE WHEN m.HomeScoreFT > m.AwayScoreFT THEN 1.0 ELSE 0.0 END AS Realise
    FROM ml.MatchPredictions p JOIN core.vw_Matches m ON m.MatchId = p.MatchId
    WHERE m.MatchStatus = 'Finished'
    UNION ALL
    SELECT p.RunId, 'Nul', p.PDraw, CASE WHEN m.HomeScoreFT = m.AwayScoreFT THEN 1.0 ELSE 0.0 END
    FROM ml.MatchPredictions p JOIN core.vw_Matches m ON m.MatchId = p.MatchId
    WHERE m.MatchStatus = 'Finished'
    UNION ALL
    SELECT p.RunId, 'BTTS', p.PBttsYes, CASE WHEN m.HomeScoreFT > 0 AND m.AwayScoreFT > 0 THEN 1.0 ELSE 0.0 END
    FROM ml.MatchPredictions p JOIN core.vw_Matches m ON m.MatchId = p.MatchId
    WHERE m.MatchStatus = 'Finished'
    UNION ALL
    SELECT p.RunId, 'Plus 2,5', p.POver25, CASE WHEN m.HomeScoreFT + m.AwayScoreFT > 2 THEN 1.0 ELSE 0.0 END
    FROM ml.MatchPredictions p JOIN core.vw_Matches m ON m.MatchId = p.MatchId
    WHERE m.MatchStatus = 'Finished'
)
SELECT RunId, Marche,
       CAST(FLOOR(Proba * 20) * 5 AS INT) AS TranchePct,
       COUNT(*) AS Matchs,
       CAST(100 * AVG(Proba)   AS DECIMAL(5,1)) AS ProbaAnnoncee,
       CAST(100 * AVG(Realise) AS DECIMAL(5,1)) AS FrequenceReelle
FROM Obs
WHERE Proba IS NOT NULL
GROUP BY RunId, Marche, CAST(FLOOR(Proba * 20) * 5 AS INT);
GO

-- Contrôle
SELECT TOP (20) * FROM app.vw_UpcomingPredictions ORDER BY MatchDate;
SELECT * FROM sim.vw_StrategySummary
WHERE ModelRunId = (SELECT MAX(ModelRunId) FROM sim.Runs)
ORDER BY StrategyFamily, IsPortfolio, RoiTestBasIC90 DESC;
