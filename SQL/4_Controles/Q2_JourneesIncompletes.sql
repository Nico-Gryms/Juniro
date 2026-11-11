/* =============================================================================
   Juniro_Evo - Journées incomplètes de la saison en cours
   -----------------------------------------------------------------------------
   Compare chaque journée à la journée la plus fournie de la même ligue. Toute ligne
   renvoyée est une journée où il manque des matchs : à repasser avec le rattrapage
   (--leagues "<Ligue>"). Une journée en cours ou reportée peut aussi apparaître.
   Lecture seule.
   Documentation : DOCUMENTATION.md (racine du projet), chapitre 4.4.
   ============================================================================= */
USE [Juniro_Evo];
GO

DECLARE @Saison NVARCHAR(20) = N'2026-2027';

WITH ParJournee AS (
    SELECT s.LeagueId, m.RoundNumber, COUNT(*) AS NbMatchs
    FROM dbo.Matches m
    JOIN dbo.Seasons s ON s.SeasonId = m.SeasonId
    WHERE REPLACE(s.SeasonName, '/', '-') = @Saison
    GROUP BY s.LeagueId, m.RoundNumber
),
Reference AS (
    SELECT LeagueId, MAX(NbMatchs) AS NbAttendu
    FROM ParJournee
    GROUP BY LeagueId
)
SELECT l.LeagueName, p.RoundNumber AS Journee, p.NbMatchs, r.NbAttendu,
       r.NbAttendu - p.NbMatchs AS Manquants
FROM ParJournee p
JOIN Reference r ON r.LeagueId = p.LeagueId
JOIN dbo.Leagues l ON l.LeagueId = p.LeagueId
WHERE p.NbMatchs < r.NbAttendu
ORDER BY l.LeagueName, p.RoundNumber;

-- Vue d'ensemble par ligue
SELECT l.LeagueName, COUNT(*) AS Matchs,
       COUNT(DISTINCT m.RoundNumber) AS Journees,
       MIN(m.MatchDate) AS Debut, MAX(m.MatchDate) AS Fin
FROM dbo.Matches m
JOIN dbo.Seasons s ON s.SeasonId = m.SeasonId
JOIN dbo.Leagues l ON l.LeagueId = s.LeagueId
WHERE REPLACE(s.SeasonName, '/', '-') = @Saison
GROUP BY l.LeagueName
ORDER BY l.LeagueName;
