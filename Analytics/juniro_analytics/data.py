"""Lecture des données brutes via les vues core.* et mise en forme commune.

Place dans la chaîne : étape 1 (lecture). Appelé par : __main__.load_data.
Pour ajouter une colonne venant de la base : l'ajouter à la vue core.vw_Matches
(SQL/5_Analytique/A2) puis à MATCH_COLUMNS ci-dessous. Documentation : DOCUMENTATION.md, chapitre 5.3.
"""
from __future__ import annotations

import re

import numpy as np
import pandas as pd

MATCH_COLUMNS = {
    "MatchId": "match_id", "LeagueId": "league_id", "LeagueName": "league_name",
    "TierLevel": "tier_level", "SeasonId": "season_id", "SeasonName": "season_name",
    "MatchDate": "date", "RoundNumber": "round", "HomeTeamId": "home_id", "HomeTeamName": "home_name",
    "AwayTeamId": "away_id", "AwayTeamName": "away_name", "MatchStatus": "status",
    "HomeScoreFT": "hg", "AwayScoreFT": "ag", "HomeScoreHT": "hg_ht", "AwayScoreHT": "ag_ht",
    "Home_xG": "h_xg", "Away_xG": "a_xg", "Home_Possession": "h_poss", "Away_Possession": "a_poss",
    "Home_Shots": "h_shots", "Away_Shots": "a_shots", "Home_SoT": "h_sot", "Away_SoT": "a_sot",
    "Home_Corners": "h_corners", "Away_Corners": "a_corners",
}

ODDS_COLUMNS = {
    "MatchId": "match_id", "BookmakerId": "bookmaker_id", "MarketCode": "market",
    "SelectionCode": "selection", "LineValue": "line", "SnapshotLabel": "label", "OddValue": "odd",
}


def load_from_db(db) -> tuple[pd.DataFrame, pd.DataFrame]:
    """Lit core.vw_Matches (matchs terminés et à venir) et core.vw_OddsByLabel (cotes par relevé)."""
    matches = db.read("SELECT * FROM core.vw_Matches WHERE MatchStatus IN ('Finished', 'Scheduled')")
    odds = db.read("SELECT MatchId, BookmakerId, MarketCode, SelectionCode, LineValue, SnapshotLabel, OddValue "
                   "FROM core.vw_OddsByLabel")
    return matches, odds


def prepare_matches(raw: pd.DataFrame, cfg: dict) -> pd.DataFrame:
    """Renomme les colonnes, convertit les types, déduit la division (TierLevel ou nom de ligue),
    marque les matchs terminés et trie par date.
    """
    m = raw.rename(columns=MATCH_COLUMNS)
    m = m[[c for c in MATCH_COLUMNS.values() if c in m.columns]].copy()
    m["date"] = pd.to_datetime(m["date"])
    for c in ["hg", "ag", "hg_ht", "ag_ht", "h_xg", "a_xg", "h_poss", "a_poss", "h_shots", "a_shots",
              "h_sot", "a_sot", "h_corners", "a_corners", "round", "tier_level"]:
        if c in m.columns:
            m[c] = pd.to_numeric(m[c], errors="coerce").astype(float)
    m["match_id"] = m["match_id"].astype("int64")
    for c in ["league_id", "season_id", "home_id", "away_id"]:
        m[c] = m[c].astype("int64")

    excl = set(cfg["data"].get("exclude_league_ids") or [])
    if excl:
        m = m[~m["league_id"].isin(excl)]

    # Niveau de division : TierLevel s'il est renseigné, sinon déduit du nom de la ligue.
    pattern = re.compile(cfg["data"]["second_tier_name_pattern"])
    by_name = m["league_name"].fillna("").map(lambda n: 2.0 if pattern.search(n) else 1.0)
    m["tier"] = m["tier_level"].where(m["tier_level"].isin([1.0, 2.0]), by_name).astype(int)

    m["finished"] = (m["status"] == "Finished") & m["hg"].notna() & m["ag"].notna()
    m["season_start"] = m["season_name"].str.slice(0, 4).astype(int)
    m = m.sort_values(["date", "match_id"]).reset_index(drop=True)
    return m


def prepare_odds(raw: pd.DataFrame) -> pd.DataFrame:
    """Convertit les cotes et donne à chaque sélection un nom unique (1X2_HOME, OU25_OVER...)."""
    o = raw.rename(columns=ODDS_COLUMNS)
    o = o[list(ODDS_COLUMNS.values())].copy()
    o["match_id"] = o["match_id"].astype("int64")
    o["odd"] = pd.to_numeric(o["odd"], errors="coerce").astype(float)
    o["line"] = pd.to_numeric(o["line"], errors="coerce").astype(float)
    o["key"] = [selection_key(mk, sel, ln) for mk, sel, ln in zip(o["market"], o["selection"], o["line"])]
    return o[o["key"].notna() & (o["odd"] > 1)].reset_index(drop=True)


def selection_key(market: str, selection: str, line) -> str | None:
    """Nom unique d'une sélection : 1X2_HOME, BTTS_YES, OU25_OVER, DC_1X, DNB_AWAY..."""
    market = (market or "").upper()
    selection = (selection or "").upper()
    if market == "1X2" and selection in ("HOME", "DRAW", "AWAY"):
        return f"1X2_{selection}"
    if market == "BTTS" and selection in ("YES", "NO"):
        return f"BTTS_{selection}"
    if market == "OVER_UNDER" and selection in ("OVER", "UNDER") and line is not None and not np.isnan(line):
        return f"OU{int(round(line * 10)):02d}_{selection}"
    if market in ("DOUBLE_CHANCE", "DC") and selection in ("1X", "X2", "12"):
        return f"DC_{selection}"
    if market == "DNB" and selection in ("HOME", "AWAY"):
        return f"DNB_{selection}"
    return None
