"""Réglage fin des modèles (« entraîner plus longtemps », mais de façon contrôlée).

Entraîner plus longtemps n'améliore pas automatiquement un modèle : au-delà d'un certain
nombre d'arbres, il apprend le bruit des saisons passées et se dégrade sur les matchs futurs.
On essaie donc plusieurs réglages (vitesse d'apprentissage plus lente, arbres plus ou moins
profonds, régularisation...) avec un arrêt anticipé qui laisse chaque modèle s'entraîner tant
qu'il progresse, et on garde celui qui prédit le mieux.

Garde-fou : le réglage est jugé UNIQUEMENT sur la saison de réglage (TUNING). La période TEST
n'est jamais regardée ici ; c'est elle qui dira, au backtest suivant, si le gain est réel.

Le meilleur réglage est écrit dans tuned.json (à côté de config.json) et utilisé
automatiquement par backtest et daily. Supprimer tuned.json revient au réglage d'origine.

Place dans la chaîne : réglage ponctuel des modèles (avant un backtest).
Appelé par : __main__.cmd_tune. Résultat : tuned.json. Documentation : DOCUMENTATION.md, chapitre 7.4.
"""
from __future__ import annotations

import copy
import json
import time
from pathlib import Path

import numpy as np
import pandas as pd

from .models import JuniroModels, log_loss_1x2, log_loss_binary

SPACE = {
    "learning_rate": [0.01, 0.02, 0.03, 0.05],
    "max_leaf_nodes": [7, 15, 31, 63],
    "min_samples_leaf": [20, 50, 100, 200],
    "l2_regularization": [0.0, 1.0, 5.0, 20.0],
    "max_features": [1.0, 0.7, 0.4],
    "max_bins": [63, 255],
}


def sample_trials(n: int, seed: int, base: dict) -> list[dict]:
    """Tire au hasard des réglages dans SPACE ; l'essai 0 est le réglage actuel (référence)."""
    rng = np.random.default_rng(seed)
    trials = [dict(base)]                                   # essai 0 = réglage actuel (référence)
    seen = {json.dumps(base, sort_keys=True)}
    while len(trials) < n:
        t = dict(base)
        for k, values in SPACE.items():
            t[k] = values[rng.integers(len(values))]
        t.update(max_iter=3000, early_stopping=True, n_iter_no_change=50)
        key = json.dumps(t, sort_keys=True)
        if key not in seen:
            seen.add(key)
            trials.append(t)
    return trials


def poisson_deviance(y: np.ndarray, mu: np.ndarray) -> float:
    """Erreur du modèle de buts (déviance de Poisson ; plus bas = meilleur)."""
    mu = np.clip(mu, 1e-6, None)
    term = np.where(y > 0, y * np.log(np.where(y > 0, y, 1) / mu), 0.0)
    return float(2 * np.mean(term - (y - mu)))


