/* =============================================================================
   Juniro_Evo - Relevés de cotes de la dernière heure
   -----------------------------------------------------------------------------
   À lancer juste après un test du rattrapage. Pour chaque match terminé, on doit voir
   par sélection une ligne OPENING datée AVANT une ligne CLOSING (ou une seule ligne si
   la cote n'a pas bougé). Pour le service journalier : J-3, J-2, J-1, DAY ou H-1.
   Lecture seule.
   Documentation : DOCUMENTATION.md (racine du projet), chapitre 4.4.
   ============================================================================= */
USE [Juniro_Evo];
GO

DECLARE @Depuis DATETIME2 = DATEADD(HOUR, -1, SYSUTCDATETIME());

SELECT q.MatchId, q.MarketCode, q.SelectionCode, q.LineValue,
       q.SnapshotLabel, q.OddValue, q.QuoteDateUtc, q.IsOpening, q.IsClosing
FROM dbo.OddsQuotes q
WHERE q.CreatedAt >= @Depuis
ORDER BY q.MatchId, q.MarketCode, q.SelectionCode, q.LineValue, q.QuoteDateUtc;

-- Synthèse : marchés récupérés par match
SELECT q.MatchId,
       SUM(CASE WHEN q.MarketCode = '1X2'           THEN 1 ELSE 0 END) AS Lignes1X2,
       SUM(CASE WHEN q.MarketCode = 'OVER_UNDER'    THEN 1 ELSE 0 END) AS LignesPlusMoins,
       SUM(CASE WHEN q.MarketCode = 'BTTS'          THEN 1 ELSE 0 END) AS LignesBTTS,
       SUM(CASE WHEN q.MarketCode = 'DOUBLE_CHANCE' THEN 1 ELSE 0 END) AS LignesDoubleChance,
       SUM(CASE WHEN q.MarketCode = 'DNB'           THEN 1 ELSE 0 END) AS LignesDNB,
       SUM(CASE WHEN q.SnapshotLabel = 'OPENING'    THEN 1 ELSE 0 END) AS Ouvertures
FROM dbo.OddsQuotes q
WHERE q.CreatedAt >= @Depuis
GROUP BY q.MatchId
ORDER BY q.MatchId;
