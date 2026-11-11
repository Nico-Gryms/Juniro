/* =============================================================================
   Juniro_Evo - Simulation ligue par ligue : ajout de la ligue aux résultats
   -----------------------------------------------------------------------------
   La simulation teste désormais chaque stratégie, avec toutes ses variantes, dans chaque
   ligue séparément. Ce script ajoute la ligue aux deux tables de résultats :

     sim.Runs      + LeagueName (NULL = anciennes simulations, toutes ligues confondues)
                   + IsPortfolio (1 = ligne « Portefeuille » : les variantes retenues de chaque
                                  ligue jouées ensemble)
     sim.RuleScan  + LeagueName, ajoutée à la clé primaire ('Toutes ligues' pour l'existant)

   Rejouable : ne fait que ce qui manque. Aucune donnée n'est supprimée.
   ENSUITE : réexécuter A3_VuesConsultation.sql et A4_ResultatsModelesEtSeuils.sql
   (leurs vues lisent ces nouvelles colonnes).
   Documentation : DOCUMENTATION.md (racine du projet), chapitre 5.2 et 8.4.
   ============================================================================= */
USE [Juniro_Evo];
GO

IF COL_LENGTH('sim.Runs', 'LeagueName') IS NULL
    ALTER TABLE sim.Runs ADD LeagueName NVARCHAR(100) NULL;
GO
IF COL_LENGTH('sim.Runs', 'IsPortfolio') IS NULL
    ALTER TABLE sim.Runs ADD IsPortfolio BIT NOT NULL CONSTRAINT DF_sim_Runs_IsPortfolio DEFAULT 0;
GO
IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = 'IX_sim_Runs_Selection')
    CREATE INDEX IX_sim_Runs_Selection ON sim.Runs (ModelRunId, IsSelected, Phase)
        INCLUDE (StrategyFamily, LeagueName, RoiPct, NbBets);
GO

IF COL_LENGTH('sim.RuleScan', 'LeagueName') IS NULL
BEGIN
    ALTER TABLE sim.RuleScan ADD LeagueName NVARCHAR(100) NOT NULL
        CONSTRAINT DF_sim_RuleScan_League DEFAULT N'Toutes ligues';
END;
GO

-- La clé primaire doit inclure la ligue (sinon deux ligues ne peuvent pas avoir la même règle)
IF NOT EXISTS (
    SELECT 1 FROM sys.index_columns ic
    JOIN sys.indexes i ON i.object_id = ic.object_id AND i.index_id = ic.index_id
    JOIN sys.columns c ON c.object_id = ic.object_id AND c.column_id = ic.column_id
    WHERE i.name = 'PK_sim_RuleScan' AND c.name = 'LeagueName')
BEGIN
    ALTER TABLE sim.RuleScan DROP CONSTRAINT PK_sim_RuleScan;
    ALTER TABLE sim.RuleScan ADD CONSTRAINT PK_sim_RuleScan
        PRIMARY KEY (ModelRunId, LeagueName, Phase, SignalName, Operator, Threshold, BetKey);
END;
GO

SELECT 'sim.Runs' AS TableName, c.name AS Colonne FROM sys.columns c
WHERE c.object_id = OBJECT_ID('sim.Runs') AND c.name IN ('LeagueName', 'IsPortfolio')
UNION ALL
SELECT 'sim.RuleScan', c.name FROM sys.columns c
WHERE c.object_id = OBJECT_ID('sim.RuleScan') AND c.name = 'LeagueName';
