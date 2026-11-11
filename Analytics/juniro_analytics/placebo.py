"""Placebo : combien de « stratégies gagnantes » le hasard fabrique-t-il à lui seul ?

On rejoue TOUT le balayage des seuils (toutes les ligues, tous les signaux, tous les seuils, tous
les paris) sur des résultats TIRÉS AU HASARD, selon un monde où les cotes sont exactement justes :

  - pour chaque match, on cherche les buts attendus (lambda domicile, lambda extérieur) d'une loi
    de Poisson qui reproduisent les probabilités du marché (1X2 sans marge, et Plus de 2,5 quand
    la cote existe) ;
  - on tire un score au hasard selon ces lambdas ;
  - on règle tous les paris avec les VRAIES cotes et ce faux score.

Dans ce monde, aucune règle n'a d'avantage : chacune perd la marge du bookmaker en moyenne. Tout
ce qui « passe » les filtres est donc du hasard pur. On répète l'opération `reps` fois et l'on
compare au vrai balayage : nombre de règles qui passent chaque filtre, meilleure règle, nombre de
survivantes dans une même ligue.

Lecture : si le vrai balayage n'a pas plus de survivantes que la plupart des tirages, ses
« stratégies » ne valent rien. S'il en a nettement plus (proportion de tirages au moins aussi
bons proche de 0), il y a probablement quelque chose.

Place dans la chaîne : diagnostic, hors chaîne quotidienne (commande « placebo »).
Appelé par : __main__.cmd_placebo. Documentation : DOCUMENTATION.md, chapitre 8.6.
"""
from __future__ import annotations

import time

import numpy as np
import pandas as pd
from scipy.spatial import cKDTree
from scipy.stats import poisson

from . import scan as scan_mod

MAX_GOALS = 10


def _poisson_grid(step: float = 0.02, lo: float = 0.05, hi: float = 4.5):
    """Table des probabilités (domicile, extérieur, plus de 2,5) pour une grille de lambdas."""
    lam = np.arange(lo, hi + step / 2, step)
    k = np.arange(MAX_GOALS + 1)
    pk = poisson.pmf(k[None, :], lam[:, None])                       # lambda x buts
    LH, LA = np.meshgrid(np.arange(len(lam)), np.arange(len(lam)), indexing="ij")
    LH, LA = LH.ravel(), LA.ravel()
    joint = pk[LH][:, :, None] * pk[LA][:, None, :]                  # grille x buts dom x buts ext
    i, j = np.meshgrid(k, k, indexing="ij")
    ph = (joint * (i > j)).sum(axis=(1, 2))
    pa = (joint * (i < j)).sum(axis=(1, 2))
    po = (joint * ((i + j) > 2.5)).sum(axis=(1, 2))
    return lam[LH], lam[LA], ph, pa, po


def market_lambdas(odds_table: pd.DataFrame, fallback: pd.DataFrame | None = None) -> pd.DataFrame:
    """Buts attendus (lh, la) qui reproduisent les probabilités du marché, match par match.
    Avec la cote Plus/Moins 2,5 : ajustement sur 1X2 et +2,5 ; sinon sur le 1X2 seul.
    Sans cote 1X2 : buts attendus du modèle (fallback, colonnes exp_home_goals / exp_away_goals)."""
    lh_g, la_g, ph, pa, po = _poisson_grid()
    t = odds_table
    PH, PA = t.get("dec_p_1X2_HOME"), t.get("dec_p_1X2_AWAY")
    PO = t.get("dec_p_OU25_OVER")
    out = pd.DataFrame(index=t.index, columns=["lh", "la"], dtype=float)
    if PH is not None and PA is not None:
        has = PH.notna() & PA.notna()
        has3 = has & (PO.notna() if PO is not None else False)
        if has3.any():
            tree3 = cKDTree(np.column_stack([ph, pa, po]))
            _, idx = tree3.query(np.column_stack([PH[has3], PA[has3], PO[has3]]))
            out.loc[has3, "lh"], out.loc[has3, "la"] = lh_g[idx], la_g[idx]
        has2 = has & ~has3
        if has2.any():
            tree2 = cKDTree(np.column_stack([ph, pa]))
            _, idx = tree2.query(np.column_stack([PH[has2], PA[has2]]))
            out.loc[has2, "lh"], out.loc[has2, "la"] = lh_g[idx], la_g[idx]
    if fallback is not None:
        miss = out["lh"].isna()
        fb = fallback.reindex(out.index)
        out.loc[miss, "lh"] = fb.loc[miss, "exp_home_goals"]
        out.loc[miss, "la"] = fb.loc[miss, "exp_away_goals"]
    return out


