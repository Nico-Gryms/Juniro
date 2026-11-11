/* =============================================================================
   Juniro_Evo - Ligue parasite "championnatdumonde" (LeagueId 11)
   -----------------------------------------------------------------------------
   Pays et niveau NULL, 4 matchs : elle ne fait pas partie des 10 ligues suivies.
   Ces 4 matchs polluent les classements et les moyennes glissantes.

   ÉTAPE 1 : lancer tel quel pour voir de quels matchs il s'agit.
   ÉTAPE 2 : si ce sont bien des matchs hors périmètre, passer @Supprimer à 1.
   Documentation : DOCUMENTATION.md (racine du projet), chapitre 4.4.
   ============================================================================= */
USE [Juniro_Evo];
GO

DECLARE @Supprimer BIT = 0;   -- 0 = inspection, 1 = suppression
DECLARE @LeagueId  INT = 11;

-- Quels matchs sont concernés ?
SELECT 'Matchs de la ligue parasite' AS Bloc,
       m.MatchId, m.FlashscoreMatchKey, m.MatchDate, m.MatchStatus,
       ht.TeamName AS Domicile, at.TeamName AS Exterieur,
       m.HomeScoreFT, m.AwayScoreFT, s.SeasonName
FROM dbo.Matches m
JOIN dbo.Seasons s  ON s.SeasonId = m.SeasonId
JOIN dbo.Teams   ht ON ht.TeamId = m.HomeTeamId
JOIN dbo.Teams   at ON at.TeamId = m.AwayTeamId
WHERE s.LeagueId = @LeagueId
ORDER BY m.MatchDate;

-- Ces équipes existent-elles ailleurs ? (ne jamais supprimer une équipe utilisée)
SELECT 'Équipes impliquées' AS Bloc,
       t.TeamId, t.TeamName, t.CountryName,
       (SELECT COUNT(*) FROM dbo.Matches m2
        WHERE m2.HomeTeamId = t.TeamId OR m2.AwayTeamId = t.TeamId) AS NbMatchsTotal
FROM dbo.Teams t
WHERE t.TeamId IN (
    SELECT m.HomeTeamId FROM dbo.Matches m JOIN dbo.Seasons s ON s.SeasonId = m.SeasonId WHERE s.LeagueId = @LeagueId
    UNION
    SELECT m.AwayTeamId FROM dbo.Matches m JOIN dbo.Seasons s ON s.SeasonId = m.SeasonId WHERE s.LeagueId = @LeagueId
);

IF @Supprimer = 1
BEGIN
    BEGIN TRANSACTION;

    DECLARE @Ids TABLE (MatchId BIGINT PRIMARY KEY);
    INSERT INTO @Ids (MatchId)
    SELECT m.MatchId FROM dbo.Matches m
    JOIN dbo.Seasons s ON s.SeasonId = m.SeasonId
    WHERE s.LeagueId = @LeagueId;

    -- Dans l'ordre des dépendances
    DELETE q FROM dbo.OddsQuotes     q JOIN @Ids i ON i.MatchId = q.MatchId;
    DELETE o FROM dbo.Odds           o JOIN @Ids i ON i.MatchId = o.MatchId;
    DELETE t FROM dbo.MatchTeamStats t JOIN @Ids i ON i.MatchId = t.MatchId;
    DELETE m FROM dbo.Matches        m JOIN @Ids i ON i.MatchId = m.MatchId;

    DELETE FROM dbo.Seasons WHERE LeagueId = @LeagueId;
    DELETE FROM dbo.Leagues WHERE LeagueId = @LeagueId;

    COMMIT TRANSACTION;
    SELECT 'Ligue parasite supprimée' AS Statut;
END
ELSE
BEGIN
    SELECT 'INSPECTION SEULEMENT - passez @Supprimer à 1 pour supprimer' AS Statut;
END;
