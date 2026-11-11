/* =============================================================================
   Juniro_Evo - Cotes de la saison en cours : sont-elles bien d'avant-match ?
   -----------------------------------------------------------------------------
   Le backtest donne au marché une perte logarithmique de 0,74 sur le 1X2 de la saison
   2026-2027. Un vrai relevé d'avant-match est autour de 1,00 : 0,74 n'est possible que si
   certaines cotes ont été relevées PENDANT ou APRÈS le match.

   1) Par étiquette : nombre de relevés, part relevée après le coup d'envoi, écart en minutes.
   2) Par étiquette : précision du 1X2 sans marge (comme D4) sur cette saison.
   3) Exemples de relevés étiquetés « avant-match » mais datés après le coup d'envoi.
   Lecture seule.
   Documentation : DOCUMENTATION.md (racine du projet), chapitre 4.4.
   ============================================================================= */
USE [Juniro_Evo];
GO

DECLARE @Saison NVARCHAR(20) = N'2026-2027';

-- 1) Horodatage des relevés par rapport au coup d'envoi (MatchDate est en heure de Paris)
;WITH Q AS (
    SELECT q.SnapshotLabel, q.MarketCode, q.QuoteDateUtc,
           CAST((m.MatchDate AT TIME ZONE 'Romance Standard Time') AT TIME ZONE 'UTC' AS DATETIME2(0)) AS KickoffUtc
    FROM dbo.OddsQuotes q
    JOIN dbo.Matches m ON m.MatchId = q.MatchId
    JOIN dbo.Seasons s ON s.SeasonId = m.SeasonId
    WHERE REPLACE(s.SeasonName, '/', '-') = @Saison
      AND m.MatchStatus = 'Finished'
)
SELECT SnapshotLabel, MarketCode, COUNT(*) AS Releves,
       SUM(CASE WHEN QuoteDateUtc >= KickoffUtc THEN 1 ELSE 0 END) AS ApresCoupEnvoi,
       CAST(100.0 * AVG(CASE WHEN QuoteDateUtc >= KickoffUtc THEN 1.0 ELSE 0 END) AS DECIMAL(5,1)) AS PctApres,
       MIN(DATEDIFF(MINUTE, KickoffUtc, QuoteDateUtc)) AS MinutesMin,
       MAX(DATEDIFF(MINUTE, KickoffUtc, QuoteDateUtc)) AS MinutesMax
FROM Q
GROUP BY SnapshotLabel, MarketCode
ORDER BY MarketCode, SnapshotLabel;

-- 2) Précision du 1X2 par étiquette, sur cette saison
;WITH R AS (
    SELECT q.MatchId, q.SnapshotLabel, q.SelectionCode, q.OddValue,
           ROW_NUMBER() OVER (PARTITION BY q.MatchId, q.SnapshotLabel, q.SelectionCode
                              ORDER BY q.QuoteDateUtc DESC, q.OddsQuoteId DESC) AS Rn
    FROM dbo.OddsQuotes q
    JOIN dbo.Matches m ON m.MatchId = q.MatchId
    JOIN dbo.Seasons s ON s.SeasonId = m.SeasonId
    WHERE REPLACE(s.SeasonName, '/', '-') = @Saison AND q.MarketCode = '1X2' AND q.OddValue > 1
),
Impl AS (
    SELECT MatchId, SnapshotLabel,
           MAX(CASE WHEN SelectionCode = 'HOME' THEN 1.0 / OddValue END) AS iH,
           MAX(CASE WHEN SelectionCode = 'DRAW' THEN 1.0 / OddValue END) AS iD,
           MAX(CASE WHEN SelectionCode = 'AWAY' THEN 1.0 / OddValue END) AS iA
    FROM R WHERE Rn = 1 GROUP BY MatchId, SnapshotLabel
)
SELECT i.SnapshotLabel, COUNT(*) AS Matchs,
       CAST(AVG(-LOG(CASE WHEN m.HomeScoreFT > m.AwayScoreFT THEN i.iH
                          WHEN m.HomeScoreFT = m.AwayScoreFT THEN i.iD ELSE i.iA END
                     / (i.iH + i.iD + i.iA))) AS DECIMAL(8,5)) AS PerteLog,
       CAST(100 * AVG(i.iH + i.iD + i.iA - 1) AS DECIMAL(5,2)) AS MargePct
FROM Impl i
JOIN dbo.Matches m ON m.MatchId = i.MatchId
WHERE m.MatchStatus = 'Finished' AND i.iH IS NOT NULL AND i.iD IS NOT NULL AND i.iA IS NOT NULL
GROUP BY i.SnapshotLabel
ORDER BY PerteLog;

-- 3) Exemples suspects : étiquette d'avant-match mais relevé après le coup d'envoi
SELECT TOP (30) m.MatchId, m.MatchDate AS CoupEnvoiParis, q.QuoteDateUtc, q.SnapshotLabel,
       q.MarketCode, q.SelectionCode, q.OddValue, m.HomeScoreFT, m.AwayScoreFT
FROM dbo.OddsQuotes q
JOIN dbo.Matches m ON m.MatchId = q.MatchId
JOIN dbo.Seasons s ON s.SeasonId = m.SeasonId
WHERE REPLACE(s.SeasonName, '/', '-') = @Saison
  AND q.SnapshotLabel IN ('OPENING', 'J-3', 'J-2', 'J-1', 'DAY', 'H-1')
  AND q.QuoteDateUtc >= CAST((m.MatchDate AT TIME ZONE 'Romance Standard Time') AT TIME ZONE 'UTC' AS DATETIME2(0))
ORDER BY q.QuoteDateUtc DESC;
