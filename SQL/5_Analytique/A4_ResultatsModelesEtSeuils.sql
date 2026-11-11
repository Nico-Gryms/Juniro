/* =============================================================================
   Juniro_Evo - Résultats des modèles séparés des simulations, et balayage des seuils
   -----------------------------------------------------------------------------
   Deux couches bien distinctes :

   1) CE QUE DISENT LES MODÈLES (aucune cote, aucun pari)
        ml.vw_PredictionResults   chaque match prédit : sorties des modèles + ce qui s'est passé
        ml.vw_GoalsCalibration    par tranche de buts attendus (2,6 / 2,7 / 2,8...) : buts réels,
                                  % de +1,5 / +2,5 / +3,5 / BTTS et cote d'équilibre de chacun

   2) CE QUE DONNERAIENT DES PARIS SUR CES SORTIES (avec les cotes)
        sim.RuleScan              table remplie par « juniro_analytics scan » (et par chaque backtest)
        sim.vw_RuleScan           chaque règle « signal >= seuil => pari », TUNING et TEST côte à côte

   Script rejouable. Seule sim.RuleScan est créée ; les vues sont en lecture seule.
   Documentation : DOCUMENTATION.md (racine du projet), chapitre 5.2 et 8.3.
   ============================================================================= */
USE [Juniro_Evo];
GO

IF OBJECT_ID('sim.RuleScan') IS NULL
CREATE TABLE sim.RuleScan (
    ModelRunId    INT           NOT NULL,
    LeagueName    NVARCHAR(100) NOT NULL CONSTRAINT DF_sim_RuleScan_League DEFAULT N'Toutes ligues',
    Phase         NVARCHAR(20)  NOT NULL,     -- TUNING / TEST / HOLDOUT
    SignalName    NVARCHAR(40)  NOT NULL,     -- exp_total_goals, p_btts_yes, ...
    Operator      NVARCHAR(2)   NOT NULL,     -- >= ou <=
    Threshold     DECIMAL(6,3)  NOT NULL,
    BetKey        NVARCHAR(20)  NOT NULL,     -- OU15_OVER, BTTS_YES, 1X2_HOME, ...
    NbMatchs      INT           NOT NULL,
    NbWin         INT           NOT NULL,
    NbPush        INT           NOT NULL,
    HitRatePct    FLOAT         NULL,
    BreakEvenOdd  FLOAT         NULL,         -- cote minimale pour ne pas perdre
    NbWithOdds    INT           NOT NULL,     -- matchs où la cote existe en base
    AvgOdd        FLOAT         NULL,
    AvgImpliedPct FLOAT         NULL,         -- 1 / cote, marge comprise
    Profit        FLOAT         NULL,         -- en unités, mise de 1 par pari
    RoiPct        FLOAT         NULL,
    RoiCiLowPct   FLOAT         NULL,         -- intervalle de confiance 90 %
    RoiCiHighPct  FLOAT         NULL,
    AvgClvPct     FLOAT         NULL,
    CreatedAtUtc  DATETIME2(0)  NOT NULL CONSTRAINT DF_sim_RuleScan_Created DEFAULT SYSUTCDATETIME(),
    CONSTRAINT PK_sim_RuleScan PRIMARY KEY (ModelRunId, LeagueName, Phase, SignalName, Operator, Threshold, BetKey),
    CONSTRAINT FK_sim_RuleScan_Run FOREIGN KEY (ModelRunId) REFERENCES ml.ModelRuns (RunId) ON DELETE CASCADE
);
GO

