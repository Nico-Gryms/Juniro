/* =============================================================================
   Juniro_Evo - Les cotes du MATCH NUL de l'ancien pipeline sont-elles inversées ?
   -----------------------------------------------------------------------------
   Dans l'ancienne application, l'attribut title « ouverture » actuelle » est lu ainsi :
       Domicile :  Init = [0]  Final = [1]
       Nul      :  Init = [1]  Final = [0]     <-- indices inversés
       Extérieur:  Init = [0]  Final = [1]
   Si c'est un bug, l'historique du nul porte l'ouverture à la place de la clôture,
   et inversement. Lecture seule : ce script ne modifie rien.
   Documentation : DOCUMENTATION.md (racine du projet), chapitre 4.4.
   ============================================================================= */
USE [Juniro_Evo];
GO

-- -----------------------------------------------------------------------------
-- TEST 1 : cohérence de la marge.
-- Trois cotes relevées au même instant forment une marge stable d'un match à l'autre.
-- Mélanger un nul d'un autre instant ajoute du bruit et augmente l'écart-type.
-- => Si les colonnes « NulInverse » ont un écart-type nettement PLUS FAIBLE que
--    « TelQuel », le nul de l'historique est inversé.
-- -----------------------------------------------------------------------------
WITH P AS (
    SELECT q.MatchId,
        MAX(CASE WHEN q.SelectionCode = 'HOME' AND q.SnapshotLabel = 'OPENING' THEN q.OddValue END) AS Ho,
        MAX(CASE WHEN q.SelectionCode = 'DRAW' AND q.SnapshotLabel = 'OPENING' THEN q.OddValue END) AS Do,
        MAX(CASE WHEN q.SelectionCode = 'AWAY' AND q.SnapshotLabel = 'OPENING' THEN q.OddValue END) AS Ao,
        MAX(CASE WHEN q.SelectionCode = 'HOME' AND q.SnapshotLabel = 'CLOSING' THEN q.OddValue END) AS Hc,
        MAX(CASE WHEN q.SelectionCode = 'DRAW' AND q.SnapshotLabel = 'CLOSING' THEN q.OddValue END) AS Dc,
        MAX(CASE WHEN q.SelectionCode = 'AWAY' AND q.SnapshotLabel = 'CLOSING' THEN q.OddValue END) AS Ac
    FROM dbo.OddsQuotes q
    WHERE q.MarketCode = '1X2' AND q.QuoteDateUtc < '2026-10-01'
    GROUP BY q.MatchId
)
SELECT 'Test 1 - écart-type de la marge' AS Test,
       COUNT(*) AS Matchs,
       CAST(STDEV(1.0/Ho + 1.0/Do + 1.0/Ao) AS DECIMAL(8,5)) AS Ouverture_TelQuel,
       CAST(STDEV(1.0/Ho + 1.0/Dc + 1.0/Ao) AS DECIMAL(8,5)) AS Ouverture_NulInverse,
       CAST(STDEV(1.0/Hc + 1.0/Dc + 1.0/Ac) AS DECIMAL(8,5)) AS Cloture_TelQuel,
       CAST(STDEV(1.0/Hc + 1.0/Do + 1.0/Ac) AS DECIMAL(8,5)) AS Cloture_NulInverse
FROM P
WHERE Ho > 1 AND Do > 1 AND Ao > 1 AND Hc > 1 AND Dc > 1 AND Ac > 1
  AND Do <> Dc;   -- seuls les matchs où le nul a bougé sont informatifs

-- -----------------------------------------------------------------------------
-- TEST 2 : cote « figée ».
-- Si chaque relevé J-2, J-1, DAY… recevait l'ouverture au lieu de la cote du moment,
-- la cote du nul ne bouge jamais d'un relevé à l'autre, alors que domicile et extérieur bougent.
-- => Un pourcentage de cotes figées très supérieur pour DRAW confirme le bug.
-- -----------------------------------------------------------------------------
WITH S AS (
    SELECT q.MatchId, q.SelectionCode,
           COUNT(DISTINCT q.OddValue) AS NbValeurs
    FROM dbo.OddsQuotes q
    WHERE q.MarketCode = '1X2'
      AND q.QuoteDateUtc < '2026-10-01'
      AND q.SnapshotLabel <> 'OPENING'
    GROUP BY q.MatchId, q.SelectionCode
    HAVING COUNT(*) >= 3
)
SELECT 'Test 2 - cote figée' AS Test, SelectionCode,
       COUNT(*) AS Matchs,
       CAST(100.0 * SUM(CASE WHEN NbValeurs = 1 THEN 1 ELSE 0 END) / COUNT(*) AS DECIMAL(5,1)) AS PctCoteFigee
FROM S
GROUP BY SelectionCode
ORDER BY SelectionCode;

-- -----------------------------------------------------------------------------
-- TEST 3 : contrôle visuel sur cinq matchs récents de l'historique.
-- Comparez avec l'infobulle d'une cote sur Flashscore (survol de la cellule).
-- -----------------------------------------------------------------------------
SELECT TOP 45 'Test 3 - échantillon' AS Test, q.MatchId, m.MatchDate,
       q.SelectionCode, q.SnapshotLabel, q.OddValue, q.QuoteDateUtc
FROM dbo.OddsQuotes q
JOIN dbo.Matches m ON m.MatchId = q.MatchId
WHERE q.MarketCode = '1X2'
  AND q.MatchId IN (SELECT TOP 5 m2.MatchId FROM dbo.Matches m2
                    WHERE m2.MatchStatus = 'Finished' AND m2.MatchDate < '2026-06-01'
                    ORDER BY m2.MatchDate DESC)
ORDER BY q.MatchId, q.SelectionCode, q.QuoteDateUtc;
