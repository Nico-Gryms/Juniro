/* =============================================================================
   Juniro_Evo - Contrôle avant backfill
   À exécuter dans SSMS sur Juniro_Evo. Lecture seule, aucune modification.
   Renvoyer les résultats des 6 blocs.
   Documentation : DOCUMENTATION.md (racine du projet), chapitre 4.4.
   ============================================================================= */

/* Exécution forcée sur la bonne base : sans cela, SSMS reste sur master
   et toutes les requêtes échouent avec « Nom d'objet non valide ». */
USE [Juniro_Evo];
GO


-- -----------------------------------------------------------------------------
-- BLOC 0 : où sommes-nous, et quelles tables existent réellement ?
-- -----------------------------------------------------------------------------
SELECT '0. Base courante' AS Bloc, DB_NAME() AS BaseCourante, SUSER_SNAME() AS Connecte;

SELECT '0b. Tables' AS Bloc, s.name AS SchemaName, t.name AS TableName,
       SUM(p.rows) AS Lignes
FROM sys.tables t
JOIN sys.schemas s   ON s.schema_id = t.schema_id
JOIN sys.partitions p ON p.object_id = t.object_id AND p.index_id IN (0, 1)
GROUP BY s.name, t.name
ORDER BY s.name, t.name;

-- -----------------------------------------------------------------------------
-- BLOC 1 : la colonne Odds.OddsQuoteId existe-t-elle ?
-- InsertOddsSnapshotAsync fait "ORDER BY [OddsQuoteId]" sur la table [Odds].
-- Si cette colonne appartient seulement à [OddsQuotes], toute insertion de cote
-- échoue, et la transaction annule AUSSI le match et ses statistiques.
-- -----------------------------------------------------------------------------
SELECT '1. Colonnes de Odds' AS Bloc, c.name AS Colonne, t.name AS Type, c.is_nullable
FROM sys.columns c
JOIN sys.types t ON t.user_type_id = c.user_type_id
WHERE c.object_id = OBJECT_ID('dbo.Odds')
ORDER BY c.column_id;

-- -----------------------------------------------------------------------------
-- BLOC 2 : une contrainte bloque-t-elle les nouveaux statuts ?
-- Le code écrit désormais Cancelled, Abandoned, Live et Unknown en plus de
-- Finished, Scheduled et Postponed. Une contrainte CHECK, ou une colonne trop
-- courte, ferait échouer l'insertion.
-- -----------------------------------------------------------------------------
SELECT '2. Contraintes MatchStatus' AS Bloc, cc.name AS Contrainte, cc.definition
FROM sys.check_constraints cc
WHERE cc.parent_object_id = OBJECT_ID('dbo.Matches');

SELECT '2b. Colonne MatchStatus' AS Bloc, c.max_length AS LongueurMax, t.name AS Type
FROM sys.columns c
JOIN sys.types t ON t.user_type_id = c.user_type_id
WHERE c.object_id = OBJECT_ID('dbo.Matches') AND c.name = 'MatchStatus';

-- -----------------------------------------------------------------------------
-- BLOC 3 : quels statuts existent aujourd'hui ?
-- -----------------------------------------------------------------------------
SELECT '3. Statuts en base' AS Bloc, m.MatchStatus, COUNT(*) AS Nb,
       MIN(m.MatchDate) AS PlusAncien, MAX(m.MatchDate) AS PlusRecent
FROM dbo.Matches m
GROUP BY m.MatchStatus
ORDER BY Nb DESC;

-- -----------------------------------------------------------------------------
-- BLOC 4 : LE CONTRÔLE CLÉ - les statistiques sont-elles réellement remplies ?
-- Un taux de NULL à 100 % sur une colonne signifie que le libellé Flashscore
-- correspondant n'est pas reconnu par MapStatRow : la donnée est perdue
-- silencieusement depuis le début.
-- -----------------------------------------------------------------------------
SELECT '4. Remplissage des stats' AS Bloc,
       COUNT(*)                                                      AS LignesStats,
       CAST(100.0 * SUM(CASE WHEN ExpectedGoals  IS NULL THEN 1 ELSE 0 END) / NULLIF(COUNT(*),0) AS DECIMAL(5,1)) AS PctNull_xG,
       CAST(100.0 * SUM(CASE WHEN PossessionPct  IS NULL THEN 1 ELSE 0 END) / NULLIF(COUNT(*),0) AS DECIMAL(5,1)) AS PctNull_Possession,
       CAST(100.0 * SUM(CASE WHEN Shots          IS NULL THEN 1 ELSE 0 END) / NULLIF(COUNT(*),0) AS DECIMAL(5,1)) AS PctNull_Tirs,
       CAST(100.0 * SUM(CASE WHEN ShotsOnTarget  IS NULL THEN 1 ELSE 0 END) / NULLIF(COUNT(*),0) AS DECIMAL(5,1)) AS PctNull_TirsCadres,
       CAST(100.0 * SUM(CASE WHEN Corners        IS NULL THEN 1 ELSE 0 END) / NULLIF(COUNT(*),0) AS DECIMAL(5,1)) AS PctNull_Corners,
       CAST(100.0 * SUM(CASE WHEN Fouls          IS NULL THEN 1 ELSE 0 END) / NULLIF(COUNT(*),0) AS DECIMAL(5,1)) AS PctNull_Fautes,
       CAST(100.0 * SUM(CASE WHEN YellowCards    IS NULL THEN 1 ELSE 0 END) / NULLIF(COUNT(*),0) AS DECIMAL(5,1)) AS PctNull_Jaunes