/* 1a) Chaque match prédit avec son issue réelle (backtests et passages quotidiens). */
CREATE OR ALTER VIEW ml.vw_PredictionResults
AS
SELECT p.RunId, r.RunType, r.Label,
       m.MatchDate, m.SeasonName, m.LeagueName, m.HomeTeamName, m.AwayTeamName,
       CAST(p.ExpHomeGoals  AS DECIMAL(4,2)) AS ButsDomAttendus,
       CAST(p.ExpAwayGoals  AS DECIMAL(4,2)) AS ButsExtAttendus,
       CAST(p.ExpTotalGoals AS DECIMAL(4,2)) AS ButsAttendus,
       p.MostLikelyScore AS ScoreProbable,
       CAST(100 * p.PHome         AS DECIMAL(5,1)) AS PctDom,
       CAST(100 * p.PDraw         AS DECIMAL(5,1)) AS PctNul,
       CAST(100 * p.PAway         AS DECIMAL(5,1)) AS PctExt,
       CAST(100 * p.PBttsYes      AS DECIMAL(5,1)) AS PctBtts,
       CAST(100 * p.Score_POver15 AS DECIMAL(5,1)) AS PctPlus15,
       CAST(100 * p.POver25       AS DECIMAL(5,1)) AS PctPlus25,
       CAST(100 * p.Score_POver35 AS DECIMAL(5,1)) AS PctPlus35,
       m.HomeScoreFT, m.AwayScoreFT,
       m.HomeScoreFT + m.AwayScoreFT AS ButsReels,
       CASE WHEN m.HomeScoreFT > m.AwayScoreFT THEN '1' WHEN m.HomeScoreFT = m.AwayScoreFT THEN 'N' ELSE '2' END AS Resultat,
       CASE WHEN p.PHome >= p.PDraw AND p.PHome >= p.PAway THEN '1'
            WHEN p.PAway >= p.PDraw THEN '2' ELSE 'N' END AS ResultatPrevu,
       CASE WHEN m.HomeScoreFT + m.AwayScoreFT > 1 THEN 1 ELSE 0 END AS Plus15,
       CASE WHEN m.HomeScoreFT + m.AwayScoreFT > 2 THEN 1 ELSE 0 END AS Plus25,
       CASE WHEN m.HomeScoreFT + m.AwayScoreFT > 3 THEN 1 ELSE 0 END AS Plus35,
       CASE WHEN m.HomeScoreFT > 0 AND m.AwayScoreFT > 0 THEN 1 ELSE 0 END AS Btts,
       p.MatchId
FROM ml.MatchPredictions p
JOIN ml.ModelRuns r ON r.RunId = p.RunId
JOIN core.vw_Matches m ON m.MatchId = p.MatchId
WHERE m.MatchStatus = 'Finished';
GO

/* 1b) « Buts attendus = 2,7 : combien de +1,5 ? » — par tranche de 0,1 but attendu. */
CREATE OR ALTER VIEW ml.vw_GoalsCalibration
AS
WITH T AS (
    SELECT RunId, Label, FLOOR(ButsAttendus * 10) / 10.0 AS Tranche, ButsAttendus, ButsReels,
           Plus15, Plus25, Plus35, Btts
    FROM ml.vw_PredictionResults
    WHERE RunType = 'BACKTEST'
)
SELECT RunId, Label,
       CAST(Tranche AS DECIMAL(3,1)) AS ButsAttendusDe,
       CAST(Tranche + 0.1 AS DECIMAL(3,1)) AS ButsAttendusA,
       COUNT(*) AS Matchs,
       CAST(AVG(CAST(ButsAttendus AS FLOAT)) AS DECIMAL(4,2)) AS ButsAttendusMoy,
       CAST(AVG(CAST(ButsReels AS FLOAT))    AS DECIMAL(4,2)) AS ButsReelsMoy,
       CAST(100.0 * AVG(CAST(Plus15 AS FLOAT)) AS DECIMAL(5,1)) AS PctPlus15,
       CAST(100.0 * AVG(CAST(Plus25 AS FLOAT)) AS DECIMAL(5,1)) AS PctPlus25,
       CAST(100.0 * AVG(CAST(Plus35 AS FLOAT)) AS DECIMAL(5,1)) AS PctPlus35,
       CAST(100.0 * AVG(CAST(Btts   AS FLOAT)) AS DECIMAL(5,1)) AS PctBtts,
       -- Cote minimale à obtenir pour qu'un pari sur cette tranche ne perde pas d'argent
       CAST(1 / NULLIF(AVG(CAST(Plus15 AS FLOAT)), 0) AS DECIMAL(5,2)) AS CoteEquilibrePlus15,
       CAST(1 / NULLIF(AVG(CAST(Plus25 AS FLOAT)), 0) AS DECIMAL(5,2)) AS CoteEquilibrePlus25,
       CAST(1 / NULLIF(1 - AVG(CAST(Plus25 AS FLOAT)), 0) AS DECIMAL(5,2)) AS CoteEquilibreMoins25,
       CAST(1 / NULLIF(AVG(CAST(Plus35 AS FLOAT)), 0) AS DECIMAL(5,2)) AS CoteEquilibrePlus35,
       CAST(1 / NULLIF(AVG(CAST(Btts   AS FLOAT)), 0) AS DECIMAL(5,2)) AS CoteEquilibreBtts
