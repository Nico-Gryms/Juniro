/* =============================================================================
   Juniro_Evo - Trous de données de la saison en cours
   -----------------------------------------------------------------------------
   Liste ce que le rattrapage automatique (job Hangfire « repair-recent-matches », 04h30)
   doit combler, et vérifie qu'il l'a fait. À lancer quand l'application affiche un match
   sans score ou sans statistiques, ou après une nuit où le PC était éteint.

     1. Matchs passés restés « à venir »  (statut non mis à jour après le coup d'envoi)
     2. Matchs terminés incomplets         (score, mi-temps, statistiques ou cotes manquants)
     3. Équipes en retard                  (moins de matchs joués que les autres de leur ligue)
        + bilan de saison par ligue          (premier match en base, journées complètes : à comparer
                                              au calendrier Flashscore — une journée manquante pour
                                              toute la ligue ne crée aucun « retard »)
     4. Dernières exécutions du rattrapage automatique

   Ce qui reste en 1 ou 2 au-delà de @JoursRattrapage jours (Repair:DaysBack, 10 par défaut :
   mettre ici la même valeur) n'est plus retenté automatiquement : relancer
   « Juniro.ScraperService.exe --backfill --from AAAA-MM-JJ --leagues "<Ligue>" ».
   Un match sans statistiques sur Flashscore (rare en 2e division) restera en 2 : c'est normal.
   Lecture seule. Documentation : DOCUMENTATION.md (racine du projet), chapitre 3.3.
   ============================================================================= */
USE [Juniro_Evo];
GO

DECLARE @JoursRattrapage INT = 14;   -- = Repair:DaysBack de appsettings.Production.json
DECLARE @Maintenant DATETIME2(0) = CAST(SYSDATETIMEOFFSET() AT TIME ZONE 'Romance Standard Time' AS DATETIME2(0));
DECLARE @DebutSaison DATE = DATEFROMPARTS(CASE WHEN MONTH(@Maintenant) >= 7 THEN YEAR(@Maintenant)
                                                ELSE YEAR(@Maintenant) - 1 END, 7, 1);

WITH Etat AS (
    SELECT m.MatchId, m.FlashscoreMatchKey, l.LeagueName, m.MatchDate, m.MatchStatus,
           ht.TeamName AS Domicile, awt.TeamName AS Exterieur, m.HomeScoreFT, m.AwayScoreFT,
           CASE WHEN m.HomeScoreFT IS NULL OR m.AwayScoreFT IS NULL THEN 1 ELSE 0 END AS SansScore,
           CASE WHEN m.HomeScoreHT IS NULL OR m.AwayScoreHT IS NULL THEN 1 ELSE 0 END AS SansMiTemps,
           CASE WHEN (SELECT COUNT(*) FROM dbo.MatchTeamStats t
                      WHERE t.MatchId = m.MatchId AND t.ShotsOnTarget IS NOT NULL AND t.Corners IS NOT NULL) < 2
                THEN 1 ELSE 0 END AS SansStats,
           CASE WHEN (SELECT COUNT(*) FROM dbo.MatchTeamStats t
                      WHERE t.MatchId = m.MatchId AND t.ExpectedGoals IS NOT NULL) < 2
                THEN 1 ELSE 0 END AS SansXg,
           CASE WHEN NOT EXISTS (SELECT 1 FROM dbo.Odds o WHERE o.MatchId = m.MatchId
                                 AND o.OddHome IS NOT NULL AND o.OddOv25 IS NOT NULL AND o.OddBTTS IS NOT NULL)
                THEN 1 ELSE 0 END AS SansCotes
    FROM dbo.Matches m
    JOIN dbo.Seasons s  ON s.SeasonId = m.SeasonId
    JOIN dbo.Leagues l  ON l.LeagueId = s.LeagueId
    JOIN dbo.Teams ht   ON ht.TeamId = m.HomeTeamId
    JOIN dbo.Teams awt  ON awt.TeamId = m.AwayTeamId
    WHERE m.MatchDate >= @DebutSaison AND m.MatchDate < DATEADD(HOUR, -3, @Maintenant)
)
SELECT '1-2. Matchs à compléter' AS Bloc, LeagueName AS Ligue,
       FORMAT(MatchDate, 'dd/MM HH:mm') AS CoupDEnvoi, Domicile, Exterieur,
       MatchStatus AS Statut, CONCAT(HomeScoreFT, '-', AwayScoreFT) AS Score,
       CONCAT_WS(', ',
           CASE WHEN MatchStatus NOT IN ('Finished', 'Postponed', 'Cancelled', 'Interrupted') THEN 'statut non mis à jour' END,
           CASE WHEN SansScore = 1 THEN 'score' END,
           CASE WHEN SansMiTemps = 1 THEN 'mi-temps' END,
           CASE WHEN SansStats = 1 THEN 'statistiques' END,
           CASE WHEN SansStats = 0 AND SansXg = 1 THEN 'xG seuls' END,
           CASE WHEN SansCotes = 1 THEN 'cotes' END) AS Manque,
       CASE WHEN MatchDate >= DATEADD(DAY, -@JoursRattrapage, CAST(@Maintenant AS DATE)) THEN 'oui'
            ELSE 'non (trop ancien : --backfill)' END
           AS RepriseAutomatique,
       FlashscoreMatchKey
