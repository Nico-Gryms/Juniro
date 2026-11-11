/* =============================================================================
   Juniro_Evo - Suppression des cotes mal attribuées par le nouveau scraper
   -----------------------------------------------------------------------------
   Flashscore a changé le format des adresses de match. Les anciennes ancres
   "#/comparaison-des-cotes/..." retombent sur la page résumé, dont l'encart de
   cotes affiche toujours le 1X2. Résultat, pour toutes les cotes écrites par le
   nouveau scraper (service depuis le 25/10 + backfills) :
     - BTTS, Double chance, DNB : valeurs 1X2 enregistrées à la place ;
     - Plus/Moins : toujours vide.
   Le 1X2 lui-même est correct (c'est bien l'encart 1X2 qui était lu) : il est conservé.

   L'historique de l'ancien pipeline (dernier relevé : 20/05/2026) n'est pas concerné :
   seules les lignes postérieures à @Depuis sont touchées.

   Après correction, les matchs concernés redeviennent "incomplets" et seront repris
   automatiquement par le backfill corrigé. Les relevés J-3 à J-1 des matchs déjà
   joués sont en revanche perdus : ils étaient faux et ne peuvent plus être relus.

   ÉTAPE 1 : lancer tel quel et contrôler l'aperçu.
   ÉTAPE 2 : passer @Appliquer à 1 et relancer.
   ============================================================================= */
USE [Juniro_Evo];
GO

DECLARE @Appliquer BIT       = 0;               -- 0 = aperçu, 1 = correction
DECLARE @Depuis    DATETIME2 = '2026-10-01';     -- début du nouveau scraper (UTC)
DECLARE @Migration NVARCHAR(100) = N'2026-10-30_CotesMalAttribuees';

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

-- -----------------------------------------------------------------------------
-- APERÇU 1 : preuve du problème. Pour un même match, la "double chance 1X"
-- enregistrée est égale à la cote domicile du 1X2 : impossible pour de vraies cotes.
-- -----------------------------------------------------------------------------
SELECT TOP 20 'Preuve' AS Bloc, o.MatchId, o.DateAdded,
       o.OddHome AS [1X2_Dom], o.OddDraw AS [1X2_Nul], o.OddAway AS [1X2_Ext],
       o.OddHomeDraw AS DC_1X, o.OddAwayDraw AS DC_X2,
       o.OddBTTS AS BTTS_Oui, o.OddBTNS AS BTTS_Non,
       o.OddHomeDNB AS DNB_Dom, o.OddAwayDNB AS DNB_Ext
FROM dbo.Odds o
WHERE o.DateAdded >= @Depuis
ORDER BY o.DateAdded DESC;

-- -----------------------------------------------------------------------------
-- APERÇU 2 : volume concerné
-- -----------------------------------------------------------------------------
SELECT 'OddsQuotes à supprimer' AS Bloc, q.MarketCode, COUNT(*) AS Nb,
       COUNT(DISTINCT q.MatchId) AS Matchs
FROM dbo.OddsQuotes q
WHERE q.QuoteDateUtc >= @Depuis
  AND q.MarketCode IN ('BTTS', 'DOUBLE_CHANCE', 'DNB', 'OVER_UNDER')
GROUP BY q.MarketCode;

SELECT 'Odds à nettoyer' AS Bloc, COUNT(*) AS Lignes, COUNT(DISTINCT o.MatchId) AS Matchs
FROM dbo.Odds o
WHERE o.DateAdded >= @Depuis;

-- -----------------------------------------------------------------------------
-- CORRECTION
-- -----------------------------------------------------------------------------
IF @Appliquer = 1
BEGIN
    BEGIN TRANSACTION;

    DECLARE @Touches TABLE (MatchId BIGINT PRIMARY KEY);
    INSERT INTO @Touches (MatchId)
    SELECT DISTINCT q.MatchId FROM dbo.OddsQuotes q
    WHERE q.QuoteDateUtc >= @Depuis
      AND q.MarketCode IN ('BTTS', 'DOUBLE_CHANCE', 'DNB', 'OVER_UNDER');

    DELETE FROM dbo.OddsQuotes
    WHERE QuoteDateUtc >= @Depuis
      AND MarketCode IN ('BTTS', 'DOUBLE_CHANCE', 'DNB', 'OVER_UNDER');
    DECLARE @NbQuotes INT = @@ROWCOUNT;

    -- Le 1X2 est conservé ; tous les autres marchés sont remis à vide.
    UPDATE dbo.Odds
    SET OddBTTS = NULL, OddBTNS = NULL,
        OddHomeDraw = NULL, OddAwayDraw = NULL,
        OddHomeDNB = NULL, OddAwayDNB = NULL,
        OddOv15 = NULL, OddUn15 = NULL,
        OddOv25 = NULL, OddUn25 = NULL,
        OddOv35 = NULL, OddUn35 = NULL
    WHERE DateAdded >= @Depuis;
    DECLARE @NbOdds INT = @@ROWCOUNT;

    -- Si une ligne supprimée portait le drapeau de clôture, le reporter sur le dernier
    -- relevé restant de la même série.
    ;WITH Series AS (
        SELECT q.OddsQuoteId,
               ROW_NUMBER() OVER (PARTITION BY q.MatchId, q.BookmakerId, q.MarketCode, q.SelectionCode, q.LineValue
                                  ORDER BY q.QuoteDateUtc DESC, q.OddsQuoteId DESC) AS Rn,
               MAX(CAST(q.IsClosing AS INT)) OVER (PARTITION BY q.MatchId, q.BookmakerId, q.MarketCode, q.SelectionCode, q.LineValue) AS AUneCloture
        FROM dbo.OddsQuotes q
        JOIN @Touches t ON t.MatchId = q.MatchId
    )
    UPDATE q SET q.IsClosing = 1
    FROM dbo.OddsQuotes q
    JOIN Series s ON s.OddsQuoteId = q.OddsQuoteId
    WHERE s.Rn = 1 AND s.AUneCloture = 0;

    INSERT INTO dbo.SchemaMigrations (MigrationName, RowsAffected)
    VALUES (@Migration, @NbQuotes + @NbOdds);

    COMMIT TRANSACTION;

    SELECT 'CORRECTION APPLIQUÉE' AS Statut, @NbQuotes AS QuotesSupprimees, @NbOdds AS LignesOddsNettoyees;
END
ELSE
BEGIN
    SELECT 'APERÇU SEULEMENT - passez @Appliquer à 1 pour corriger' AS Statut;
END;