FROM T
GROUP BY RunId, Label, Tranche;
GO

/* 2) Chaque règle : TUNING et TEST côte à côte. Filtrer sur Verdict et NbTest. */
CREATE OR ALTER VIEW sim.vw_RuleScan
AS
SELECT s.ModelRunId, mr.Label AS Backtest, s.LeagueName AS Ligue,
       CONCAT(s.SignalName, ' ', s.Operator, ' ', CAST(s.Threshold AS DECIMAL(5,2)), ' => ', s.BetKey) AS Regle,
       s.SignalName, s.Operator, s.Threshold, s.BetKey,
       t.NbMatchs AS NbTuning, CAST(t.HitRatePct AS DECIMAL(5,1)) AS ReussiteTuning,
       CAST(t.RoiPct AS DECIMAL(6,2)) AS RoiTuning,
       s.NbMatchs AS NbTest, CAST(s.HitRatePct AS DECIMAL(5,1)) AS ReussiteTest,
       CAST(s.BreakEvenOdd AS DECIMAL(5,2)) AS CoteEquilibreTest,
       s.NbWithOdds AS NbTestAvecCote,
       CAST(s.AvgOdd AS DECIMAL(5,2)) AS CoteMoyTest,
       CAST(s.RoiPct AS DECIMAL(6,2)) AS RoiTest,
       CAST(s.RoiCiLowPct AS DECIMAL(6,2)) AS RoiTestBasIC90,
       CAST(s.RoiCiHighPct AS DECIMAL(6,2)) AS RoiTestHautIC90,
       CAST(s.AvgClvPct AS DECIMAL(6,2)) AS ClvTest,
       CASE WHEN s.RoiPct IS NULL THEN N'PAS DE COTE EN BASE'
            WHEN t.RoiPct > 0 AND s.RoiCiLowPct > 0 THEN N'PROMETTEUSE'
            WHEN t.RoiPct > 0 AND s.RoiPct > 0 THEN N'POSITIVE MAIS NON PROUVÉE'
            ELSE N'NON RENTABLE' END AS Verdict
FROM sim.RuleScan s
JOIN ml.ModelRuns mr ON mr.RunId = s.ModelRunId
LEFT JOIN sim.RuleScan t ON t.ModelRunId = s.ModelRunId AND t.Phase = 'TUNING'
     AND t.LeagueName = s.LeagueName
     AND t.SignalName = s.SignalName AND t.Operator = s.Operator
     AND t.Threshold = s.Threshold AND t.BetKey = s.BetKey
WHERE s.Phase = 'TEST';
GO

-- Exemples
-- Buts attendus du dernier backtest, tranche par tranche :
SELECT * FROM ml.vw_GoalsCalibration
WHERE RunId = (SELECT MAX(RunId) FROM ml.ModelRuns WHERE RunType = 'BACKTEST')
ORDER BY ButsAttendusDe;

-- « Buts attendus >= 2,7 » : que donne chaque pari ?
SELECT Ligue, Regle, NbTest, ReussiteTest, CoteEquilibreTest, CoteMoyTest, RoiTuning, RoiTest, Verdict
FROM sim.vw_RuleScan
WHERE ModelRunId = (SELECT MAX(RunId) FROM ml.ModelRuns WHERE RunType = 'BACKTEST')
  AND SignalName = 'exp_total_goals' AND Operator = '>=' AND Threshold = 2.7
ORDER BY Ligue, RoiTest DESC;

-- Les meilleures règles qui tiennent sur les deux périodes :
SELECT TOP (30) Ligue, Regle, NbTuning, RoiTuning, NbTestAvecCote, RoiTest, RoiTestBasIC90, ReussiteTest, CoteMoyTest, Verdict
FROM sim.vw_RuleScan
WHERE ModelRunId = (SELECT MAX(RunId) FROM ml.ModelRuns WHERE RunType = 'BACKTEST')
  AND NbTestAvecCote >= 100 AND RoiTuning > 0
ORDER BY RoiTestBasIC90 DESC;
