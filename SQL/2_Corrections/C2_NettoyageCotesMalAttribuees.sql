/* =============================================================================
   Juniro_Evo - Nettoyage des cotes écrites par le nouveau scraper avant correction
   -----------------------------------------------------------------------------
   Jusqu'à la correction du 31/10, le nouveau scraper lisait l'encart de cotes de la page
   résumé (toujours le 1X2) à la place du marché demandé. Pour toutes les cotes qu'il a
   écrites (service depuis le 25/10, rattrapages des 29 et 30/10) :
     - BTTS, Double chance, DNB : valeurs 1X2 enregistrées à la place ;
     - Plus/Moins : vide ;
     - 1X2 : valeurs justes.

   Règle appliquée, relevé par relevé, selon l'heure du coup d'envoi :
     - Relevé APRÈS le coup d'envoi (rattrapage, ou service à J+1) : supprimé, tous
       marchés. Le rattrapage corrigé le refera proprement, avec ouverture ET clôture.
     - Relevé AVANT le coup d'envoi (service J-3 à J-1) : le 1X2 est conservé, c'est un
       vrai relevé d'avant-match qu'on ne peut plus refaire. Les autres marchés sont
       supprimés.
     - Étiquette OPENING posée par erreur sur un relevé à J-3 : renommée J-3. Sinon le
       rattrapage, qui n'écrit la vraie ouverture que si aucune n'existe, s'en abstiendrait.

   Version 2 : remplace la version précédente de ce script, qui ne traitait pas les deux
   derniers points. Relancer sans risque même si la version 1 a déjà été appliquée.
   Si cette version 2 a déjà été appliquée, ne rien faire : le script le détecte.

   PRÉREQUIS : C1_HeuresMatchsUtcVersParis.sql appliqué (les heures de coup d'envoi
   doivent être en heure de Paris pour que la comparaison avant/après soit juste).

   L'historique de l'ancien pipeline (dernier relevé : 20/05/2026) n'est pas concerné.

   ÉTAPE 1 : lancer tel quel et contrôler l'aperçu.
   ÉTAPE 2 : passer @Appliquer à 1 et relancer.
   Documentation : DOCUMENTATION.md (racine du projet), chapitre 4.4.
   ============================================================================= */
USE [Juniro_Evo];
GO

DECLARE @Appliquer BIT       = 0;               -- 0 = aperçu, 1 = correction
DECLARE @Depuis    DATETIME2 = '2026-10-01';           -- début du nouveau scraper (UTC)
DECLARE @Jusqua    DATETIME2 = '2026-10-31 17:55:00';  -- correction déployée, 18:55 heure de Paris (UTC)
-- Seuls les relevés INSÉRÉS entre ces deux dates sont touchés : ceux écrits par le scraper
-- corrigé (rattrapage du 31/10 au soir) sont justes et doivent être conservés. On filtre sur
-- la date d'insertion et non sur QuoteDateUtc, car une cote d'ouverture est datée dans le passé.
DECLARE @Migration NVARCHAR(100) = N'2026-10-31_CotesMalAttribuees_v2';

IF OBJECT_ID('dbo.SchemaMigrations') IS NULL
    CREATE TABLE dbo.SchemaMigrations (
        MigrationName NVARCHAR(100) NOT NULL PRIMARY KEY,
        AppliedAtUtc  DATETIME2     NOT NULL DEFAULT SYSUTCDATETIME(),
        RowsAffected  INT           NULL
    );

IF NOT EXISTS (SELECT 1 FROM dbo.SchemaMigrations WHERE MigrationName = N'2026-10-29_MatchDate_UTC_vers_Paris')
BEGIN
    SELECT 'ARRÊT : appliquez d''abord C1_HeuresMatchsUtcVersParis.sql' AS Statut;
    RETURN;
END;

