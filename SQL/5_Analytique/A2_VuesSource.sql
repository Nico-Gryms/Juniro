/* =============================================================================
   Juniro_Evo - Vues canoniques (schéma core)
   -----------------------------------------------------------------------------
   La seule porte d'entrée des calculs vers les données brutes. Si une table brute
   change un jour, on corrige ces vues et rien d'autre.

     core.vw_Matches     un match par ligne, statistiques domicile/extérieur à plat
     core.vw_TeamMatch   deux lignes par match, du point de vue de chaque équipe
                         (pour vos propres analyses : formes, classements, requêtes ad hoc)
     core.vw_OddsByLabel dernière cote de chaque relevé (OPENING, J-3 ... CLOSING)

   Script rejouable (CREATE OR ALTER). Lecture seule sur les données brutes.
   Documentation : DOCUMENTATION.md (racine du projet), chapitre 5.3.
   ============================================================================= */
USE [Juniro_Evo];
GO

CREATE OR ALTER VIEW core.vw_Matches
AS
SELECT
    m.MatchId,
    m.FlashscoreMatchKey,
    l.LeagueId,
    l.LeagueName,
    l.CountryName,
    l.TierLevel,
    s.SeasonId,
    REPLACE(s.SeasonName, '/', '-')  AS SeasonName,
    m.MatchDate,                                   -- heure de Paris
    m.RoundNumber,
    m.HomeTeamId,
    ht.TeamName                      AS HomeTeamName,
    m.AwayTeamId,
    awt.TeamName                     AS AwayTeamName,
    m.MatchStatus,
    m.HomeScoreFT, m.AwayScoreFT,
    m.HomeScoreHT, m.AwayScoreHT,
    hs.ExpectedGoals  AS Home_xG,          aws.ExpectedGoals  AS Away_xG,
    hs.PossessionPct  AS Home_Possession,  aws.PossessionPct  AS Away_Possession,
    hs.Shots          AS Home_Shots,       aws.Shots          AS Away_Shots,
    hs.ShotsOnTarget  AS Home_SoT,         aws.ShotsOnTarget  AS Away_SoT,
    hs.Corners        AS Home_Corners,     aws.Corners        AS Away_Corners
FROM dbo.Matches m
JOIN dbo.Seasons s  ON s.SeasonId = m.SeasonId
JOIN dbo.Leagues l  ON l.LeagueId = s.LeagueId
JOIN dbo.Teams   ht ON ht.TeamId  = m.HomeTeamId
JOIN dbo.Teams   awt ON awt.TeamId = m.AwayTeamId
OUTER APPLY (SELECT TOP (1) t.* FROM dbo.MatchTeamStats t
             WHERE t.MatchId = m.MatchId AND t.IsHome = 1) hs
OUTER APPLY (SELECT TOP (1) t.* FROM dbo.MatchTeamStats t
             WHERE t.MatchId = m.MatchId AND t.IsHome = 0) aws
WHERE l.IsActive = 1;
GO

CREATE OR ALTER VIEW core.vw_TeamMatch
AS
SELECT
    v.MatchId, v.LeagueId, v.LeagueName, v.SeasonId, v.SeasonName, v.MatchDate, v.RoundNumber,
    v.MatchStatus,
    x.IsHome,
    x.TeamId, x.TeamName, x.OpponentId, x.OpponentName,
    x.GoalsFor, x.GoalsAgainst,
    x.xGFor, x.xGAgainst,
    x.PossessionPct,
    x.ShotsFor, x.ShotsAgainst,
    x.SoTFor, x.SoTAgainst,
    x.CornersFor, x.CornersAgainst,
    CASE WHEN v.MatchStatus <> 'Finished' OR x.GoalsFor IS NULL THEN NULL
         WHEN x.GoalsFor > x.GoalsAgainst THEN 3
         WHEN x.GoalsFor = x.GoalsAgainst THEN 1 ELSE 0 END AS Points
FROM core.vw_Matches v
CROSS APPLY (VALUES
    (CAST(1 AS BIT), v.HomeTeamId, v.HomeTeamName, v.AwayTeamId, v.AwayTeamName,
     v.HomeScoreFT, v.AwayScoreFT, v.Home_xG, v.Away_xG, v.Home_Possession,
     v.Home_Shots, v.Away_Shots, v.Home_SoT, v.Away_SoT, v.Home_Corners, v.Away_Corners),
    (CAST(0 AS BIT), v.AwayTeamId, v.AwayTeamName, v.HomeTeamId, v.HomeTeamName,
     v.AwayScoreFT, v.HomeScoreFT, v.Away_xG, v.Home_xG, v.Away_Possession,
     v.Away_Shots, v.Home_Shots, v.Away_SoT, v.Home_SoT, v.Away_Corners, v.Home_Corners)
) x (IsHome, TeamId, TeamName, OpponentId, OpponentName,
     GoalsFor, GoalsAgainst, xGFor, xGAgainst, PossessionPct,
     ShotsFor, ShotsAgainst, SoTFor, SoTAgainst, CornersFor, CornersAgainst);
GO

/* Dernière cote connue pour chaque relevé. Les cotes « live » (pendant le match) sont exclues. */
CREATE OR ALTER VIEW core.vw_OddsByLabel
AS
SELECT MatchId, BookmakerId, MarketCode, SelectionCode, LineValue, SnapshotLabel, OddValue, QuoteDateUtc
FROM (
    SELECT q.MatchId, q.BookmakerId, q.MarketCode, q.SelectionCode, q.LineValue,
           q.SnapshotLabel, q.OddValue, q.QuoteDateUtc,
           ROW_NUMBER() OVER (PARTITION BY q.MatchId, q.BookmakerId, q.MarketCode, q.SelectionCode,
                                           q.LineValue, q.SnapshotLabel
                              ORDER BY q.QuoteDateUtc DESC, q.OddsQuoteId DESC) AS Rn
    FROM dbo.OddsQuotes q
    WHERE q.OddValue > 1
      AND ISNULL(q.IsLive, 0) = 0
      AND q.SnapshotLabel IS NOT NULL
) z
WHERE Rn = 1;
GO

-- Contrôle rapide
SELECT TOP (5) * FROM core.vw_Matches WHERE MatchStatus = 'Finished' ORDER BY MatchDate DESC;
SELECT SnapshotLabel, MarketCode, COUNT(*) AS NbCotes
FROM core.vw_OddsByLabel GROUP BY SnapshotLabel, MarketCode ORDER BY MarketCode, SnapshotLabel;
