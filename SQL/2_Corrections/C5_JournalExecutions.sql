/* =============================================================================
   Juniro_Evo - Journal d'exécution fiable (dbo.AppExecutionRuns)
   -----------------------------------------------------------------------------
   Problème : chaque exécution du scraper restait « Running » pour toujours (aucune
   écriture de fin), sans heure de fin ni bilan. Impossible de savoir d'un coup d'œil
   si l'import de la nuit s'est bien passé.

   Ce script :
     1. affiche la structure actuelle des tables AppExecutionRuns / AppExecutionLogs ;
     2. ajoute les colonnes de bilan manquantes : FinishedAtUtc, DurationSeconds,
        MatchesFound, MatchesSaved, MatchesSkipped, MatchesFailed, QualityDegraded,
        LogWriteFailures, Summary, ErrorMessage ;
     3. élargit Status s'il est trop court pour 'Interrupted' et élargit une éventuelle
        contrainte CHECK sur Status (nouveaux statuts : Success, Warning, Failed, Interrupted) ;
     4. reconstitue le bilan des anciennes exécutions à partir de AppExecutionLogs ;
     5. crée la vue dbo.vw_ScraperRuns (dernières exécutions, en heure de Paris).

   Le service mis à jour remplit ces colonnes tout seul. Il fonctionne aussi AVANT ce
   script (il n'écrit alors que Status), mais le bilan complet exige ce script.

   ÉTAPE 1 : lancer tel quel et contrôler l'aperçu (rien n'est modifié).
   ÉTAPE 2 : passer @Appliquer à 1 et relancer.
   Ensuite  : SQL/4_Controles/Q4_DernieresExecutions.sql chaque matin.
   Documentation : DOCUMENTATION.md (racine du projet), chapitre 4.5.
   ============================================================================= */
USE [Juniro_Evo];
GO

DECLARE @Appliquer BIT = 0;
DECLARE @Migration NVARCHAR(100) = N'2026-11-08_JournalExecutions';

IF OBJECT_ID('dbo.SchemaMigrations') IS NULL
    CREATE TABLE dbo.SchemaMigrations (
        MigrationName NVARCHAR(100) NOT NULL PRIMARY KEY,
        AppliedAtUtc  DATETIME2     NOT NULL DEFAULT SYSUTCDATETIME(),
        RowsAffected  INT           NULL
    );

IF EXISTS (SELECT 1 FROM dbo.SchemaMigrations WHERE MigrationName = @Migration)
BEGIN
    SELECT 'DÉJÀ APPLIQUÉE - ne pas relancer (la vue dbo.vw_ScraperRuns existe)' AS Statut, *
    FROM dbo.SchemaMigrations WHERE MigrationName = @Migration;
    RETURN;
END;

-- -----------------------------------------------------------------------------
-- APERÇU 1 : colonnes actuelles des deux tables
-- -----------------------------------------------------------------------------
SELECT '1. Colonnes actuelles' AS Bloc, t.name AS TableName, c.column_id, c.name AS ColumnName,
       ty.name AS Type,
       CASE WHEN c.max_length = -1 THEN 'MAX'
            WHEN ty.name IN ('nvarchar','nchar') THEN CAST(c.max_length / 2 AS VARCHAR(10))
            WHEN ty.name IN ('varchar','char')   THEN CAST(c.max_length AS VARCHAR(10))
            ELSE '' END AS Longueur,
       c.is_nullable, c.is_identity, c.is_computed
FROM sys.columns c
JOIN sys.tables t  ON t.object_id = c.object_id
JOIN sys.types  ty ON ty.user_type_id = c.user_type_id
WHERE SCHEMA_NAME(t.schema_id) = 'dbo' AND t.name IN ('AppExecutionRuns', 'AppExecutionLogs')
ORDER BY t.name, c.column_id;

-- APERÇU 2 : contraintes CHECK de la table des exécutions
SELECT '2. Contraintes CHECK' AS Bloc, cc.name AS Contrainte, cc.definition
FROM sys.check_constraints cc
WHERE cc.parent_object_id = OBJECT_ID('dbo.AppExecutionRuns');