FROM dbo.MatchTeamStats;

-- Même contrôle par ligue : le xG manque souvent en 2e division.
SELECT '4b. Stats par ligue' AS Bloc, l.LeagueName,
       COUNT(*) AS LignesStats,
       CAST(100.0 * SUM(CASE WHEN s.ExpectedGoals IS NULL THEN 1 ELSE 0 END) / NULLIF(COUNT(*),0) AS DECIMAL(5,1)) AS PctNull_xG,
       CAST(100.0 * SUM(CASE WHEN s.Shots         IS NULL THEN 1 ELSE 0 END) / NULLIF(COUNT(*),0) AS DECIMAL(5,1)) AS PctNull_Tirs,
       CAST(100.0 * SUM(CASE WHEN s.Corners       IS NULL THEN 1 ELSE 0 END) / NULLIF(COUNT(*),0) AS DECIMAL(5,1)) AS PctNull_Corners
FROM dbo.MatchTeamStats s
JOIN dbo.Matches m ON m.MatchId = s.MatchId
JOIN dbo.Seasons q ON q.SeasonId = m.SeasonId
JOIN dbo.Leagues l ON l.LeagueId = q.LeagueId
GROUP BY l.LeagueName
ORDER BY l.LeagueName;

-- -----------------------------------------------------------------------------
-- BLOC 5 : les cotes sont-elles réellement remplies, marché par marché ?
-- Un marché à 100 % de NULL signale un onglet Flashscore jamais lu.
-- -----------------------------------------------------------------------------
SELECT '5. Remplissage des cotes' AS Bloc,
       COUNT(*) AS LignesOdds,
       CAST(100.0 * SUM(CASE WHEN OddHome     IS NULL THEN 1 ELSE 0 END) / NULLIF(COUNT(*),0) AS DECIMAL(5,1)) AS PctNull_1X2,
       CAST(100.0 * SUM(CASE WHEN OddOv25     IS NULL THEN 1 ELSE 0 END) / NULLIF(COUNT(*),0) AS DECIMAL(5,1)) AS PctNull_OU25,
       CAST(100.0 * SUM(CASE WHEN OddOv15     IS NULL THEN 1 ELSE 0 END) / NULLIF(COUNT(*),0) AS DECIMAL(5,1)) AS PctNull_OU15,
       CAST(100.0 * SUM(CASE WHEN OddOv35     IS NULL THEN 1 ELSE 0 END) / NULLIF(COUNT(*),0) AS DECIMAL(5,1)) AS PctNull_OU35,
       CAST(100.0 * SUM(CASE WHEN OddBTTS     IS NULL THEN 1 ELSE 0 END) / NULLIF(COUNT(*),0) AS DECIMAL(5,1)) AS PctNull_BTTS,
       CAST(100.0 * SUM(CASE WHEN OddHomeDraw IS NULL THEN 1 ELSE 0 END) / NULLIF(COUNT(*),0) AS DECIMAL(5,1)) AS PctNull_DoubleChance,
       CAST(100.0 * SUM(CASE WHEN OddHomeDNB  IS NULL THEN 1 ELSE 0 END) / NULLIF(COUNT(*),0) AS DECIMAL(5,1)) AS PctNull_DNB
FROM dbo.Odds;

SELECT '5b. OddsQuotes par marché' AS Bloc, MarketCode, SelectionCode, LineValue,
       COUNT(*) AS Nb,
       SUM(CASE WHEN SnapshotLabel = 'PreMatch'  THEN 1 ELSE 0 END) AS PreMatch,
       SUM(CASE WHEN SnapshotLabel = 'PostMatch' THEN 1 ELSE 0 END) AS PostMatch
FROM dbo.OddsQuotes
GROUP BY MarketCode, SelectionCode, LineValue
ORDER BY MarketCode, SelectionCode, LineValue;

-- -----------------------------------------------------------------------------
-- BLOC 6 : combien de matchs terminés sont incomplets ?
-- C'est la mesure du trou réel : terminés sans stats, ou sans cotes.
-- -----------------------------------------------------------------------------
SELECT '6. Complétude des matchs terminés' AS Bloc,
       COUNT(*) AS TerminesTotal,
       SUM(CASE WHEN NbStats = 2 THEN 1 ELSE 0 END) AS AvecStatsCompletes,
       SUM(CASE WHEN NbStats = 0 THEN 1 ELSE 0 END) AS SansAucuneStat,
       SUM(CASE WHEN NbOdds > 0 THEN 1 ELSE 0 END) AS AvecCotes,
       SUM(CASE WHEN NbOdds = 0 THEN 1 ELSE 0 END) AS SansCote
FROM (
    SELECT m.MatchId,
           (SELECT COUNT(*) FROM dbo.MatchTeamStats s WHERE s.MatchId = m.MatchId) AS NbStats,
           (SELECT COUNT(*) FROM dbo.Odds o          WHERE o.MatchId = m.MatchId) AS NbOdds
    FROM dbo.Matches m
    WHERE m.MatchStatus = 'Finished'
) x;