IF EXISTS (SELECT 1 FROM dbo.SchemaMigrations WHERE MigrationName = @Migration)
BEGIN
    SELECT 'DÉJÀ APPLIQUÉE - ne pas relancer' AS Statut, *
    FROM dbo.SchemaMigrations WHERE MigrationName = @Migration;
    RETURN;
END;

-- Coup d'envoi de chaque match concerné, converti en UTC pour comparer aux relevés.
IF OBJECT_ID('tempdb..#Kickoff') IS NOT NULL DROP TABLE #Kickoff;

SELECT m.MatchId,
       CAST((CAST(m.MatchDate AS DATETIME2) AT TIME ZONE 'Romance Standard Time') AT TIME ZONE 'UTC' AS DATETIME2) AS KickoffUtc
INTO #Kickoff
FROM dbo.Matches m
WHERE m.MatchId IN (SELECT MatchId FROM dbo.OddsQuotes WHERE CreatedAt >= @Depuis AND CreatedAt < @Jusqua
                    UNION
                    SELECT MatchId FROM dbo.Odds WHERE DateAdded >= @Depuis AND DateAdded < @Jusqua);

-- -----------------------------------------------------------------------------
-- APERÇU
-- -----------------------------------------------------------------------------
SELECT 'OddsQuotes' AS TableCible,
       CASE WHEN q.QuoteDateUtc >= k.KickoffUtc THEN 'Après coup d''envoi : supprimé'
            WHEN q.MarketCode <> '1X2'          THEN 'Avant : autre marché, supprimé'
            WHEN q.SnapshotLabel = 'OPENING'    THEN 'Avant : 1X2 conservé, OPENING renommé J-3'
            ELSE                                     'Avant : 1X2 conservé' END AS Action,
       COUNT(*) AS Lignes, COUNT(DISTINCT q.MatchId) AS Matchs
FROM dbo.OddsQuotes q
JOIN #Kickoff k ON k.MatchId = q.MatchId
WHERE q.QuoteDateUtc >= k.KickoffUtc
   OR (q.CreatedAt >= @Depuis AND q.CreatedAt < @Jusqua)
GROUP BY CASE WHEN q.QuoteDateUtc >= k.KickoffUtc THEN 'Après coup d''envoi : supprimé'
              WHEN q.MarketCode <> '1X2'          THEN 'Avant : autre marché, supprimé'
              WHEN q.SnapshotLabel = 'OPENING'    THEN 'Avant : 1X2 conservé, OPENING renommé J-3'
              ELSE                                     'Avant : 1X2 conservé' END
UNION ALL
SELECT 'Odds',
       CASE WHEN o.DateAdded >= k.KickoffUtc THEN 'Après coup d''envoi : supprimé'
            ELSE 'Avant : 1X2 conservé, autres marchés vidés' END,
       COUNT(*), COUNT(DISTINCT o.MatchId)
FROM dbo.Odds o
JOIN #Kickoff k ON k.MatchId = o.MatchId
WHERE o.DateAdded >= k.KickoffUtc
   OR (o.DateAdded >= @Depuis AND o.DateAdded < @Jusqua)
GROUP BY CASE WHEN o.DateAdded >= k.KickoffUtc THEN 'Après coup d''envoi : supprimé'
              ELSE 'Avant : 1X2 conservé, autres marchés vidés' END
ORDER BY TableCible, Action;

