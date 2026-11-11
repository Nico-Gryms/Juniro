/* =============================================================================
   Juniro_Evo - Surveillance de la qualité de collecte
   -----------------------------------------------------------------------------
   Filet de sécurité indépendant de l'application : même si le contrôle qualité
   intégré au scraper venait à échouer, ces requêtes révèlent la dégradation en
   interrogeant les données elles-mêmes.

   À regarder une fois par semaine, ou dès qu'un doute apparaît.
   ============================================================================= */
USE [Juniro_Evo];
GO

-- -----------------------------------------------------------------------------
-- 1. ALERTES DU SCRAPER sur les 30 derniers jours
--    Le scraper écrit lui-même son contrôle qualité en fin d'exécution.
-- -----------------------------------------------------------------------------
SELECT TOP 50 '1. Alertes scraper' AS Bloc,
       l.CreatedAtUtc, l.Level, l.Sector, l.MatchIdent,
       LEFT(l.Message, 400) AS Message
FROM dbo.AppExecutionLogs l
WHERE l.CreatedAtUtc >= DATEADD(DAY, -30, SYSUTCDATETIME())
  AND (l.Level IN ('ERROR', 'FATAL', 'WARN') OR l.Sector = 'Scraper.Quality')
ORDER BY l.CreatedAtUtc DESC;

-- -----------------------------------------------------------------------------
-- 2. TAUX DE REMPLISSAGE SEMAINE PAR SEMAINE
--    C'est LA requête qui révèle une panne silencieuse : une colonne qui tombe
--    brutalement de 99 % à 0 % une semaine donnée signale un changement de page
--    chez Flashscore. Une baisse progressive signale autre chose (couverture).
-- -----------------------------------------------------------------------------
WITH Base AS (
    SELECT DATEADD(DAY, -(DATEPART(WEEKDAY, m.MatchDate) - 2 + 7) % 7, CAST(m.MatchDate AS DATE)) AS Semaine,
           m.MatchId,
           (SELECT COUNT(*) FROM dbo.MatchTeamStats s WHERE s.MatchId = m.MatchId) AS NbStats,
           (SELECT COUNT(*) FROM dbo.MatchTeamStats s WHERE s.MatchId = m.MatchId AND s.ExpectedGoals  IS NOT NULL) AS NbXg,
           (SELECT COUNT(*) FROM dbo.MatchTeamStats s WHERE s.MatchId = m.MatchId AND s.Shots          IS NOT NULL) AS NbTirs,
           (SELECT COUNT(*) FROM dbo.MatchTeamStats s WHERE s.MatchId = m.MatchId AND s.Corners        IS NOT NULL) AS NbCorners,
           (SELECT COUNT(*) FROM dbo.Odds o          WHERE o.MatchId = m.MatchId AND o.OddHome        IS NOT NULL) AS NbCotes1X2,
           (SELECT COUNT(*) FROM dbo.Odds o          WHERE o.MatchId = m.MatchId AND o.OddOv25        IS NOT NULL) AS NbCotesOU
    FROM dbo.Matches m
    WHERE m.MatchStatus = 'Finished'
      AND m.MatchDate >= DATEADD(MONTH, -6, GETDATE())
)
SELECT '2. Remplissage hebdomadaire' AS Bloc,
       Semaine,
       COUNT(*) AS Matchs,
       CAST(100.0 * SUM(CASE WHEN NbStats    = 2 THEN 1 ELSE 0 END) / COUNT(*) AS DECIMAL(5,1)) AS Pct_Stats,
       CAST(100.0 * SUM(CASE WHEN NbXg       = 2 THEN 1 ELSE 0 END) / COUNT(*) AS DECIMAL(5,1)) AS Pct_xG,
       CAST(100.0 * SUM(CASE WHEN NbTirs     = 2 THEN 1 ELSE 0 END) / COUNT(*) AS DECIMAL(5,1)) AS Pct_Tirs,
       CAST(100.0 * SUM(CASE WHEN NbCorners  = 2 THEN 1 ELSE 0 END) / COUNT(*) AS DECIMAL(5,1)) AS Pct_Corners,
       CAST(100.0 * SUM(CASE WHEN NbCotes1X2 > 0 THEN 1 ELSE 0 END) / COUNT(*) AS DECIMAL(5,1)) AS Pct_Cote1X2,
       CAST(100.0 * SUM(CASE WHEN NbCotesOU  > 0 THEN 1 ELSE 0 END) / COUNT(*) AS DECIMAL(5,1)) AS Pct_CoteOU
FROM Base
GROUP BY Semaine
ORDER BY Semaine DESC;

-- -----------------------------------------------------------------------------
-- 3. MATCHS TERMINÉS DEPUIS PLUS DE 48 H ET TOUJOURS INCOMPLETS
--    Liste de travail directe : ces matchs sont à repasser au backfill.
-- -----------------------------------------------------------------------------
SELECT '3. À retraiter' AS Bloc,
       l.LeagueName, m.MatchDate, m.FlashscoreMatchKey,
       ht.TeamName + ' - ' + at.TeamName AS Affiche,
       (SELECT COUNT(*) FROM dbo.MatchTeamStats s WHERE s.MatchId = m.MatchId) AS NbStats,
       (SELECT COUNT(*) FROM dbo.Odds o          WHERE o.MatchId = m.MatchId) AS NbCotes
FROM dbo.Matches m
JOIN dbo.Seasons s  ON s.SeasonId = m.SeasonId
JOIN dbo.Leagues l  ON l.LeagueId = s.LeagueId
JOIN dbo.Teams   ht ON ht.TeamId = m.HomeTeamId
JOIN dbo.Teams   at ON at.TeamId = m.AwayTeamId
WHERE m.MatchStatus = 'Finished'
  AND m.MatchDate < DATEADD(HOUR, -48, GETDATE())
  AND m.MatchDate >= DATEADD(MONTH, -6, GETDATE())
  AND (SELECT COUNT(*) FROM dbo.MatchTeamStats t WHERE t.MatchId = m.MatchId) < 2
ORDER BY m.MatchDate DESC;

-- -----------------------------------------------------------------------------
-- 4. LE SERVICE TOURNE-T-IL ENCORE ?
--    Une exécution manquante est aussi grave qu'une page mal lue.
-- -----------------------------------------------------------------------------
SELECT TOP 15 '4. Dernières exécutions' AS Bloc,
       r.RunId, r.StartedAtUtc, r.Status,
       (SELECT COUNT(*) FROM dbo.AppExecutionLogs l WHERE l.RunId = r.RunId AND l.Level = 'ERROR') AS NbErreurs,
       (SELECT COUNT(*) FROM dbo.AppExecutionLogs l WHERE l.RunId = r.RunId AND l.Sector LIKE '%MatchProcessed') AS NbMatchs
FROM dbo.AppExecutionRuns r
ORDER BY r.StartedAtUtc DESC;

-- Étiquettes de cotes : vérifie que le nouveau scraper écrit bien le vocabulaire
-- historique (OPENING / J-2 / J-1 / DAY / H-1 / CLOSING) et non un dialecte parallèle.
SELECT '5. Étiquettes de cotes récentes' AS Bloc,
       ISNULL(SnapshotLabel, '(NULL)') AS SnapshotLabel, COUNT(*) AS Nb
FROM dbo.OddsQuotes
WHERE QuoteDateUtc >= DATEADD(DAY, -14, SYSUTCDATETIME())
GROUP BY SnapshotLabel
ORDER BY Nb DESC;
