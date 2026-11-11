/* =============================================================================
   Juniro_Evo - Prédictions Plus/Moins 1,5 et 3,5 buts
   -----------------------------------------------------------------------------
   Jusqu'au 10/11/2026, les lignes 1,5 et 3,5 ne venaient que du modèle de score
   (colonnes Score_POver15 / Score_POver35). Le moteur entraîne désormais un classifieur
   dédié pour chacune, comme pour 2,5, et enregistre :
     Clf_POver15, Clf_POver35   probabilité du classifieur
     POver15, POver35           probabilité finale (mélange score + classifieur)
     Mkt_POver15, Mkt_POver35   probabilité du marché (cote de décision, marge retirée)
   Les anciennes prédictions gardent ces colonnes vides : les vues prennent alors Score_*.

   À exécuter UNE FOIS, puis rejouer A8_ExportSupabase.sql. Script rejouable.
   Tant qu'il n'est pas exécuté, le moteur fonctionne mais n'enregistre pas ces colonnes.
   Documentation : DOCUMENTATION.md (racine du projet), chapitre 7.1.
   ============================================================================= */
USE [Juniro_Evo];
GO

IF COL_LENGTH('ml.MatchPredictions', 'Clf_POver15') IS NULL
    ALTER TABLE ml.MatchPredictions ADD Clf_POver15 FLOAT NULL;
IF COL_LENGTH('ml.MatchPredictions', 'Clf_POver35') IS NULL
    ALTER TABLE ml.MatchPredictions ADD Clf_POver35 FLOAT NULL;
IF COL_LENGTH('ml.MatchPredictions', 'POver15') IS NULL
    ALTER TABLE ml.MatchPredictions ADD POver15 FLOAT NULL;
IF COL_LENGTH('ml.MatchPredictions', 'POver35') IS NULL
    ALTER TABLE ml.MatchPredictions ADD POver35 FLOAT NULL;
IF COL_LENGTH('ml.MatchPredictions', 'Mkt_POver15') IS NULL
    ALTER TABLE ml.MatchPredictions ADD Mkt_POver15 FLOAT NULL;
IF COL_LENGTH('ml.MatchPredictions', 'Mkt_POver35') IS NULL
    ALTER TABLE ml.MatchPredictions ADD Mkt_POver35 FLOAT NULL;
GO

-- Contrôle : les 6 colonnes doivent apparaître
SELECT name AS Colonne, TYPE_NAME(system_type_id) AS Type
FROM sys.columns
WHERE object_id = OBJECT_ID('ml.MatchPredictions')
  AND name IN ('Clf_POver15', 'Clf_POver35', 'POver15', 'POver35', 'Mkt_POver15', 'Mkt_POver35');

-- Après le prochain backtest : précision des trois lignes, modèle face au marché
-- (perte logarithmique, plus bas = mieux ; le bloc « Précision » de la commande backtest
--  affiche la même chose).
SELECT TOP 1 r.RunId, r.Label, r.CreatedAtUtc,
       JSON_VALUE(r.MetricsJson, '$.TEST.over15_modele_memes_matchs') AS Plus15_Modele,
       JSON_VALUE(r.MetricsJson, '$.TEST.over15_marche')              AS Plus15_Marche,
       JSON_VALUE(r.MetricsJson, '$.TEST.over25_modele_memes_matchs') AS Plus25_Modele,
       JSON_VALUE(r.MetricsJson, '$.TEST.over25_marche')              AS Plus25_Marche,
       JSON_VALUE(r.MetricsJson, '$.TEST.over35_modele_memes_matchs') AS Plus35_Modele,
       JSON_VALUE(r.MetricsJson, '$.TEST.over35_marche')              AS Plus35_Marche
FROM ml.ModelRuns r
WHERE r.RunType = 'BACKTEST'
ORDER BY r.RunId DESC;
