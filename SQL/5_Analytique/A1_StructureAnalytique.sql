/* =============================================================================
   Juniro_Evo - Structure analytique : schémas et tables de résultats
   -----------------------------------------------------------------------------
   Crée l'espace de travail des calculs, séparé des données brutes (dbo) :

     core  vues canoniques sur les données brutes (script A2)
     feat  variables calculées au coup d'envoi       (écrites par Analytics, reconstruites à chaque passage)
     ml    exécutions de modèles et prédictions      (tables ci-dessous)
     sim   simulations de paris et signaux du jour   (tables ci-dessous)
     app   vues de consultation                      (script A3)

   Aucune donnée brute n'est modifiée. Script rejouable : il ne crée que ce qui manque.
   Documentation : DOCUMENTATION.md (racine du projet), chapitre 5.2.
   ============================================================================= */
USE [Juniro_Evo];
GO

IF SCHEMA_ID('core') IS NULL EXEC('CREATE SCHEMA core');
IF SCHEMA_ID('feat') IS NULL EXEC('CREATE SCHEMA feat');
IF SCHEMA_ID('ml')   IS NULL EXEC('CREATE SCHEMA ml');
IF SCHEMA_ID('sim')  IS NULL EXEC('CREATE SCHEMA sim');
IF SCHEMA_ID('app')  IS NULL EXEC('CREATE SCHEMA app');
GO

/* -----------------------------------------------------------------------------
   ml.ModelRuns : une ligne par exécution des modèles.
     BACKTEST = walk-forward historique (réentraînement mensuel, prédictions hors échantillon)
     LIVE     = prédictions des matchs à venir, lancées chaque jour par le service
   ----------------------------------------------------------------------------- */
IF OBJECT_ID('ml.ModelRuns') IS NULL
CREATE TABLE ml.ModelRuns (
    RunId              INT IDENTITY(1,1) NOT NULL CONSTRAINT PK_ml_ModelRuns PRIMARY KEY,
    RunType            NVARCHAR(20)  NOT NULL CONSTRAINT CK_ml_ModelRuns_Type CHECK (RunType IN ('BACKTEST', 'LIVE')),
    Label              NVARCHAR(100) NULL,
    TrainWindow        NVARCHAR(20)  NOT NULL,      -- all / 1y / 2y / decay
    UseMarketFeatures  BIT           NOT NULL,
    PeriodFrom         DATE          NULL,          -- premiers matchs prédits
    PeriodTo           DATE          NULL,          -- derniers matchs prédits
    NbMatchs           INT           NULL,
    ConfigJson         NVARCHAR(MAX) NULL,
    MetricsJson        NVARCHAR(MAX) NULL,          -- perte log modèle vs marché, par phase
    CodeVersion        NVARCHAR(20)  NULL,
    CreatedAtUtc       DATETIME2(0)  NOT NULL CONSTRAINT DF_ml_ModelRuns_Created DEFAULT SYSUTCDATETIME()
);
GO

/* -----------------------------------------------------------------------------
   ml.MatchPredictions : toutes les sorties des modèles pour un match, sur une ligne.
     Exp*        buts attendus (modèle de score Poisson)
     Score_*     probabilités déduites de la matrice des scores (cohérentes entre marchés)
     Clf_*       probabilités des classifieurs dédiés (un par marché)
     P*          probabilité finale = mélange des deux, utilisée par les stratégies
     Mkt_*       probabilités du marché sans marge, à la cote de décision, pour comparaison
   ----------------------------------------------------------------------------- */
