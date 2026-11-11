"""Écriture et relecture des résultats dans les schémas feat / ml / sim.

Place dans la chaîne : écriture des résultats (feat.*, ml.*, sim.*).
Appelé par : __main__. Les tables sont créées par SQL/5_Analytique/A1, A4 et A5.
"""
from __future__ import annotations

import datetime as dt
import json

import numpy as np
import pandas as pd

from . import __version__

PRED_COLUMNS = {
    "exp_home_goals": "ExpHomeGoals", "exp_away_goals": "ExpAwayGoals", "exp_total_goals": "ExpTotalGoals",
    "most_likely_score": "MostLikelyScore",
    "score_p_home": "Score_PHome", "score_p_draw": "Score_PDraw", "score_p_away": "Score_PAway",
    "score_p_btts_yes": "Score_PBttsYes", "score_p_over15": "Score_POver15",
    "score_p_over25": "Score_POver25", "score_p_over35": "Score_POver35",
    "clf_p_home": "Clf_PHome", "clf_p_draw": "Clf_PDraw", "clf_p_away": "Clf_PAway",
    "clf_p_btts_yes": "Clf_PBttsYes", "clf_p_over25": "Clf_POver25",
    "p_home": "PHome", "p_draw": "PDraw", "p_away": "PAway", "p_btts_yes": "PBttsYes", "p_over25": "POver25",
    "mkt_p_home": "Mkt_PHome", "mkt_p_draw": "Mkt_PDraw", "mkt_p_away": "Mkt_PAway",
    "mkt_p_btts": "Mkt_PBttsYes", "mkt_p_over25": "Mkt_POver25",
    # Lignes 1,5 et 3,5 (colonnes ajoutées par SQL/5_Analytique/A9_LignesButs.sql ; ignorées tant
    # que le script n'a pas été exécuté)
    "clf_p_over15": "Clf_POver15", "clf_p_over35": "Clf_POver35",
    "p_over15": "POver15", "p_over35": "POver35",
    "mkt_p_over15": "Mkt_POver15", "mkt_p_over35": "Mkt_POver35",
}


def save_features(db, F: pd.DataFrame, feature_cols: list[str]):
    """Réécrit entièrement feat.MatchFeatures (une ligne par match, toutes les variables)."""
    keep = ["date", "league_id", "league_name", "season_id", "season_name", "round", "home_id", "home_name",
            "away_id", "away_name", "status", "hg", "ag"]
    cols = keep + [c for c in feature_cols if c not in keep]
    out = F[cols].reset_index()
    out = out.rename(columns={"match_id": "MatchId", "date": "MatchDate"})
    out["finished"] = F["finished"].to_numpy().astype(int)
    db.replace_table("feat", "MatchFeatures", out, key=["MatchId"])
    db.commit()
    return len(out)


def save_model_run(db, run_type: str, cfg: dict, preds: pd.DataFrame, F: pd.DataFrame,
                   metrics: dict | None = None, label: str | None = None) -> int:
    """Crée une ligne ml.ModelRuns (BACKTEST ou LIVE) et y rattache les prédictions (ml.MatchPredictions)."""
    dates = F.loc[preds.index, "date"]
    run_id = db.insert_returning_id("ml.ModelRuns", {
        "RunType": run_type, "Label": label, "TrainWindow": cfg["model"]["train_window"],
        "UseMarketFeatures": bool(cfg["model"]["use_market_features"]),
        "PeriodFrom": dates.min().date() if len(dates) else None,
        "PeriodTo": dates.max().date() if len(dates) else None,
        "NbMatchs": int(len(preds)),
        "ConfigJson": json.dumps({k: v for k, v in cfg.items() if k in ("model", "features", "elo", "phases", "data")},
                                 ensure_ascii=False),
        "MetricsJson": json.dumps(metrics, ensure_ascii=False) if metrics else None,
        "CodeVersion": __version__,
    }, "RunId")

    mkt = [c for c in ("mkt_p_home", "mkt_p_draw", "mkt_p_away", "mkt_p_btts", "mkt_p_over25",
                       "mkt_p_over15", "mkt_p_over35") if c in F.columns]
    p = preds.join(F[mkt], rsuffix="_f")
    rows = pd.DataFrame({"RunId": run_id, "MatchId": p.index.astype("int64")})
    rows["TrainedUntil"] = pd.to_datetime(p["trained_until"]).dt.date.to_numpy()
    existing = db.columns("ml", "MatchPredictions")
    for src, dst in PRED_COLUMNS.items():
        if existing and dst not in existing:
            continue        # colonne pas encore créée en base (script SQL A9 non exécuté)
        rows[dst] = pd.to_numeric(p[src], errors="coerce").to_numpy() if src in p.columns else np.nan
    db.append("ml.MatchPredictions", rows)
    db.commit()
    return run_id