-- -----------------------------------------------------------------------------
-- CORRECTION
-- -----------------------------------------------------------------------------
IF @Appliquer = 1
BEGIN
    BEGIN TRANSACTION;

    -- 1. Relevés d'après-match des matchs touchés : TOUS supprimés, y compris ceux, justes,
    --    du rattrapage corrigé. Sinon la table [Odds] garderait un trou à l'index 1 (réservé à
    --    l'ouverture). Le rattrapage les refait intégralement : ces matchs redeviennent incomplets.
    DELETE q FROM dbo.OddsQuotes q
    JOIN #Kickoff k ON k.MatchId = q.MatchId
    WHERE q.QuoteDateUtc >= k.KickoffUtc;
    DECLARE @N1 INT = @@ROWCOUNT;

    DELETE o FROM dbo.Odds o
    JOIN #Kickoff k ON k.MatchId = o.MatchId
    WHERE o.DateAdded >= k.KickoffUtc;
    DECLARE @N2 INT = @@ROWCOUNT;

    -- 2. Relevés d'avant-match : seul le 1X2 était juste.
    DELETE q FROM dbo.OddsQuotes q
    JOIN #Kickoff k ON k.MatchId = q.MatchId
    WHERE q.CreatedAt >= @Depuis AND q.CreatedAt < @Jusqua AND q.QuoteDateUtc < k.KickoffUtc
      AND q.MarketCode <> '1X2';
    DECLARE @N3 INT = @@ROWCOUNT;

    UPDATE o
    SET OddBTTS = NULL, OddBTNS = NULL,
        OddHomeDraw = NULL, OddAwayDraw = NULL,
        OddHomeDNB = NULL, OddAwayDNB = NULL,
        OddOv15 = NULL, OddUn15 = NULL,
        OddOv25 = NULL, OddUn25 = NULL,
        OddOv35 = NULL, OddUn35 = NULL
    FROM dbo.Odds o
    JOIN #Kickoff k ON k.MatchId = o.MatchId
    WHERE o.DateAdded >= @Depuis AND o.DateAdded < @Jusqua AND o.DateAdded < k.KickoffUtc;
    DECLARE @N4 INT = @@ROWCOUNT;

    -- 3. Fausse étiquette OPENING sur un relevé à J-3 : la vraie ouverture viendra du rattrapage.
    UPDATE q SET q.SnapshotLabel = 'J-3'
    FROM dbo.OddsQuotes q
    JOIN #Kickoff k ON k.MatchId = q.MatchId
    WHERE q.CreatedAt >= @Depuis AND q.CreatedAt < @Jusqua AND q.QuoteDateUtc < k.KickoffUtc
      AND q.SnapshotLabel = 'OPENING';
    DECLARE @N5 INT = @@ROWCOUNT;

    -- 4. Drapeaux d'ouverture et de clôture : reportés sur le premier et le dernier relevé
    --    restants de chaque série touchée.
    ;WITH Series AS (
        SELECT q.OddsQuoteId,
               ROW_NUMBER() OVER (PARTITION BY q.MatchId, q.BookmakerId, q.MarketCode, q.SelectionCode, q.LineValue
                                  ORDER BY q.QuoteDateUtc ASC,  q.OddsQuoteId ASC)  AS RnFirst,
               ROW_NUMBER() OVER (PARTITION BY q.MatchId, q.BookmakerId, q.MarketCode, q.SelectionCode, q.LineValue
                                  ORDER BY q.QuoteDateUtc DESC, q.OddsQuoteId DESC) AS RnLast
        FROM dbo.OddsQuotes q
        JOIN #Kickoff k ON k.MatchId = q.MatchId
    )
    UPDATE q
    SET q.IsOpening = CASE WHEN s.RnFirst = 1 THEN 1 ELSE 0 END,
        q.IsClosing = CASE WHEN s.RnLast  = 1 THEN 1 ELSE 0 END
    FROM dbo.OddsQuotes q
    JOIN Series s ON s.OddsQuoteId = q.OddsQuoteId;

    INSERT INTO dbo.SchemaMigrations (MigrationName, RowsAffected)
    VALUES (@Migration, @N1 + @N2 + @N3 + @N4 + @N5);

    COMMIT TRANSACTION;

    SELECT 'CORRECTION APPLIQUÉE' AS Statut,
           @N1 AS QuotesApresMatchSupprimees, @N2 AS OddsApresMatchSupprimees,
           @N3 AS QuotesAutresMarchesSupprimees, @N4 AS OddsVidees, @N5 AS OpeningRenommees;
END
ELSE
BEGIN
    SELECT 'APERÇU SEULEMENT - passez @Appliquer à 1 pour corriger' AS Statut;
END;