def run_tuning(F: pd.DataFrame, feature_cols: list[str], cfg: dict, trials: int = 16, every_months: int = 2,
               seed: int = 0, log=print) -> tuple[pd.DataFrame, dict]:
    """Évalue chaque réglage par walk-forward sur la seule période de réglage ; renvoie le tableau
    des essais et le meilleur réglage (buts et marchés séparément).
    """
    p = cfg["phases"]
    start, end = pd.Timestamp(p["tuning_start"]), pd.Timestamp(p["test_start"])
    fin = F[F["finished"]]
    blocks = pd.date_range(start.replace(day=1), end, freq=f"{every_months}MS")
    candidates = sample_trials(trials, seed, cfg["model"]["hgb"])
    log(f"Réglage : {len(candidates)} essais x {len(blocks)} réentraînements sur la saison de réglage "
        f"({start:%Y-%m} -> {end:%Y-%m}). Le premier essai est le réglage actuel.")

    results = []
    t0 = time.time()
    for k, params in enumerate(candidates):
        c = copy.deepcopy(cfg)
        c["model"]["hgb"] = params
        c["model"].pop("hgb_goals", None)
        c["model"].pop("hgb_clf", None)
        preds, iters = [], []
        for b in blocks:
            nxt = b + pd.DateOffset(months=every_months)
            test = fin[(fin["date"] >= b) & (fin["date"] < min(nxt, end))]
            if test.empty:
                continue
            try:
                mdl = JuniroModels(c).fit(F, feature_cols, cutoff=b)
            except ValueError:
                continue
            preds.append(mdl.predict(test).join(test[["hg", "ag", "y_result", "y_btts", "y_over25"]]))
            iters.append(mdl.iterations())
        if not preds:
            continue
        d = pd.concat(preds)
        it = pd.DataFrame(iters).mean()
        r = {"essai": k, **{key: params.get(key) for key in SPACE},
             "buts_deviance": (poisson_deviance(d["hg"].to_numpy(float), d["exp_home_goals"].to_numpy())
                               + poisson_deviance(d["ag"].to_numpy(float), d["exp_away_goals"].to_numpy())) / 2,
             "ll_1x2": log_loss_1x2(d, d["y_result"], ("clf_p_home", "clf_p_draw", "clf_p_away")),
             "ll_btts": log_loss_binary(d["clf_p_btts_yes"], d["y_btts"]),
             "ll_over25": log_loss_binary(d["clf_p_over25"], d["y_over25"]),
             "ll_1x2_final": log_loss_1x2(d, d["y_result"]),
             "arbres_buts": float((it["reg_home"] + it["reg_away"]) / 2),
             "arbres_marches": float((it["clf_1x2"] + it["clf_btts"] + it["clf_over25"]) / 3),
             "matchs": len(d)}
        r["ll_marches"] = r["ll_1x2"] + r["ll_btts"] + r["ll_over25"]
        results.append(r)
        log(f"  essai {k:>2}/{len(candidates) - 1}  buts {r['buts_deviance']:.5f}  marchés {r['ll_marches']:.5f}"
            f"  arbres {r['arbres_buts']:.0f}/{r['arbres_marches']:.0f}  "
            f"(vitesse {params['learning_rate']}, feuilles {params['max_leaf_nodes']}, "
            f"min {params['min_samples_leaf']}, l2 {params['l2_regularization']}, "
            f"variables {params.get('max_features', 1.0)})  — {time.time() - t0:,.0f} s")

    res = pd.DataFrame(results)
    if res.empty:
        raise RuntimeError("Aucun essai n'a pu être évalué (pas assez de données avant la saison de réglage ?).")
    best_goals = candidates[int(res.loc[res["buts_deviance"].idxmin(), "essai"])]
    best_clf = candidates[int(res.loc[res["ll_marches"].idxmin(), "essai"])]
    tuned = {"model": {"hgb_goals": best_goals, "hgb_clf": best_clf}}
    return res, tuned


def save_tuned(cfg: dict, tuned: dict, res: pd.DataFrame) -> Path:
    """Écrit tuned.json à côté de config.json."""
    path = Path(cfg["_path"]).parent / "tuned.json"
    base = res.iloc[0]
    out = {
        "_note": "Écrit par 'juniro_analytics tune'. Supprimer ce fichier = revenir au réglage de config.json.",
        "_gain_vs_reglage_actuel": {
            "buts_deviance": round(float(res["buts_deviance"].min() - base["buts_deviance"]), 5),
            "ll_marches": round(float(res["ll_marches"].min() - base["ll_marches"]), 5),
        },
        **tuned,
    }
    path.write_text(json.dumps(out, indent=2, ensure_ascii=False), encoding="utf-8")
    return path


def render(res: pd.DataFrame) -> str:
    """Résumé « actuel -> meilleur » pour le modèle de buts et les modèles de marché."""
    base = res.iloc[0]
    bg = res.loc[res["buts_deviance"].idxmin()]
    bc = res.loc[res["ll_marches"].idxmin()]
    return "\n".join([
        "Résultat du réglage (saison de réglage uniquement ; plus bas = meilleur)",
        f"  Modèle de buts    actuel {base['buts_deviance']:.5f}  ->  meilleur {bg['buts_deviance']:.5f}"
        f"  (essai {int(bg['essai'])}, {bg['arbres_buts']:.0f} arbres en moyenne)",
        f"  Modèles marchés   actuel {base['ll_marches']:.5f}  ->  meilleur {bc['ll_marches']:.5f}"
        f"  (essai {int(bc['essai'])}, {bc['arbres_marches']:.0f} arbres en moyenne)",
        "  Un gain de moins de 0,001 est du bruit : dans ce cas, le réglage actuel est déjà bon.",
    ])
