/* =============================================================================
   Juniro_Evo - Correction des heures de match écrites en UTC par le service
   -----------------------------------------------------------------------------
   Jusqu'ici, le service convertissait l'heure Flashscore en UTC avant de l'écrire.
   Les données historiques, elles, sont en heure de Paris : d'où le décalage
   d'1 h (heure d'hiver) ou de 2 h (heure d'été) sur les matchs importés par le service.

   Les matchs concernés sont identifiés sans ambiguïté par le journal du service :
   toute clé présente dans AppExecutionLogs comme "match traité" a eu sa date
   réécrite en UTC, y compris les matchs anciens que le service a retouchés.

   'Romance Standard Time' est le fuseau de Paris : AT TIME ZONE applique le bon
   décalage à chaque date, changement d'heure du 25/10/2026 compris.

   Le script est protégé contre une double exécution (table dbo.SchemaMigrations).
   ÉTAPE 1 : lancer tel quel et contrôler l'aperçu.
   ÉTAPE 2 : passer @Appliquer à 1 et relancer.
   ============================================================================= */

/* Exécution forcée sur la bonne base : sans cela, SSMS reste sur master
   et toutes les requêtes échouent avec « Nom d'objet non valide ». */
USE [Juniro_Evo];
GO


DECLARE @Appliquer BIT = 0;   -- 0 = aperçu seulement, 1 = correction réelle
DECLARE @Migration NVARCHAR(100) = N'2026-10-29_MatchDate_UTC_vers_Paris';

IF OBJECT_ID('dbo.SchemaMigrations') IS NULL
    CREATE TABLE dbo.SchemaMigrations (
        MigrationName NVARCHAR(100) NOT NULL PRIMARY KEY,
        AppliedAtUtc  DATETIME2     NOT NULL DEFAULT SYSUTCDATETIME(),
        RowsAffected  INT           NULL
    );

IF EXISTS (SELECT 1 FROM dbo.SchemaMigrations WHERE MigrationName = @Migration)
BEGIN
    SELECT 'DÉJÀ APPLIQUÉE - ne pas relancer' AS Statut, *
    FROM dbo.SchemaMigrations WHERE MigrationName = @Migration;
    RETURN;
END;

-- Matchs dont la date a été écrite par le service
IF OBJECT_ID('tempdb..#Cibles') IS NOT NULL DROP TABLE #Cibles;

SELECT DISTINCT m.MatchId
INTO #Cibles
FROM dbo.Matches m
JOIN dbo.AppExecutionLogs l
  ON l.MatchIdent = m.FlashscoreMatchKey
WHERE l.ApplicationName = 'Juniro.ScraperService'
  AND l.Sector IN ('Scraper.MatchProcessed', 'Backfill.MatchProcessed');

-- -----------------------------------------------------------------------------
-- APERÇU : comparez quelques lignes avec l'heure affichée sur Flashscore.
-- Un match de Ligue 1 à 21:00 doit passer de 20:00 (hiver) ou 19:00 (été) à 21:00.
-- -----------------------------------------------------------------------------
SELECT COUNT(*) AS MatchsACorriger FROM #Cibles;

SELECT TOP 30
    l.LeagueName,
    ht.TeamName + ' - ' + at.TeamName AS Affiche,
    m.MatchDate AS DateActuelle_UTC,
    CAST((m.MatchDate AT TIME ZONE 'UTC') AT TIME ZONE 'Romance Standard Time' AS DATETIME2(0)) AS DateCorrigee_Paris
FROM #Cibles c
JOIN dbo.Matches m  ON m.MatchId = c.MatchId
JOIN dbo.Seasons s  ON s.SeasonId = m.SeasonId
JOIN dbo.Leagues l  ON l.LeagueId = s.LeagueId
JOIN dbo.Teams   ht ON ht.TeamId = m.HomeTeamId
JOIN dbo.Teams   at ON at.TeamId = m.AwayTeamId
ORDER BY m.MatchDate DESC;

-- -----------------------------------------------------------------------------
-- CORRECTION
-- -----------------------------------------------------------------------------
IF @Appliquer = 1
BEGIN
    BEGIN TRANSACTION;

    UPDATE m
    SET m.MatchDate = CAST((m.MatchDate AT TIME ZONE 'UTC') AT TIME ZONE 'Romance Standard Time' AS DATETIME2(0)),
        m.UpdatedAt = SYSUTCDATETIME()
    FROM dbo.Matches m
    JOIN #Cibles c ON c.MatchId = m.MatchId;

    DECLARE @Nb INT = @@ROWCOUNT;

    INSERT INTO dbo.SchemaMigrations (MigrationName, RowsAffected)
    VALUES (@Migration, @Nb);

    COMMIT TRANSACTION;

    SELECT 'CORRECTION APPLIQUÉE' AS Statut, @Nb AS LignesModifiees;
END
ELSE
BEGIN
    SELECT 'APERÇU SEULEMENT - passez @Appliquer à 1 pour corriger' AS Statut;
END;
