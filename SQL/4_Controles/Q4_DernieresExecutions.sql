/* =============================================================================
   Juniro_Evo - L'import de la nuit s'est-il bien passé ?
   -----------------------------------------------------------------------------
   À lancer chaque matin (le scraper passe à 03h00, heure de Paris).
   Exige SQL/2_Corrections/C5_JournalExecutions.sql (vue dbo.vw_ScraperRuns).

   Lecture de la colonne Etat :
     OK                     tout est enregistré, contrôle qualité au vert
     Terminée avec alertes  des matchs en échec, contrôle qualité en alerte, ou aucun match
     ÉCHEC                  plantage, ou aucun match enregistré malgré des tentatives
     Interrompue            le service s'est arrêté avant la fin (PC éteint, service stoppé)
     BLOQUÉE ?              « en cours » depuis plus de 6 h
   Aucune ligne datée d'aujourd'hui = le service n'a pas tourné (PC éteint ? service arrêté ?).
   Documentation : DOCUMENTATION.md (racine du projet), chapitre 4.5.
   ============================================================================= */
USE [Juniro_Evo];
GO

-- 1. Les 15 dernières exécutions, la plus récente en haut
SELECT TOP 15 '1. Dernières exécutions' AS Bloc,
       RunId, TriggerSource, DebutParis, FinParis, Duree, Etat,
       MatchesFound AS Trouves, MatchesSaved AS Enregistres, MatchesSkipped AS NonEnregistres,
       MatchesFailed AS Echecs, QualityDegraded AS AlerteQualite, NbErreursJournal,
       LEFT(Summary, 600) AS Bilan
FROM dbo.vw_ScraperRuns
ORDER BY DebutParis DESC;

-- 2. Le service a-t-il tourné ces 7 derniers jours ? (une ligne par jour, journalier seulement)
WITH Jours AS (
    SELECT CAST(DATEADD(DAY, -n, CAST(SYSDATETIME() AS DATE)) AS DATE) AS Jour
    FROM (VALUES (0), (1), (2), (3), (4), (5), (6)) x(n)
)
SELECT '2. Passages quotidiens' AS Bloc, j.Jour,
       COUNT(r.RunId) AS NbExecutions,
       MAX(r.Etat) AS Etat,
       SUM(r.MatchesSaved) AS Enregistres,
       CASE WHEN COUNT(r.RunId) = 0 THEN 'AUCUNE EXÉCUTION : PC éteint ou service arrêté ?' ELSE '' END AS Alerte
FROM Jours j
LEFT JOIN dbo.vw_ScraperRuns r
       ON CAST(r.DebutParis AS DATE) = j.Jour AND r.TriggerSource = 'ScheduledTask'
GROUP BY j.Jour
ORDER BY j.Jour DESC;

-- 3. Détail des problèmes de la dernière exécution (avertissements, erreurs, bilan)
DECLARE @Dernier INT = (SELECT TOP 1 RunId FROM dbo.vw_ScraperRuns ORDER BY DebutParis DESC);

SELECT '3. Problèmes de la dernière exécution' AS Bloc,
       l.CreatedAtUtc, l.Level, l.Sector, l.MatchIdent,
       LEFT(l.Message, 400) AS Message, LEFT(l.Exception, 400) AS Exception
FROM dbo.AppExecutionLogs l
WHERE l.RunId = @Dernier
  AND (l.Level IN ('WARN', 'ERROR', 'FATAL') OR l.Sector LIKE '%.Finished' OR l.Sector LIKE '%.Collected')
ORDER BY l.CreatedAtUtc;

-- 4. Matchs enregistrés par la dernière exécution (une ligne par match : la preuve de l'import)
SELECT '4. Matchs enregistrés' AS Bloc, l.CreatedAtUtc, l.MatchIdent, LEFT(l.Message, 300) AS Message
FROM dbo.AppExecutionLogs l
WHERE l.RunId = @Dernier AND l.Sector LIKE '%.MatchProcessed'
ORDER BY l.CreatedAtUtc;