-- APERÇU 3 : statuts actuels
SELECT '3. Statuts actuels' AS Bloc, TriggerSource, Status, COUNT(*) AS Nb,
       MIN(StartedAtUtc) AS Premiere, MAX(StartedAtUtc) AS Derniere
FROM dbo.AppExecutionRuns
GROUP BY TriggerSource, Status;

-- APERÇU 4 : ce que donnera la reconstitution des anciennes exécutions
WITH L AS (
    SELECT l.RunId,
           MAX(l.CreatedAtUtc) AS DernierLog,
           SUM(CASE WHEN l.Sector LIKE '%.MatchProcessed' THEN 1 ELSE 0 END) AS NbMatchProcessed,
           SUM(CASE WHEN l.Sector LIKE '%.MatchError'     THEN 1 ELSE 0 END) AS NbMatchError,
           SUM(CASE WHEN l.Level = 'FATAL' THEN 1 ELSE 0 END) AS NbFatal,
           SUM(CASE WHEN l.Level = 'ERROR' THEN 1 ELSE 0 END) AS NbError,
           MAX(CASE WHEN l.Sector LIKE '%.Finished' THEN l.Message END) AS MessageFin
    FROM dbo.AppExecutionLogs l
    GROUP BY l.RunId
)
SELECT TOP 40 '4. Reconstitution (aperçu)' AS Bloc, L.RunId, L.DernierLog AS FinEstimeeUtc,
       L.NbMatchProcessed, L.NbMatchError, L.NbFatal, L.NbError,
       CASE WHEN L.NbFatal > 0 THEN 'Failed'
            WHEN L.MessageFin IS NULL THEN 'Interrupted'
            WHEN L.NbError > 0 OR L.NbMatchError > 0 THEN 'Warning'
            ELSE 'Success' END AS StatutReconstitue,
       LEFT(L.MessageFin, 200) AS MessageFin
FROM L
ORDER BY L.RunId DESC;

IF @Appliquer = 0
BEGIN
    SELECT 'APERÇU SEULEMENT - passez @Appliquer à 1 pour corriger' AS Statut;
    RETURN;
END;

