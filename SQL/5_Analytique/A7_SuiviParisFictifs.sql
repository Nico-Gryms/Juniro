/* =============================================================================
   Juniro_Evo - Suivi des paris fictifs (stratégies de config.json > live_strategies)
   -----------------------------------------------------------------------------
   Chaque jour, la commande « daily » propose des paris pour les matchs des 4 prochains
   jours (sim.LiveSignals) : un même match est donc proposé plusieurs fois (J-3, J-2,
   J-1, jour J), avec une cote qui bouge. Le pari fictif retenu est la DERNIÈRE
   proposition faite AVANT le coup d'envoi : c'est celle qu'on aurait jouée.

     sim.vw_PaperBets     un pari fictif par match et stratégie, avec son résultat
                          (WIN / LOSE / PUSH, ou PENDING tant que le match n'est pas joué)
     sim.vw_PaperSummary  bilan par stratégie : tous mois confondus (Mois = 'Total') et
                          mois par mois

   Une stratégie qui disparaît des propositions d'un jour à l'autre (cote qui a bougé
   sous le seuil) n'est PAS jouée : seule la proposition faite au dernier passage avant
   le match compte. Script rejouable, lecture seule (aucune table modifiée).
   Exige A1 à A3. Documentation : DOCUMENTATION.md (racine du projet), chapitre 8.5.
   ============================================================================= */
USE [Juniro_Evo];
GO

CREATE OR ALTER VIEW sim.vw_PaperBets
AS
WITH Runs AS (
    -- Dernier passage « daily » avant le coup d'envoi de chaque match
    SELECT m.MatchId, MAX(r.RunId) AS LastRunId
    FROM ml.ModelRuns r
    JOIN ml.MatchPredictions p ON p.RunId = r.RunId
    JOIN core.vw_Matches m ON m.MatchId = p.MatchId
    WHERE r.RunType = 'LIVE'
      AND CAST((r.CreatedAtUtc AT TIME ZONE 'UTC') AT TIME ZONE 'Romance Standard Time' AS DATETIME2(0)) < m.MatchDate
    GROUP BY m.MatchId
), S AS (
    SELECT ls.*
    FROM sim.LiveSignals ls
    JOIN Runs ON Runs.MatchId = ls.MatchId AND Runs.LastRunId = ls.ModelRunId
)
SELECT m.MatchDate, m.LeagueName, m.SeasonName, m.HomeTeamName, m.AwayTeamName,
       S.StrategyName, S.Market, S.Selection, S.LineValue,
       CAST(100 * S.Probability AS DECIMAL(5,1)) AS ProbaPct,
       S.OddSeen AS Cote, S.OddLabel, S.StakeUnits AS Mise,
       m.HomeScoreFT, m.AwayScoreFT,
       R.Resultat,
       CAST(CASE R.Resultat
                WHEN 'WIN'  THEN S.StakeUnits * (S.OddSeen - 1)
                WHEN 'LOSE' THEN -S.StakeUnits
                WHEN 'PUSH' THEN 0 END AS DECIMAL(8,2)) AS Profit,
       S.ModelRunId, S.MatchId
FROM S
JOIN core.vw_Matches m ON m.MatchId = S.MatchId
CROSS APPLY (SELECT h = m.HomeScoreFT, a = m.AwayScoreFT) g
CROSS APPLY (SELECT Resultat =
    CASE
        WHEN m.MatchStatus <> 'Finished' OR g.h IS NULL OR g.a IS NULL THEN 'PENDING'
        WHEN S.Market = '1X2' THEN
             CASE WHEN (S.Selection = 'HOME' AND g.h > g.a) OR (S.Selection = 'DRAW' AND g.h = g.a)
                    OR (S.Selection = 'AWAY' AND g.h < g.a) THEN 'WIN' ELSE 'LOSE' END
        WHEN S.Market = 'DNB' THEN
             CASE WHEN g.h = g.a THEN 'PUSH'
                  WHEN (S.Selection = 'HOME' AND g.h > g.a) OR (S.Selection = 'AWAY' AND g.h < g.a) THEN 'WIN'
                  ELSE 'LOSE' END
        WHEN S.Market = 'DC' THEN
             CASE WHEN (S.Selection = '1X' AND g.h >= g.a) OR (S.Selection = 'X2' AND g.h <= g.a)
                    OR (S.Selection = '12' AND g.h <> g.a) THEN 'WIN' ELSE 'LOSE' END
        WHEN S.Market = 'BTTS' THEN
             CASE WHEN S.Selection = 'YES' AND g.h > 0 AND g.a > 0 THEN 'WIN'
                  WHEN S.Selection = 'NO' AND (g.h = 0 OR g.a = 0) THEN 'WIN'
                  ELSE 'LOSE' END
        WHEN S.Market LIKE 'OU%' THEN
             CASE WHEN (S.Selection = 'OVER' AND g.h + g.a > S.LineValue)
                    OR (S.Selection = 'UNDER' AND g.h + g.a < S.LineValue) THEN 'WIN' ELSE 'LOSE' END
    END) R;
GO

CREATE OR ALTER VIEW sim.vw_PaperSummary
AS
SELECT StrategyName,
       COALESCE(CONVERT(CHAR(7), MatchDate, 126), 'Total') AS Mois,
       MIN(LeagueName) AS Ligue,
       COUNT(*) AS Paris,
       SUM(CASE WHEN Resultat = 'PENDING' THEN 1 ELSE 0 END) AS EnAttente,
       SUM(CASE WHEN Resultat = 'WIN' THEN 1 ELSE 0 END) AS Gagnes,
       SUM(CASE WHEN Resultat = 'LOSE' THEN 1 ELSE 0 END) AS Perdus,
       SUM(CASE WHEN Resultat = 'PUSH' THEN 1 ELSE 0 END) AS Rembourses,
       CAST(SUM(CASE WHEN Resultat <> 'PENDING' THEN Mise END) AS DECIMAL(10,2)) AS Mises,
       CAST(SUM(Profit) AS DECIMAL(10,2)) AS Profit,
       CAST(100.0 * SUM(Profit) / NULLIF(SUM(CASE WHEN Resultat <> 'PENDING' THEN Mise END), 0) AS DECIMAL(6,1)) AS RoiPct,
       CAST(100.0 * SUM(CASE WHEN Resultat = 'WIN' THEN 1 ELSE 0 END)
            / NULLIF(SUM(CASE WHEN Resultat IN ('WIN', 'LOSE') THEN 1 ELSE 0 END), 0) AS DECIMAL(5,1)) AS ReussitePct,
       CAST(AVG(Cote) AS DECIMAL(5,2)) AS CoteMoyenne
FROM sim.vw_PaperBets
GROUP BY GROUPING SETS ((StrategyName, CONVERT(CHAR(7), MatchDate, 126)), (StrategyName));
GO

-- Contrôle
SELECT * FROM sim.vw_PaperSummary WHERE Mois = 'Total' ORDER BY StrategyName;
SELECT TOP 50 * FROM sim.vw_PaperBets ORDER BY MatchDate DESC;