def load_predictions(db, run_id: int) -> pd.DataFrame:
    """Relit les prédictions d'un backtest (pour simulate / scan sans réentraîner)."""
    df = db.read("SELECT * FROM ml.MatchPredictions WHERE RunId = ?", (run_id,))
    if df.empty:
        raise ValueError(f"Aucune prédiction pour RunId={run_id}")
    inv = {v: k for k, v in PRED_COLUMNS.items()}
    df = df.rename(columns=inv).rename(columns={"MatchId": "match_id", "TrainedUntil": "trained_until"})
    df = df.set_index("match_id")
    for c in df.columns:
        if c not in ("most_likely_score", "trained_until", "RunId"):
            df[c] = pd.to_numeric(df[c], errors="coerce").astype(float)
    return df.drop(columns=["RunId"] + [c for c in df.columns if c.startswith("mkt_")])


def _run_row(model_run_id, batch_label, r) -> dict:
    """Ligne sim.Runs correspondant à un résultat de simulation."""
    from .simulate import spec_json
    return {
        "ModelRunId": model_run_id, "BatchLabel": batch_label, "StrategyName": r["name"][:200],
        "StrategyFamily": r["family"][:100], "StrategyType": r["type"], "StrategyJson": spec_json(r["spec"]),
        "LeagueName": (r.get("league") or None), "IsPortfolio": bool(r.get("portfolio", False)),
        "Phase": r["phase"],
        "PeriodFrom": _d(r.get("period_from")), "PeriodTo": _d(r.get("period_to")),
        "NbBets": int(r["nb_bets"] or 0), "Staked": r.get("staked"), "Profit": r.get("profit"),
        "RoiPct": r.get("roi_pct"), "RoiCiLowPct": r.get("roi_ci_low_pct"), "RoiCiHighPct": r.get("roi_ci_high_pct"),
        "HitRatePct": r.get("hit_rate_pct"), "AvgOdd": r.get("avg_odd"), "AvgEdgePct": r.get("avg_edge_pct"),
        "AvgClvPct": r.get("avg_clv_pct"), "BeatClosingPct": r.get("beat_closing_pct"),
        "MaxDrawdown": r.get("max_drawdown"), "FinalBankroll": r.get("final_bankroll"),
        "NbStrategiesTested": int(r["family_size"]), "IsSelected": bool(r["selected"]),
    }


def save_simulation(db, model_run_id: int, res: pd.DataFrame, bets: dict, batch_label: str | None = None) -> int:
    """Toutes les variantes de toutes les ligues sont enregistrées (en bloc) ; les variantes retenues
    le sont une par une, avec le détail de leurs paris."""
    res = res.copy()
    for c in ("league", "portfolio"):
        if c not in res:
            res[c] = None if c == "league" else False
    others = res[~res["selected"].astype(bool)]
    if len(others):
        db.append("sim.Runs", pd.DataFrame([_run_row(model_run_id, batch_label, r) for _, r in others.iterrows()]))
    n_runs = len(others)
    for i, r in res[res["selected"].astype(bool)].iterrows():
        sim_id = db.insert_returning_id("sim.Runs", _run_row(model_run_id, batch_label, r), "SimRunId")
        n_runs += 1
        # Détail des paris des variantes retenues (pas du portefeuille : ce sont les mêmes paris)
        if not bool(r.get("portfolio", False)) and int(r["nb_bets"] or 0) > 0 and i in bets:
            b = bets[i]
            rows = pd.DataFrame({
                "SimRunId": sim_id, "MatchId": b["match_id"].astype("int64").to_numpy(),
                "MatchDate": b["date"].to_numpy(),
                "Market": b["key"].str.rsplit("_", n=1).str[0].to_numpy(),
                "Selection": b["key"].str.rsplit("_", n=1).str[1].to_numpy(),
                "LineValue": line_of(b["key"]),
                "Probability": b["prob"].to_numpy(), "OddTaken": b["odd"].to_numpy(),
                "OddLabel": b["odd_label"].to_numpy(), "OddClose": b["odd_close"].to_numpy(),
                "EdgePct": 100 * b["edge"].to_numpy(), "Stake": b["stake"].to_numpy(),
                "Outcome": b["outcome"].to_numpy(), "Profit": b["profit"].to_numpy(),
                "Bankroll": b["bankroll"].to_numpy(),
                "ClvPct": 100 * (b["odd"] / b["odd_close"] - 1).to_numpy(),
            })
            db.append("sim.Bets", rows)
    db.commit()
    return n_runs


