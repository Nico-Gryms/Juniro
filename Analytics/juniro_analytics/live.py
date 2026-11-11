"""Exécution quotidienne : prédictions des matchs à venir et paris proposés.

Les modèles sont réentraînés une fois par mois (sur tous les matchs terminés avant le 1er
du mois, exactement comme dans le walk-forward), puis conservés dans le dossier models/.
Les variables, elles, sont recalculées chaque jour : la forme d'hier compte dès aujourd'hui.

Place dans la chaîne : décision du jour. Appelé par : __main__.cmd_daily.
Pour activer une stratégie : la copier, seuils figés et sans « grid », dans
config.json > simulation > live_strategies (option "leagues" pour la limiter à certaines ligues).
Documentation : DOCUMENTATION.md, chapitre 8.
"""
from __future__ import annotations

from pathlib import Path

import joblib
import pandas as pd

from .models import JuniroModels
from .simulate import candidate_bets, expand_grid, select_bets, stake_and_settle


def model_path(cfg: dict, cutoff: pd.Timestamp) -> Path:
    """Fichier des modèles du mois (models/juniro_AAAA-MM_<fenêtre>_<mkt|nomkt>.joblib)."""
    base = Path(cfg.get("_path", ".")).parent / cfg["live"]["models_dir"]
    base.mkdir(parents=True, exist_ok=True)
    mk = "mkt" if cfg["model"]["use_market_features"] else "nomkt"
    return base / f"juniro_{cutoff:%Y-%m}_{cfg['model']['train_window']}_{mk}.joblib"


def get_models(F: pd.DataFrame, feature_cols: list[str], cfg: dict, now: pd.Timestamp,
               retrain: bool = False, log=print) -> JuniroModels:
    """Modèles du mois : relus depuis models/ s'ils existent, sinon entraînés (données avant le 1er du mois)."""
    cutoff = now.normalize().replace(day=1)
    path = model_path(cfg, cutoff)
    if path.exists() and not retrain:
        models = joblib.load(path)
        # Modèles enregistrés avant l'ajout du recalage des buts : on les réentraîne.
        if hasattr(models, "goal_cal") and list(models.columns) == [c for c in models.columns if c in F.columns]:
            log(f"Modèles du mois chargés : {path.name}")
            return models
        log("Les variables ont changé depuis l'entraînement : réentraînement.")
    log(f"Entraînement des modèles (données avant le {cutoff:%d/%m/%Y})...")
    models = JuniroModels(cfg).fit(F, feature_cols, cutoff=cutoff)
    joblib.dump(models, path)
    log(f"  {models.n_train} matchs, modèles enregistrés : {path.name}")
    return models


def upcoming(F: pd.DataFrame, now: pd.Timestamp, days_ahead: int) -> pd.DataFrame:
    """Matchs non joués entre maintenant et aujourd'hui + days_ahead.
    Un match du jour déjà commencé (heure connue et passée) est écarté : on ne peut plus le jouer.
    Heure inconnue (00:00) : gardé toute la journée."""
    start = now.normalize()
    d = F["date"]
    started = (d <= now) & (d != d.dt.normalize())
    return F[(~F["finished"]) & (d >= start) & (d < start + pd.Timedelta(days=days_ahead)) & ~started]


def live_signals(preds: pd.DataFrame, matches: pd.DataFrame, odds_table: pd.DataFrame, cfg: dict) -> pd.DataFrame:
    """Applique les stratégies de config.json > simulation > live_strategies aux matchs à venir
    et renvoie les paris proposés (aucun si la liste est vide).
    """
    specs = cfg["simulation"].get("live_strategies") or []
    if not specs or preds.empty:
        return pd.DataFrame(columns=["match_id", "strategy", "key", "prob", "odd", "odd_label", "edge", "stake"])
    cands = candidate_bets(preds, matches, odds_table,
                           int(cfg["simulation"].get("skip_first_season_matches", 0) or 0))
    out = []
    for spec in specs:
        spec = {k: v for k, v in spec.items() if k != "grid"}
        variant = expand_grid(spec)[0]
        chosen = select_bets(cands, variant)
        if chosen.empty:
            continue
        chosen = chosen.assign(outcome="PUSH")   # issue inconnue : la bankroll ne doit pas bouger
        staked = stake_and_settle(chosen, variant, cfg["simulation"]["bankroll"])
        staked["strategy"] = spec["name"]
        out.append(staked)
    if not out:
        return pd.DataFrame(columns=["match_id", "strategy", "key", "prob", "odd", "odd_label", "edge", "stake"])
    return pd.concat(out, ignore_index=True)