IF OBJECT_ID('ml.MatchPredictions') IS NULL
CREATE TABLE ml.MatchPredictions (
    RunId            INT          NOT NULL,
    MatchId          BIGINT       NOT NULL,
    TrainedUntil     DATE         NOT NULL,   -- dernier jour de données vu par le modèle
    ExpHomeGoals     FLOAT        NULL,
    ExpAwayGoals     FLOAT        NULL,
    ExpTotalGoals    FLOAT        NULL,
    MostLikelyScore  NVARCHAR(7)  NULL,
    Score_PHome      FLOAT NULL, Score_PDraw   FLOAT NULL, Score_PAway   FLOAT NULL,
    Score_PBttsYes   FLOAT NULL, Score_POver15 FLOAT NULL, Score_POver25 FLOAT NULL, Score_POver35 FLOAT NULL,
    Clf_PHome        FLOAT NULL, Clf_PDraw     FLOAT NULL, Clf_PAway     FLOAT NULL,
    Clf_PBttsYes     FLOAT NULL, Clf_POver25   FLOAT NULL,
    PHome            FLOAT NULL, PDraw         FLOAT NULL, PAway         FLOAT NULL,
    PBttsYes         FLOAT NULL, POver25       FLOAT NULL,
    Mkt_PHome        FLOAT NULL, Mkt_PDraw     FLOAT NULL, Mkt_PAway     FLOAT NULL,
    Mkt_PBttsYes     FLOAT NULL, Mkt_POver25   FLOAT NULL,
    -- Lignes 1,5 et 3,5 (10/11/2026 ; sur une base existante : A9_LignesButs.sql)
    Clf_POver15      FLOAT NULL, Clf_POver35   FLOAT NULL,
    POver15          FLOAT NULL, POver35       FLOAT NULL,
    Mkt_POver15      FLOAT NULL, Mkt_POver35   FLOAT NULL,
    CONSTRAINT PK_ml_MatchPredictions PRIMARY KEY (RunId, MatchId),
    CONSTRAINT FK_ml_MatchPredictions_Run FOREIGN KEY (RunId) REFERENCES ml.ModelRuns (RunId) ON DELETE CASCADE
);
GO

IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = 'IX_ml_MatchPredictions_Match')
    CREATE INDEX IX_ml_MatchPredictions_Match ON ml.MatchPredictions (MatchId) INCLUDE (RunId);
GO

/* -----------------------------------------------------------------------------
   sim.Runs : une ligne par stratégie et par phase testée.
   TOUTES les stratégies essayées sont enregistrées, y compris les mauvaises : c'est ce qui
   permet de savoir combien d'essais ont été faits avant de trouver « la bonne ».
     Phase : TUNING (réglage des seuils) / TEST (évaluation) / HOLDOUT (réserve, une seule fois)
     IsSelected = 1 : stratégie retenue sur la phase TUNING, et donc la seule dont le
                      résultat en TEST a une valeur de preuve.
   ----------------------------------------------------------------------------- */
IF OBJECT_ID('sim.Runs') IS NULL
CREATE TABLE sim.Runs (
    SimRunId            INT IDENTITY(1,1) NOT NULL CONSTRAINT PK_sim_Runs PRIMARY KEY,
    ModelRunId          INT           NOT NULL,
    BatchLabel          NVARCHAR(100) NULL,
    StrategyName        NVARCHAR(200) NOT NULL,
    StrategyFamily      NVARCHAR(100) NOT NULL,
    StrategyType        NVARCHAR(20)  NOT NULL,     -- value / combo
    StrategyJson        NVARCHAR(MAX) NULL,
    LeagueName          NVARCHAR(100) NULL,         -- NULL = toutes ligues confondues
    IsPortfolio         BIT           NOT NULL CONSTRAINT DF_sim_Runs_IsPortfolio DEFAULT 0,
    Phase               NVARCHAR(20)  NOT NULL,
    PeriodFrom          DATE          NULL,
    PeriodTo            DATE          NULL,
    NbBets              INT           NOT NULL,
    Staked              FLOAT         NULL,
    Profit              FLOAT         NULL,
    RoiPct              FLOAT         NULL,
    RoiCiLowPct         FLOAT         NULL,         -- intervalle de confiance 90 % (bootstrap)
    RoiCiHighPct        FLOAT         NULL,
    HitRatePct          FLOAT         NULL,
    AvgOdd              FLOAT         NULL,
    AvgEdgePct          FLOAT         NULL,
    AvgClvPct           FLOAT         NULL,         -- gain moyen de cote vs clôture
    BeatClosingPct      FLOAT         NULL,         -- % de paris pris au-dessus de la clôture
    MaxDrawdown         FLOAT         NULL,         -- en unités de mise
    FinalBankroll       FLOAT         NULL,
    NbStrategiesTested  INT           NULL,         -- taille de la famille essayée
    IsSelected          BIT           NOT NULL CONSTRAINT DF_sim_Runs_Selected DEFAULT 0,
    CreatedAtUtc        DATETIME2(0)  NOT NULL CONSTRAINT DF_sim_Runs_Created DEFAULT SYSUTCDATETIME(),
    CONSTRAINT FK_sim_Runs_ModelRun FOREIGN KEY (ModelRunId) REFERENCES ml.ModelRuns (RunId) ON DELETE CASCADE
);
GO

