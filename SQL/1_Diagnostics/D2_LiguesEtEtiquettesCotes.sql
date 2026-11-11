/* =============================================================================
   Juniro_Evo - Deux points soulevés par le contrôle
   Lecture seule.
   Documentation : DOCUMENTATION.md (racine du projet), chapitre 4.4.
   ============================================================================= */
USE [Juniro_Evo];
GO

-- -----------------------------------------------------------------------------
-- A. La table Leagues contient 11 lignes pour 10 ligues suivies.
--    Un doublon éclaterait les matchs d'une même ligue sur deux LeagueId, ce qui
--    fausserait classements, moyennes glissantes et force d'attaque.
-- -----------------------------------------------------------------------------
SELECT 'A. Ligues' AS Bloc,
       l.LeagueId, l.LeagueName, l.CountryName, l.TierLevel,
       (SELECT COUNT(*) FROM dbo.Seasons s WHERE s.LeagueId = l.LeagueId) AS NbSaisons,
       (SELECT COUNT(*) FROM dbo.Matches m
        JOIN dbo.Seasons s ON s.SeasonId = m.SeasonId
        WHERE s.LeagueId = l.LeagueId) AS NbMatchs
FROM dbo.Leagues l
ORDER BY l.CountryName, l.TierLevel, l.LeagueName;

-- Saisons rattachées, pour repérer un doublon créé à une date précise
SELECT 'A2. Saisons' AS Bloc,
       l.LeagueId, l.LeagueName, s.SeasonId, s.SeasonName, s.IsCurrent, s.CreatedAt,
       (SELECT COUNT(*) FROM dbo.Matches m WHERE m.SeasonId = s.SeasonId) AS NbMatchs
FROM dbo.Seasons s
JOIN dbo.Leagues l ON l.LeagueId = s.LeagueId
ORDER BY l.LeagueName, s.SeasonName;

-- -----------------------------------------------------------------------------
-- B. SnapshotLabel : sur ~67 000 lignes d'OddsQuotes, seules ~150 portent
--    PreMatch ou PostMatch. Il faut savoir ce que contiennent les autres avant
--    de s'appuyer sur cette colonne pour entraîner un modèle.
-- -----------------------------------------------------------------------------
SELECT 'B. SnapshotLabel' AS Bloc,
       ISNULL(SnapshotLabel, '(NULL)') AS SnapshotLabel,
       COUNT(*) AS Nb,
       MIN(QuoteDateUtc) AS PlusAncien,
       MAX(QuoteDateUtc) AS PlusRecent
FROM dbo.OddsQuotes
GROUP BY SnapshotLabel
ORDER BY Nb DESC;

-- Combien de relevés par match : une seule cote par match signifie qu'il n'y a
-- pas d'historique d'évolution exploitable sur les données anciennes.
SELECT 'B2. Relevés par match' AS Bloc,
       NbReleves, COUNT(*) AS NbMatchs
FROM (
    SELECT q.MatchId, COUNT(*) AS NbReleves
    FROM dbo.OddsQuotes q
    WHERE q.MarketCode = '1X2' AND q.SelectionCode = 'HOME'
    GROUP BY q.MatchId
) x
GROUP BY NbReleves
ORDER BY NbReleves;

-- Idem côté table Odds (OddIndex = numéro de relevé)
SELECT 'B3. Relevés Odds par match' AS Bloc,
       NbReleves, COUNT(*) AS NbMatchs
FROM (
    SELECT o.MatchId, COUNT(*) AS NbReleves
    FROM dbo.Odds o
    GROUP BY o.MatchId
) y
GROUP BY NbReleves
ORDER BY NbReleves;
