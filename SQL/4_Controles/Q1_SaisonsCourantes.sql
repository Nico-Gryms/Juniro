/* =============================================================================
   Juniro_Evo - Saisons : une seule saison courante par ligue, matchs bien rattachés
   Lecture seule.
   Documentation : DOCUMENTATION.md (racine du projet), chapitre 4.4.
   ============================================================================= */
USE [Juniro_Evo];
GO

-- Saisons marquées courantes : une par ligue, la saison 2026/2027.
SELECT l.LeagueName, s.SeasonName, s.IsCurrent, s.StartDate, s.EndDate
FROM dbo.Seasons s
JOIN dbo.Leagues l ON l.LeagueId = s.LeagueId
WHERE s.IsCurrent = 1
ORDER BY l.LeagueName;

-- Matchs rattachés à une saison incohérente avec leur date (bascule au 1er juillet).
-- Doit ne rien renvoyer.
SELECT l.LeagueName, s.SeasonName, COUNT(*) AS NbMatchs,
       MIN(m.MatchDate) AS PlusAncien, MAX(m.MatchDate) AS PlusRecent
FROM dbo.Matches m
JOIN dbo.Seasons s ON s.SeasonId = m.SeasonId
JOIN dbo.Leagues l ON l.LeagueId = s.LeagueId
WHERE REPLACE(s.SeasonName, '-', '/') <> CONCAT(
        CASE WHEN MONTH(m.MatchDate) >= 7 THEN YEAR(m.MatchDate) ELSE YEAR(m.MatchDate) - 1 END, '/',
        CASE WHEN MONTH(m.MatchDate) >= 7 THEN YEAR(m.MatchDate) + 1 ELSE YEAR(m.MatchDate) END)
GROUP BY l.LeagueName, s.SeasonName
ORDER BY NbMatchs DESC;
