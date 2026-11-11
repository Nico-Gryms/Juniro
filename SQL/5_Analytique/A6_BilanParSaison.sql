/* =============================================================================
   Juniro_Evo - Bilan des stratégies par ligue et par saison
   -----------------------------------------------------------------------------
   Pour chaque stratégie et chaque ligue, la variante retenue sur la période de réglage,
   jugée SAISON PAR SAISON, puis deux totaux :
     Saison = 'Test'   saisons de test seulement : la seule mesure honnête
     Saison = 'Total'  toutes les saisons, réglage compris (donc flatteur)
   Il n'y a plus de cumul entre ligues : chaque couple stratégie x ligue est un
   portefeuille à part.

   Lit sim.Bets (paris des variantes retenues, écrits par simulate / backtest).
   Exige A5_SimulationParLigue.sql. Script rejouable, lecture seule.
   Documentation : DOCUMENTATION.md (racine du projet), chapitre 8.4.
   ============================================================================= */
USE [Juniro_Evo];
GO

CREATE OR ALTER VIEW sim.vw_SeasonReport
AS
WITH B AS (
    SELECT r.ModelRunId, r.StrategyFamily, r.LeagueName, r.StrategyName, r.Phase,
           m.SeasonName, b.Stake, b.Profit, b.Outcome, b.OddTaken
    FROM sim.Bets b
    JOIN sim.Runs r ON r.SimRunId = b.SimRunId
    JOIN core.vw_Matches m ON m.MatchId = b.MatchId
    WHERE r.IsSelected = 1 AND r.IsPortfolio = 0 AND r.LeagueName IS NOT NULL
)
SELECT ModelRunId, StrategyFamily, LeagueName AS Ligue, StrategyName, Saison, Periode,
       COUNT(*) AS Paris,
       CAST(SUM(Stake) AS DECIMAL(10,2)) AS Mises,
       CAST(SUM(Profit) AS DECIMAL(10,2)) AS Profit,
       CAST(100 * SUM(Profit) / NULLIF(SUM(Stake), 0) AS DECIMAL(6,2)) AS RoiPct,
       CAST(100.0 * SUM(CASE WHEN Outcome = 'WIN' THEN 1 ELSE 0 END)
            / NULLIF(SUM(CASE WHEN Outcome <> 'PUSH' THEN 1 ELSE 0 END), 0) AS DECIMAL(5,1)) AS ReussitePct,
       CAST(AVG(OddTaken) AS DECIMAL(5,2)) AS CoteMoy,
       Ordre
FROM (
    -- une ligne par saison
    SELECT ModelRunId, StrategyFamily, LeagueName, StrategyName, SeasonName AS Saison,
           CASE Phase WHEN 'TUNING' THEN N'réglage' WHEN 'TEST' THEN N'test' ELSE N'en cours' END AS Periode,
           1 AS Ordre, Stake, Profit, Outcome, OddTaken
    FROM B
    UNION ALL
    -- total des saisons de test
    SELECT ModelRunId, StrategyFamily, LeagueName, StrategyName, N'Test', N'test', 2,
           Stake, Profit, Outcome, OddTaken
    FROM B WHERE Phase = 'TEST'
    UNION ALL
    -- total toutes saisons
    SELECT ModelRunId, StrategyFamily, LeagueName, StrategyName, N'Total', N'', 3,
           Stake, Profit, Outcome, OddTaken
    FROM B
) x
GROUP BY ModelRunId, StrategyFamily, LeagueName, StrategyName, Saison, Periode, Ordre;
GO

-- Exemple : bilan du dernier calcul, stratégie par stratégie, ligue par ligue, saison par saison
SELECT StrategyFamily, Ligue, Saison, Periode, Paris, Profit, RoiPct, ReussitePct, CoteMoy
FROM sim.vw_SeasonReport
WHERE ModelRunId = (SELECT MAX(ModelRunId) FROM sim.Runs)
ORDER BY StrategyFamily, Ligue, Ordre, Saison;

-- Vue d'ensemble : une ligne par stratégie et par ligue, saisons en colonnes (ROI %)
DECLARE @Run INT = (SELECT MAX(ModelRunId) FROM sim.Runs);
SELECT StrategyFamily, Ligue,
       MAX(CASE WHEN Saison = '2022-2023' THEN RoiPct END) AS [2022-23],
       MAX(CASE WHEN Saison = '2023-2024' THEN RoiPct END) AS [2023-24],
       MAX(CASE WHEN Saison = '2024-2025' THEN RoiPct END) AS [2024-25],
       MAX(CASE WHEN Saison = '2025-2026' THEN RoiPct END) AS [2025-26],
       MAX(CASE WHEN Saison = '2026-2027' THEN RoiPct END) AS [2026-27],
       MAX(CASE WHEN Saison = 'Test'      THEN RoiPct END) AS RoiTest,
       MAX(CASE WHEN Saison = 'Test'      THEN Paris  END) AS ParisTest,
       MAX(CASE WHEN Saison = 'Total'     THEN RoiPct END) AS RoiTotal
FROM sim.vw_SeasonReport
WHERE ModelRunId = @Run
GROUP BY StrategyFamily, Ligue
ORDER BY StrategyFamily, RoiTest DESC;
