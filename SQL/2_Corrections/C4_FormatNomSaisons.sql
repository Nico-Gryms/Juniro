/* =============================================================================
   Juniro_Evo - Uniformisation du nom des saisons : "2026/2027" -> "2026-2027"
   -----------------------------------------------------------------------------
   L'historique nomme ses saisons "2025-2026", le nouveau scraper créait "2026/2027".
   Le scraper corrigé écrit désormais le format de l'historique ; ce script renomme les
   saisons déjà créées avec une barre.

   Garde-fou : si une ligue possède déjà la même saison sous les deux formats, rien n'est
   renommé (il faudrait d'abord fusionner les deux saisons, le script le signale).

   ÉTAPE 1 : lancer tel quel et contrôler l'aperçu.
   ÉTAPE 2 : passer @Appliquer à 1 et relancer.
   Documentation : DOCUMENTATION.md (racine du projet), chapitre 4.4.
   ============================================================================= */
USE [Juniro_Evo];
GO

DECLARE @Appliquer BIT = 0;
DECLARE @Migration NVARCHAR(100) = N'2026-10-31_FormatNomSaisons';

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

-- Aperçu : saisons à renommer, et éventuels conflits avec un nom déjà existant
SELECT l.LeagueName, s.SeasonId, s.SeasonName AS NomActuel,
       REPLACE(s.SeasonName, '/', '-') AS NouveauNom,
       (SELECT COUNT(*) FROM dbo.Matches m WHERE m.SeasonId = s.SeasonId) AS NbMatchs,
       CASE WHEN EXISTS (SELECT 1 FROM dbo.Seasons d
                         WHERE d.LeagueId = s.LeagueId
                           AND d.SeasonName = REPLACE(s.SeasonName, '/', '-'))
            THEN 'CONFLIT : saison en double à fusionner' ELSE 'ok' END AS Controle
FROM dbo.Seasons s
JOIN dbo.Leagues l ON l.LeagueId = s.LeagueId
WHERE s.SeasonName LIKE '%/%'
ORDER BY l.LeagueName;

IF @Appliquer = 1
BEGIN
    IF EXISTS (SELECT 1 FROM dbo.Seasons s
               WHERE s.SeasonName LIKE '%/%'
                 AND EXISTS (SELECT 1 FROM dbo.Seasons d
                             WHERE d.LeagueId = s.LeagueId
                               AND d.SeasonName = REPLACE(s.SeasonName, '/', '-')))
    BEGIN
        SELECT 'ARRÊT : conflit détecté, aucune modification. Envoyez l''aperçu.' AS Statut;
        RETURN;
    END;

    BEGIN TRANSACTION;

    UPDATE dbo.Seasons
    SET SeasonName = REPLACE(SeasonName, '/', '-')
    WHERE SeasonName LIKE '%/%';
    DECLARE @Nb INT = @@ROWCOUNT;

    INSERT INTO dbo.SchemaMigrations (MigrationName, RowsAffected) VALUES (@Migration, @Nb);

    COMMIT TRANSACTION;
    SELECT 'CORRECTION APPLIQUÉE' AS Statut, @Nb AS SaisonsRenommees;
END
ELSE
BEGIN
    SELECT 'APERÇU SEULEMENT - passez @Appliquer à 1 pour corriger' AS Statut;
END;