SCAN_COLUMNS = {
    "league": "LeagueName", "phase": "Phase", "signal": "SignalName", "op": "Operator", "threshold": "Threshold", "bet": "BetKey",
    "n": "NbMatchs", "wins": "NbWin", "pushes": "NbPush", "hit_rate_pct": "HitRatePct",
    "break_even_odd": "BreakEvenOdd", "n_odds": "NbWithOdds", "avg_odd": "AvgOdd",
    "avg_implied_pct": "AvgImpliedPct", "profit": "Profit", "roi_pct": "RoiPct",
    "roi_ci_low_pct": "RoiCiLowPct", "roi_ci_high_pct": "RoiCiHighPct", "avg_clv_pct": "AvgClvPct",
}


def save_scan(db, model_run_id: int, scan: pd.DataFrame) -> int:
    """Remplace le balayage des seuils de ce passage de modèles."""
    db.execute("DELETE FROM sim.RuleScan WHERE ModelRunId = ?", (model_run_id,))
    if scan.empty:
        db.commit()
        return 0
    out = pd.DataFrame({"ModelRunId": model_run_id}, index=scan.index)
    for src, dst in SCAN_COLUMNS.items():
        out[dst] = scan[src] if src in scan else np.nan
    if "league" not in scan:
        out["LeagueName"] = "Toutes ligues"
    for c in ("NbMatchs", "NbWin", "NbPush", "NbWithOdds"):
        out[c] = out[c].astype("int64")
    n = db.append("sim.RuleScan", out.reset_index(drop=True))
    db.commit()
    return n


def save_live_signals(db, model_run_id: int, signals: pd.DataFrame):
    """Enregistre les paris proposés du jour dans sim.LiveSignals."""
    if signals.empty:
        return 0
    rows = pd.DataFrame({
        "ModelRunId": model_run_id, "MatchId": signals["match_id"].astype("int64").to_numpy(),
        "StrategyName": signals["strategy"].str.slice(0, 200).to_numpy(),
        "Market": signals["key"].str.rsplit("_", n=1).str[0].to_numpy(),
        "Selection": signals["key"].str.rsplit("_", n=1).str[1].to_numpy(),
        "LineValue": line_of(signals["key"]),
        "Probability": signals["prob"].to_numpy(), "OddSeen": signals["odd"].to_numpy(),
        "OddLabel": signals["odd_label"].to_numpy(), "EdgePct": 100 * signals["edge"].to_numpy(),
        "StakeUnits": signals["stake"].to_numpy(),
    })
    db.append("sim.LiveSignals", rows)
    db.commit()
    return len(rows)


def line_of(keys: pd.Series) -> np.ndarray:
    """OU15_OVER -> 1.5, OU25_UNDER -> 2.5 ; NaN pour les autres marchés."""
    num = keys.str.extract(r"^OU(\d)(\d)_")
    return (pd.to_numeric(num[0]) + pd.to_numeric(num[1]) / 10).to_numpy(dtype=float)


def _d(v):
    """Date seule (sans heure) ou None."""
    if v is None or (isinstance(v, float) and np.isnan(v)) or v is pd.NaT:
        return None
    if isinstance(v, (pd.Timestamp, dt.datetime)):
        return v.date()
    return v