def fake_scores(lams: pd.DataFrame, rng: np.random.Generator) -> pd.DataFrame:
    """Un score tiré au hasard pour chaque match (Poisson indépendants)."""
    ok = lams["lh"].notna() & lams["la"].notna()
    hg = pd.Series(np.nan, index=lams.index)
    ag = pd.Series(np.nan, index=lams.index)
    hg[ok] = rng.poisson(lams.loc[ok, "lh"].to_numpy(float))
    ag[ok] = rng.poisson(lams.loc[ok, "la"].to_numpy(float))
    return pd.DataFrame({"hg": hg, "ag": ag})


def _summary(res: pd.DataFrame, cfg: dict) -> dict:
    """Chiffres clés d'un balayage : entonnoir des filtres, meilleure règle, concentration par ligue."""
    cands, funnel, near = scan_mod.candidates(res, cfg)
    out = {label: n for label, n in funnel}
    t = res[res["phase"] == "TUNING"].set_index(scan_mod.KEY)
    s = res[res["phase"] == "TEST"].set_index(scan_mod.KEY)
    both = t[["roi_pct"]].join(s[["roi_pct", "roi_ci_low_pct"]], lsuffix="_tun", rsuffix="_test", how="inner")
    solid = both[(both["roi_pct_tun"] > 0) & (both["roi_ci_low_pct"] > 0)]
    per_lg = solid.groupby(level="league").size()
    out["meilleure borne basse IC90 (test)"] = float(both["roi_ci_low_pct"].max()) if len(both) else np.nan
    out["max de survivantes (étape 2) dans une ligue"] = int(per_lg.max()) if len(per_lg) else 0
    out["_per_league"] = per_lg.to_dict()
    return out


def run_placebo(preds: pd.DataFrame, matches: pd.DataFrame, odds_table: pd.DataFrame, cfg: dict,
                features: pd.DataFrame | None = None, reps: int = 10, seed: int = 0, log=print) -> dict:
    """Balayage réel + `reps` balayages sur résultats tirés au hasard (marché juste)."""
    phases = ("TUNING", "TEST")
    t0 = time.time()
    real = scan_mod.run_scan(preds, matches, odds_table, cfg, phases=phases, features=features)
    real_sum = _summary(real, cfg)
    log(f"Balayage réel : {len(real)} lignes — {time.time() - t0:.0f} s")
    lams = market_lambdas(odds_table, preds)
    rng = np.random.default_rng(seed)
    sims = []
    for r in range(reps):
        t = time.time()
        res = scan_mod.run_scan(preds, matches, odds_table, cfg, phases=phases, features=features,
                                scores=fake_scores(lams, rng))
        sims.append(_summary(res, cfg))
        log(f"  tirage {r + 1}/{reps} — {time.time() - t:.0f} s")
    return {"real": real_sum, "sims": sims, "reps": reps}


def render(res: dict) -> str:
    """Comparaison du vrai balayage avec les tirages « marché juste »."""
    real, sims = res["real"], res["sims"]
    keys = [k for k in real if not k.startswith("_")]
    lines = [f"Placebo — {res['reps']} balayages sur résultats tirés au hasard (cotes supposées justes)",
             "Si le vrai balayage ne fait pas mieux que la plupart des tirages, ses stratégies sont du hasard.",
             "",
             f"    {'':<62}{'réel':>8}{'hasard moy.':>13}{'hasard 90 %':>13}{'hasard max':>12}{'tirages >= réel':>17}"]
    for k in keys:
        vals = np.array([s.get(k, np.nan) for s in sims], dtype=float)
        rv = real[k]
        share = np.mean(vals >= rv) if len(vals) else np.nan
        fmt = (lambda v: f"{v:+.1f}") if k.startswith("meilleure") else (lambda v: f"{v:.0f}")
        lines.append(f"    {k[:62]:<62}{fmt(rv):>8}{fmt(np.nanmean(vals)):>13}"
                     f"{fmt(np.nanpercentile(vals, 90)):>13}{fmt(np.nanmax(vals)):>12}{100 * share:>15.0f} %")
    lines += ["", "Survivantes de l'étape 2 (positives en réglage, borne basse IC90 du test > 0), par ligue :",
              f"    {'Ligue':<18}{'réel':>6}{'hasard moy.':>13}{'hasard max':>12}"]
    leagues = sorted(set(real["_per_league"]) | {lg for s in sims for lg in s["_per_league"]})
    for lg in leagues:
        v = np.array([s["_per_league"].get(lg, 0) for s in sims], dtype=float)
        lines.append(f"    {lg[:18]:<18}{real['_per_league'].get(lg, 0):>6}{v.mean():>13.1f}{v.max():>12.0f}")
    lines.append("\nLecture de « tirages >= réel » : 0 à 10 % = le vrai résultat sort nettement du hasard ;"
                 " au-delà de 20 % = rien de plus que le hasard.")
    return "\n".join(lines)