-- =============================================================================
-- APPLICATION
-- Les requêtes qui utilisent les nouvelles colonnes passent par sp_executesql :
-- SQL Server refuse de compiler un lot qui cite une colonne pas encore créée.
-- =============================================================================
BEGIN TRY
    BEGIN TRANSACTION;

    -- 2. Colonnes de bilan (ajoutées seulement si absentes)
    IF COL_LENGTH('dbo.AppExecutionRuns', 'FinishedAtUtc')    IS NULL ALTER TABLE dbo.AppExecutionRuns ADD FinishedAtUtc    DATETIME2(0)   NULL;
    IF COL_LENGTH('dbo.AppExecutionRuns', 'DurationSeconds')  IS NULL ALTER TABLE dbo.AppExecutionRuns ADD DurationSeconds  INT            NULL;
    IF COL_LENGTH('dbo.AppExecutionRuns', 'MatchesFound')     IS NULL ALTER TABLE dbo.AppExecutionRuns ADD MatchesFound     INT            NULL;
    IF COL_LENGTH('dbo.AppExecutionRuns', 'MatchesSaved')     IS NULL ALTER TABLE dbo.AppExecutionRuns ADD MatchesSaved     INT            NULL;
    IF COL_LENGTH('dbo.AppExecutionRuns', 'MatchesSkipped')   IS NULL ALTER TABLE dbo.AppExecutionRuns ADD MatchesSkipped   INT            NULL;
    IF COL_LENGTH('dbo.AppExecutionRuns', 'MatchesFailed')    IS NULL ALTER TABLE dbo.AppExecutionRuns ADD MatchesFailed    INT            NULL;
    IF COL_LENGTH('dbo.AppExecutionRuns', 'QualityDegraded')  IS NULL ALTER TABLE dbo.AppExecutionRuns ADD QualityDegraded  BIT            NULL;
    IF COL_LENGTH('dbo.AppExecutionRuns', 'LogWriteFailures') IS NULL ALTER TABLE dbo.AppExecutionRuns ADD LogWriteFailures INT            NULL;
    IF COL_LENGTH('dbo.AppExecutionRuns', 'Summary')          IS NULL ALTER TABLE dbo.AppExecutionRuns ADD Summary          NVARCHAR(2000) NULL;
    IF COL_LENGTH('dbo.AppExecutionRuns', 'ErrorMessage')     IS NULL ALTER TABLE dbo.AppExecutionRuns ADD ErrorMessage     NVARCHAR(2000) NULL;

    -- 3a. Contraintes CHECK sur Status : supprimées ici, recréées élargies plus bas
    DECLARE @HadCheck BIT = 0, @sql NVARCHAR(MAX), @ck SYSNAME;
    DECLARE ck CURSOR LOCAL FAST_FORWARD FOR
        SELECT cc.name FROM sys.check_constraints cc
        WHERE cc.parent_object_id = OBJECT_ID('dbo.AppExecutionRuns')
          AND cc.definition LIKE '%Status%';
    OPEN ck;
    FETCH NEXT FROM ck INTO @ck;
    WHILE @@FETCH_STATUS = 0
    BEGIN
        SET @HadCheck = 1;
        SET @sql = N'ALTER TABLE dbo.AppExecutionRuns DROP CONSTRAINT ' + QUOTENAME(@ck) + N';';
        EXEC sp_executesql @sql;
        FETCH NEXT FROM ck INTO @ck;
    END;
    CLOSE ck; DEALLOCATE ck;

    -- 3b. Status trop court pour 'Interrupted' (11 caractères) : élargi à 20
    DECLARE @StatusLen INT, @StatusNullable BIT, @StatusType SYSNAME;
    SELECT @StatusLen = CASE WHEN ty.name IN ('nvarchar','nchar') THEN c.max_length / 2 ELSE c.max_length END,
           @StatusNullable = c.is_nullable, @StatusType = ty.name
    FROM sys.columns c JOIN sys.types ty ON ty.user_type_id = c.user_type_id
    WHERE c.object_id = OBJECT_ID('dbo.AppExecutionRuns') AND c.name = 'Status';

    IF @StatusLen BETWEEN 1 AND 19
    BEGIN
        SET @sql = N'ALTER TABLE dbo.AppExecutionRuns ALTER COLUMN [Status] NVARCHAR(20) '
                 + CASE WHEN @StatusNullable = 1 THEN N'NULL' ELSE N'NOT NULL' END + N';';
        EXEC sp_executesql @sql;
    END;

    -- 4. Reconstitution des anciennes exécutions restées « Running » (plus de 12 h)
    SET @sql = N'
    WITH L AS (
        SELECT l.RunId,
               MAX(l.CreatedAtUtc) AS DernierLog,
               SUM(CASE WHEN l.Sector LIKE ''%.MatchProcessed'' THEN 1 ELSE 0 END) AS NbMatchProcessed,
               SUM(CASE WHEN l.Sector LIKE ''%.MatchError''     THEN 1 ELSE 0 END) AS NbMatchError,
               SUM(CASE WHEN l.Level = ''FATAL'' THEN 1 ELSE 0 END) AS NbFatal,
               SUM(CASE WHEN l.Level = ''ERROR'' THEN 1 ELSE 0 END) AS NbError,
               MAX(CASE WHEN l.Sector LIKE ''%.Finished'' THEN l.Message END) AS MessageFin
        FROM dbo.AppExecutionLogs l
        GROUP BY l.RunId
    ), P AS (
        -- Nombre de matchs lu dans l''ancien message de fin :
        --   « Scraping terminé. 72 matchs insérés. » / « Backfill terminé en 01:02:03. 120 enregistrés, ... »
        SELECT L.*,
               TRY_CAST(SUBSTRING(L.MessageFin, CHARINDEX(''. '', L.MessageFin) + 2,
                        NULLIF(CHARINDEX('' '', L.MessageFin, CHARINDEX(''. '', L.MessageFin) + 2), 0)
                        - (CHARINDEX(''. '', L.MessageFin) + 2)) AS INT) AS NbLuDansMessage
        FROM L
    )
    UPDATE r SET
        r.[Status]         = CASE WHEN P.NbFatal > 0 THEN ''Failed''
                                  WHEN P.MessageFin IS NULL THEN ''Interrupted''
                                  WHEN P.NbError > 0 OR P.NbMatchError > 0 THEN ''Warning''
                                  ELSE ''Success'' END,
        r.FinishedAtUtc    = CASE WHEN P.MessageFin IS NOT NULL THEN P.DernierLog END,
        r.DurationSeconds  = CASE WHEN P.MessageFin IS NOT NULL THEN DATEDIFF(SECOND, r.StartedAtUtc, P.DernierLog) END,
        r.MatchesSaved     = COALESCE(CASE WHEN P.NbLuDansMessage >= 0 THEN P.NbLuDansMessage END, P.NbMatchProcessed),
        r.MatchesFailed    = P.NbMatchError,
        r.Summary          = N''Reconstitué depuis AppExecutionLogs (exécution antérieure au correctif du journal). ''
                             + COALESCE(LEFT(P.MessageFin, 1500), N''Aucune ligne de fin : exécution interrompue.'')
    FROM dbo.AppExecutionRuns r
    LEFT JOIN P ON P.RunId = r.' + QUOTENAME(
        COALESCE((SELECT name FROM sys.identity_columns WHERE object_id = OBJECT_ID('dbo.AppExecutionRuns')),
                 CASE WHEN COL_LENGTH('dbo.AppExecutionRuns', 'RunId') IS NOT NULL THEN 'RunId' ELSE 'Id' END)) + N'
    WHERE r.[Status] = ''Running''
      AND r.StartedAtUtc < DATEADD(HOUR, -12, SYSUTCDATETIME());
    SELECT @n = @@ROWCOUNT;';
    DECLARE @Nb INT;
    EXEC sp_executesql @sql, N'@n INT OUTPUT', @n = @Nb OUTPUT;

    -- 3c. Contrainte CHECK recréée, élargie aux nouveaux statuts (seulement s'il y en avait une)
    IF @HadCheck = 1
    BEGIN
        DECLARE @list NVARCHAR(MAX);
        SELECT @list = STRING_AGG(N'''' + REPLACE(s, N'''', N'''''') + N'''', N', ')
        FROM (SELECT [Status] AS s FROM dbo.AppExecutionRuns WHERE [Status] IS NOT NULL
              UNION SELECT v FROM (VALUES (N'Running'), (N'Success'), (N'Warning'), (N'Failed'), (N'Interrupted')) x(v)) u;
        SET @sql = N'ALTER TABLE dbo.AppExecutionRuns ADD CONSTRAINT CK_AppExecutionRuns_Status CHECK ([Status] IN (' + @list + N'));';
        EXEC sp_executesql @sql;
    END;

    INSERT INTO dbo.SchemaMigrations (MigrationName, RowsAffected) VALUES (@Migration, @Nb);
    COMMIT TRANSACTION;
    SELECT 'CORRECTION APPLIQUÉE' AS Statut, @Nb AS ExecutionsReconstituees,
           CASE WHEN @HadCheck = 1 THEN 'Contrainte CHECK élargie' ELSE 'Pas de contrainte CHECK' END AS ContrainteStatus;
END TRY
BEGIN CATCH
    IF @@TRANCOUNT > 0 ROLLBACK TRANSACTION;
    SELECT 'ÉCHEC - rien n''a été modifié. Envoyez ce message.' AS Statut,
           ERROR_NUMBER() AS Numero, ERROR_MESSAGE() AS Message;
    RETURN;
END CATCH;
GO

-- -----------------------------------------------------------------------------
-- 5. Vue de consultation dbo.vw_ScraperRuns (lot séparé : CREATE VIEW doit être seul)
--    Créée par SQL dynamique car le nom de la colonne identifiant (RunId ou Id) varie.
-- -----------------------------------------------------------------------------
-- Toutes les colonnes de bilan doivent exister (FinishedAtUtc existait déjà dans certaines
-- installations : le tester seul ne suffit pas).
IF COL_LENGTH('dbo.AppExecutionRuns', 'FinishedAtUtc')    IS NOT NULL
   AND COL_LENGTH('dbo.AppExecutionRuns', 'DurationSeconds')  IS NOT NULL
   AND COL_LENGTH('dbo.AppExecutionRuns', 'MatchesFound')     IS NOT NULL
   AND COL_LENGTH('dbo.AppExecutionRuns', 'MatchesSaved')     IS NOT NULL
   AND COL_LENGTH('dbo.AppExecutionRuns', 'MatchesSkipped')   IS NOT NULL
   AND COL_LENGTH('dbo.AppExecutionRuns', 'MatchesFailed')    IS NOT NULL
   AND COL_LENGTH('dbo.AppExecutionRuns', 'QualityDegraded')  IS NOT NULL
   AND COL_LENGTH('dbo.AppExecutionRuns', 'LogWriteFailures') IS NOT NULL
   AND COL_LENGTH('dbo.AppExecutionRuns', 'Summary')          IS NOT NULL
   AND COL_LENGTH('dbo.AppExecutionRuns', 'ErrorMessage')     IS NOT NULL
BEGIN
    DECLARE @id SYSNAME = COALESCE(
        (SELECT name FROM sys.identity_columns WHERE object_id = OBJECT_ID('dbo.AppExecutionRuns')),
        CASE WHEN COL_LENGTH('dbo.AppExecutionRuns', 'RunId') IS NOT NULL THEN 'RunId' ELSE 'Id' END);

    DECLARE @v NVARCHAR(MAX) = N'
    CREATE OR ALTER VIEW dbo.vw_ScraperRuns AS
    /* Une ligne par exécution du scraper, heures en heure de Paris.
       Etat : lecture en français du statut, avec « BLOQUÉE ? » pour une exécution
       « Running » depuis plus de 6 h. Documentation : DOCUMENTATION.md, chapitre 4.5. */
    SELECT r.' + QUOTENAME(@id) + N' AS RunId,
           r.TriggerSource,
           CAST((r.StartedAtUtc  AT TIME ZONE ''UTC'') AT TIME ZONE ''Romance Standard Time'' AS DATETIME2(0)) AS DebutParis,
           CAST((r.FinishedAtUtc AT TIME ZONE ''UTC'') AT TIME ZONE ''Romance Standard Time'' AS DATETIME2(0)) AS FinParis,
           CASE WHEN r.DurationSeconds IS NULL THEN NULL
                ELSE CONVERT(VARCHAR(8), DATEADD(SECOND, r.DurationSeconds, 0), 108) END AS Duree,
           r.[Status],
           CASE WHEN r.[Status] = ''Running'' AND r.StartedAtUtc < DATEADD(HOUR, -6, SYSUTCDATETIME()) THEN ''BLOQUÉE ?''
                WHEN r.[Status] = ''Running''     THEN ''En cours''
                WHEN r.[Status] = ''Success''     THEN ''OK''
                WHEN r.[Status] = ''Warning''     THEN ''Terminée avec alertes''
                WHEN r.[Status] = ''Failed''      THEN ''ÉCHEC''
                WHEN r.[Status] = ''Interrupted'' THEN ''Interrompue''
                ELSE r.[Status] END AS Etat,
           r.MatchesFound, r.MatchesSaved, r.MatchesSkipped, r.MatchesFailed,
           r.QualityDegraded, r.LogWriteFailures,
           (SELECT COUNT(*) FROM dbo.AppExecutionLogs l WHERE l.RunId = r.' + QUOTENAME(@id) + N'
                AND l.Level IN (''ERROR'', ''FATAL'')) AS NbErreursJournal,
           r.Summary, r.ErrorMessage
    FROM dbo.AppExecutionRuns r;';
    EXEC sp_executesql @v;
    SELECT 'Vue dbo.vw_ScraperRuns prête' AS Statut;
END
ELSE
    SELECT 'Vue non créée : passez @Appliquer à 1 (les colonnes de bilan manquent).' AS Statut;
GO