FROM Etat
WHERE MatchStatus NOT IN ('Postponed', 'Cancelled', 'Interrupted')
  AND (MatchStatus <> 'Finished' OR SansScore = 1 OR SansMiTemps = 1 OR SansStats = 1 OR SansXg = 1 OR SansCotes = 1)
ORDER BY MatchDate DESC, LeagueName;

-- 3. Équipes en retard : 2 matchs ou plus de moins que l'équipe la plus avancée de leur ligue
--    (match jamais enregistré, ou match reporté : vérifier sur Flashscore). Seules les équipes
--    en retard sont listées, avec leurs matchs reportés connus en base.
WITH Joues AS (
    SELECT l.LeagueName, t.TeamName, COUNT(*) AS Joues
    FROM dbo.Matches m
    JOIN dbo.Seasons s ON s.SeasonId = m.SeasonId
    JOIN dbo.Leagues l ON l.LeagueId = s.LeagueId
    CROSS APPLY (VALUES (m.HomeTeamId), (m.AwayTeamId)) x(TeamId)
    JOIN dbo.Teams t ON t.TeamId = x.TeamId
    WHERE m.MatchDate >= @DebutSaison AND m.MatchStatus = 'Finished'
    GROUP BY l.LeagueName, t.TeamName
)
, Retards AS (
    SELECT LeagueName, TeamName, Joues,
           MAX(Joues) OVER (PARTITION BY LeagueName) AS MaxLigue,
           MAX(Joues) OVER (PARTITION BY LeagueName) - Joues AS Retard
    FROM Joues
)
SELECT '3. Équipes en retard' AS Bloc, r.LeagueName AS Ligue, r.TeamName AS Equipe, r.Joues, r.MaxLigue, r.Retard,
       (SELECT STRING_AGG(CONCAT(FORMAT(m.MatchDate, 'dd/MM'), ' ', ht.TeamName, '-', awt.TeamName, ' (', m.MatchStatus, ')'), ' ; ')
        FROM dbo.Matches m
        JOIN dbo.Seasons s ON s.SeasonId = m.SeasonId
        JOIN dbo.Leagues l ON l.LeagueId = s.LeagueId
        JOIN dbo.Teams ht  ON ht.TeamId = m.HomeTeamId
        JOIN dbo.Teams awt ON awt.TeamId = m.AwayTeamId
        WHERE l.LeagueName = r.LeagueName AND m.MatchDate >= @DebutSaison
          AND m.MatchStatus IN ('Postponed', 'Cancelled', 'Interrupted')
          AND r.TeamName IN (ht.TeamName, awt.TeamName)) AS MatchsReportesEnBase
FROM Retards r
WHERE r.Retard >= 2
ORDER BY r.LeagueName, r.Joues;

-- 3b. Bilan de saison par ligue : si le premier match en base est postérieur au début réel du
--     championnat, ou si JourneesCompletes est inférieur au nombre de journées jouées sur
--     Flashscore, des journées entières manquent (le bloc 3 ne peut pas le voir).
WITH Fin AS (
    SELECT l.LeagueName, m.MatchDate, m.HomeTeamId, m.AwayTeamId
    FROM dbo.Matches m
    JOIN dbo.Seasons s ON s.SeasonId = m.SeasonId
    JOIN dbo.Leagues l ON l.LeagueId = s.LeagueId
    WHERE m.MatchDate >= @DebutSaison AND m.MatchStatus = 'Finished'
), Eq AS (
    SELECT LeagueName, COUNT(DISTINCT TeamId) AS Equipes
    FROM Fin CROSS APPLY (VALUES (HomeTeamId), (AwayTeamId)) x(TeamId)
    GROUP BY LeagueName
)
SELECT '3b. Saison par ligue' AS Bloc, f.LeagueName AS Ligue, e.Equipes,
       FORMAT(MIN(f.MatchDate), 'dd/MM/yyyy') AS PremierMatchEnBase,
       FORMAT(MAX(f.MatchDate), 'dd/MM/yyyy') AS DernierMatchTermine,
       COUNT(*) AS MatchsTermines,
       CAST(COUNT(*) * 2.0 / NULLIF(e.Equipes, 0) AS DECIMAL(5, 1)) AS JourneesCompletes
FROM Fin f
JOIN Eq e ON e.LeagueName = f.LeagueName
GROUP BY f.LeagueName, e.Equipes
ORDER BY f.LeagueName;

-- 4. Dernières exécutions du rattrapage automatique (TriggerSource = Repair)
IF OBJECT_ID('dbo.vw_ScraperRuns') IS NOT NULL
    SELECT TOP 10 '4. Rattrapages automatiques' AS Bloc, RunId, DebutParis, Duree, Etat,
           MatchesFound AS ATraiter, MatchesSaved AS Enregistres, MatchesFailed AS Echecs,
           LEFT(Summary, 600) AS Bilan
    FROM dbo.vw_ScraperRuns
    WHERE TriggerSource = 'Repair'
    ORDER BY DebutParis DESC;
