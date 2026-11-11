/* =============================================================================
   Juniro_Evo - Quel relevé de cotes prédit le mieux le résultat ?
   -----------------------------------------------------------------------------
   Pour chaque étiquette (OPENING, J-2, J-1, DAY, H-1, CLOSING), on retire la marge du
   bookmaker sur le 1X2, puis on mesure l'écart entre ces probabilités et le résultat réel :
     - PerteLog : perte logarithmique. Plus elle est BASSE, plus le relevé est précis.
     - Brier    : erreur quadratique. Plus il est BAS, plus le relevé est précis.
     - Marge    : marge moyenne du bookmaker à ce moment.

   Comparaison équitable : seuls les matchs disposant des six relevés sont retenus, pour
   que toutes les étiquettes soient évaluées sur exactement les mêmes matchs.

   ATTENTION : si D3_VerifierInversionCoteNul.sql montre que le nul de l'ancien pipeline
   est inversé, les résultats de OPENING et CLOSING sont faussés. Lancer D3 d'abord.
   Lecture seule.
   Documentation : DOCUMENTATION.md (racine du projet), chapitre 4.4.
   ============================================================================= */
USE [Juniro_Evo];
GO

WITH Releves AS (
    SELECT q.MatchId, q.SnapshotLabel, q.SelectionCode, q.OddValue,
           ROW_NUMBER() OVER (PARTITION BY q.MatchId, q.SnapshotLabel, q.SelectionCode
                              ORDER BY q.QuoteDateUtc DESC, q.OddsQuoteId DESC) AS Rn
    FROM dbo.OddsQuotes q
    WHERE q.MarketCode = '1X2'
      AND q.OddValue > 1
      AND q.SnapshotLabel IN ('OPENING', 'J-2', 'J-1', 'DAY', 'H-1', 'CLOSING')
),
Implicites AS (
    SELECT MatchId, SnapshotLabel,
           MAX(CASE WHEN SelectionCode = 'HOME' THEN 1.0 / OddValue END) AS iH,
           MAX(CASE WHEN SelectionCode = 'DRAW' THEN 1.0 / OddValue END) AS iD,
           MAX(CASE WHEN SelectionCode = 'AWAY' THEN 1.0 / OddValue END) AS iA
    FROM Releves
    WHERE Rn = 1
    GROUP BY MatchId, SnapshotLabel
),
Probas AS (
    SELECT i.MatchId, i.SnapshotLabel,
           i.iH / (i.iH + i.iD + i.iA) AS pH,
           i.iD / (i.iH + i.iD + i.iA) AS pD,
           i.iA / (i.iH + i.iD + i.iA) AS pA,
           i.iH + i.iD + i.iA - 1      AS Marge,
           CASE WHEN m.HomeScoreFT > m.AwayScoreFT THEN 'H'
                WHEN m.HomeScoreFT = m.AwayScoreFT THEN 'D'
                ELSE 'A' END AS Resultat
    FROM Implicites i
    JOIN dbo.Matches m ON m.MatchId = i.MatchId
    WHERE m.MatchStatus = 'Finished'
      AND m.HomeScoreFT IS NOT NULL
      AND i.iH IS NOT NULL AND i.iD IS NOT NULL AND i.iA IS NOT NULL
),
MemesMatchs AS (
    SELECT MatchId FROM Probas
    GROUP BY MatchId
    HAVING COUNT(DISTINCT SnapshotLabel) = 6
)
SELECT p.SnapshotLabel AS Releve,
       COUNT(*) AS Matchs,
       CAST(AVG(-LOG(CASE p.Resultat WHEN 'H' THEN p.pH WHEN 'D' THEN p.pD ELSE p.pA END)) AS DECIMAL(8,5)) AS PerteLog,
       CAST(AVG(  SQUARE(p.pH - CASE WHEN p.Resultat = 'H' THEN 1 ELSE 0 END)
                + SQUARE(p.pD - CASE WHEN p.Resultat = 'D' THEN 1 ELSE 0 END)
                + SQUARE(p.pA - CASE WHEN p.Resultat = 'A' THEN 1 ELSE 0 END)) AS DECIMAL(8,5)) AS Brier,
       CAST(100 * AVG(p.Marge) AS DECIMAL(5,2)) AS MargePct
FROM Probas p
JOIN MemesMatchs mm ON mm.MatchId = p.MatchId
GROUP BY p.SnapshotLabel
ORDER BY CASE p.SnapshotLabel WHEN 'OPENING' THEN 1 WHEN 'J-2' THEN 2 WHEN 'J-1' THEN 3
                              WHEN 'DAY' THEN 4 WHEN 'H-1' THEN 5 ELSE 6 END;
