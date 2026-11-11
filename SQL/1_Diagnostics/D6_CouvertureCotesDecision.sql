/* =============================================================================
   Juniro_Evo - Couverture des cotes « de décision » par saison et par semaine
   -----------------------------------------------------------------------------
   Le moteur ne parie que sur une cote connue AVANT le match : relevé DAY (jour J, 03h00),
   sinon J-1, J-2, J-3. Ces relevés n'existent que si le SERVICE JOURNALIER a vu le match
   avant son coup d'envoi. Le rattrapage (--backfill), lui, ne relève que OPENING et CLOSING.

   1) Par saison : part des matchs terminés qui ont un relevé de décision complet
      (1X2, BTTS, Plus/Moins 2,5), un OPENING, un CLOSING.
   2) Saison en cours, semaine par semaine : à partir de quand les relevés de décision
      manquent (= le service journalier ne tournait pas).
   3) Dernières exécutions du scraper (service journalier et rattrapage).
   Lecture seule.
   Documentation : DOCUMENTATION.md (racine du projet), chapitre 4.3 et 6.6.
   ============================================================================= */
USE [Juniro_Evo];
GO

;WITH Q AS (
    SELECT q.MatchId, q.MarketCode, q.SelectionCode, q.LineValue, q.SnapshotLabel
    FROM dbo.OddsQuotes q
    WHERE q.OddValue > 1
),
M AS (
    SELECT m.MatchId, REPLACE(s.SeasonName, '/', '-') AS Saison, m.MatchDate
    FROM dbo.Matches m
    JOIN dbo.Seasons s ON s.SeasonId = m.SeasonId
    WHERE m.MatchStatus = 'Finished'
),
Par AS (
    SELECT M.MatchId, M.Saison, M.MatchDate,
           -- décision complète = au moins une étiquette de décision avec les 3 sélections 1X2
           MAX(CASE WHEN d.Nb1X2 = 3 THEN 1 ELSE 0 END) AS Dec1X2,
           MAX(CASE WHEN d.NbBtts = 2 THEN 1 ELSE 0 END) AS DecBtts,
           MAX(CASE WHEN d.NbOu = 2 THEN 1 ELSE 0 END) AS DecOu25,
           MAX(CASE WHEN o.HasOpening = 1 THEN 1 ELSE 0 END) AS AOpening,
           MAX(CASE WHEN o.HasClosing = 1 THEN 1 ELSE 0 END) AS AClosing
    FROM M
    OUTER APPLY (
        SELECT Q.SnapshotLabel,
               COUNT(DISTINCT CASE WHEN Q.MarketCode = '1X2' THEN Q.SelectionCode END) AS Nb1X2,
               COUNT(DISTINCT CASE WHEN Q.MarketCode = 'BTTS' THEN Q.SelectionCode END) AS NbBtts,
               COUNT(DISTINCT CASE WHEN Q.MarketCode = 'OVER_UNDER' AND Q.LineValue = 2.5
                                   THEN Q.SelectionCode END) AS NbOu
        FROM Q
        WHERE Q.MatchId = M.MatchId AND Q.SnapshotLabel IN ('DAY', 'J-1', 'J-2', 'J-3')
        GROUP BY Q.SnapshotLabel
    ) d
    OUTER APPLY (
        SELECT MAX(CASE WHEN Q.SnapshotLabel = 'OPENING' THEN 1 ELSE 0 END) AS HasOpening,
               MAX(CASE WHEN Q.SnapshotLabel = 'CLOSING' THEN 1 ELSE 0 END) AS HasClosing
        FROM Q WHERE Q.MatchId = M.MatchId AND Q.MarketCode = '1X2'
    ) o
    GROUP BY M.MatchId, M.Saison, M.MatchDate
)
SELECT * INTO #Couverture FROM Par;

-- 1) Par saison
SELECT Saison, COUNT(*) AS MatchsTermines,
       CAST(100.0 * AVG(1.0 * Dec1X2)  AS DECIMAL(5,1)) AS PctDecision1X2,
       CAST(100.0 * AVG(1.0 * DecBtts) AS DECIMAL(5,1)) AS PctDecisionBtts,
       CAST(100.0 * AVG(1.0 * DecOu25) AS DECIMAL(5,1)) AS PctDecisionPlus25,
       CAST(100.0 * AVG(1.0 * AOpening) AS DECIMAL(5,1)) AS PctOpening,
       CAST(100.0 * AVG(1.0 * AClosing) AS DECIMAL(5,1)) AS PctClosing
FROM #Couverture
GROUP BY Saison
ORDER BY Saison;

-- 2) Saison en cours, semaine par semaine
SELECT DATEADD(DAY, 1 - DATEPART(WEEKDAY, CAST(MatchDate AS DATE)), CAST(MatchDate AS DATE)) AS Semaine,
       COUNT(*) AS MatchsTermines,
       SUM(Dec1X2)  AS AvecDecision1X2,
       SUM(DecOu25) AS AvecDecisionPlus25,
       SUM(AOpening) AS AvecOpening,
       SUM(AClosing) AS AvecClosing
FROM #Couverture
WHERE Saison = (SELECT MAX(Saison) FROM #Couverture)
GROUP BY DATEADD(DAY, 1 - DATEPART(WEEKDAY, CAST(MatchDate AS DATE)), CAST(MatchDate AS DATE))
ORDER BY Semaine;

DROP TABLE #Couverture;

-- 3) Dernières exécutions du scraper
SELECT TOP (30) *
FROM dbo.AppExecutionRuns
ORDER BY StartedAtUtc DESC;