/* sim.Bets : le détail des paris simulés (stratégies explicites et stratégies retenues). */
IF OBJECT_ID('sim.Bets') IS NULL
CREATE TABLE sim.Bets (
    BetId        BIGINT IDENTITY(1,1) NOT NULL CONSTRAINT PK_sim_Bets PRIMARY KEY,
    SimRunId     INT           NOT NULL,
    MatchId      BIGINT        NOT NULL,
    MatchDate    DATETIME2(0)  NOT NULL,
    Market       NVARCHAR(20)  NOT NULL,
    Selection    NVARCHAR(10)  NOT NULL,
    LineValue    DECIMAL(4,2)  NULL,
    Probability  FLOAT         NOT NULL,
    OddTaken     FLOAT         NOT NULL,
    OddLabel     NVARCHAR(10)  NULL,
    OddClose     FLOAT         NULL,
    EdgePct      FLOAT         NULL,
    Stake        FLOAT         NOT NULL,
    Outcome      NVARCHAR(5)   NOT NULL,           -- WIN / LOSE / PUSH
    Profit       FLOAT         NOT NULL,
    Bankroll     FLOAT         NULL,
    ClvPct       FLOAT         NULL,
    CONSTRAINT FK_sim_Bets_Run FOREIGN KEY (SimRunId) REFERENCES sim.Runs (SimRunId) ON DELETE CASCADE
);
GO

IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = 'IX_sim_Bets_Run')
    CREATE INDEX IX_sim_Bets_Run ON sim.Bets (SimRunId, MatchDate);
GO

/* sim.LiveSignals : paris proposés pour les matchs à venir par les stratégies retenues. */
IF OBJECT_ID('sim.LiveSignals') IS NULL
CREATE TABLE sim.LiveSignals (
    SignalId      BIGINT IDENTITY(1,1) NOT NULL CONSTRAINT PK_sim_LiveSignals PRIMARY KEY,
    ModelRunId    INT           NOT NULL,
    MatchId       BIGINT        NOT NULL,
    StrategyName  NVARCHAR(200) NOT NULL,
    Market        NVARCHAR(20)  NOT NULL,
    Selection     NVARCHAR(10)  NOT NULL,
    LineValue     DECIMAL(4,2)  NULL,
    Probability   FLOAT         NOT NULL,
    OddSeen       FLOAT         NOT NULL,
    OddLabel      NVARCHAR(10)  NULL,
    EdgePct       FLOAT         NULL,
    StakeUnits    FLOAT         NULL,
    CreatedAtUtc  DATETIME2(0)  NOT NULL CONSTRAINT DF_sim_LiveSignals_Created DEFAULT SYSUTCDATETIME(),
    CONSTRAINT FK_sim_LiveSignals_Run FOREIGN KEY (ModelRunId) REFERENCES ml.ModelRuns (RunId) ON DELETE CASCADE
);
GO

SELECT s.name AS SchemaName, o.name AS Objet, o.type_desc
FROM sys.objects o
JOIN sys.schemas s ON s.schema_id = o.schema_id
WHERE s.name IN ('core', 'feat', 'ml', 'sim', 'app') AND o.type IN ('U', 'V')
ORDER BY s.name, o.name;
