"""Carte des biais de prix : ligue x pari x tranche de cote, saison par saison.

Au lieu de milliers de seuils, une question simple et lisible : « parier systématiquement sur
X dans telle ligue quand la cote est entre a et b, qu'est-ce que ça a rapporté ? ». Exemple :
victoire à domicile en LaLiga, cote entre 1,25 et 1,40. Aucun modèle n'intervient : seulement
la cote de décision et le résultat.

Pour chaque case (ligue, pari, tranche de cote) :
  - nombre de paris, taux de réussite comparé à la probabilité implicite, ROI et IC90 ;
  - ROI saison par saison et nombre de saisons positives ;
  - ROI RAMENÉ VERS LA MOYENNE des autres ligues (« rétrécissement », méthode empirique de
    DerSimonian-Laird) : une case avec peu de paris est tirée vers le comportement général de
    ce pari dans cette tranche de cote. Un vrai biais résiste ; un coup de chance s'efface ;
  - régularité : les tranches de cote voisines vont-elles dans le même sens ?

Le haut du rapport montre le biais favori / outsider « toutes ligues » (ROI par tranche de cote
pour chaque pari) ; le bas, les cases les plus solides après rétrécissement. Un fichier CSV
complet est écrit dans le dossier rapports/ (à ouvrir dans Excel).

Place dans la chaîne : diagnostic (commande « biais »). Appelé par : __main__.cmd_bias.
Réglages : config.json > bias. Documentation : DOCUMENTATION.md, chapitre 8.7.
"""
from __future__ import annotations

from pathlib import Path

import numpy as np
import pandas as pd

from .scan import BET_KEYS
from .simulate import _outcomes, early_season_mask

DEFAULT_BANDS = [1.01, 1.25, 1.40, 1.55, 1.70, 1.85, 2.00, 2.25, 2.60, 3.00, 3.60, 4.50, 6.00, 51.0]
MAIN_BETS = ["1X2_HOME", "1X2_DRAW", "1X2_AWAY", "OU25_OVER", "OU25_UNDER", "BTTS_YES", "BTTS_NO"]


def _bets(matches: pd.DataFrame, odds_table: pd.DataFrame, cfg: dict, include_holdout: bool) -> pd.DataFrame:
    """Un pari d'une unité sur chaque sélection de chaque match joué : cote, issue, gain."""
    m = matches[matches["finished"]].set_index("match_id")
    end = pd.Timestamp(cfg["phases"]["holdout_start"])
    if not include_holdout:
        m = m[m["date"] < end]
    skip = int(cfg["simulation"].get("skip_first_season_matches", 0) or 0)
    if skip:
        early = early_season_mask(matches, skip)
        m = m[~m.index.isin(early[early].index)]
    outs = _outcomes(m["hg"], m["ag"])
    ot = odds_table.reindex(m.index)
    frames = []
    for key in BET_KEYS:
        col = f"dec_{key}"
        if col not in ot or key not in outs:
            continue
        f = pd.DataFrame({"league": m["league_name"], "season": m["season_name"], "bet": key,
                          "odd": ot[col], "outcome": outs[key]}, index=m.index)
        frames.append(f[f["odd"].notna() & f["outcome"].notna()])
    b = pd.concat(frames)
    b["profit"] = np.where(b["outcome"] == "WIN", b["odd"] - 1, np.where(b["outcome"] == "PUSH", 0.0, -1.0))
    b["win"] = (b["outcome"] == "WIN").astype(float)
    b["settled"] = (b["outcome"] != "PUSH").astype(float)
    b["implied"] = 1 / b["odd"]
    return b


def _stats(g: pd.DataFrame) -> pd.Series:
    """ROI, erreur type, réussite et probabilité implicite d'un groupe de paris."""
    n = len(g)
    roi = g["profit"].mean()
    se = g["profit"].std(ddof=1) / np.sqrt(n) if n > 1 else np.nan
    return pd.Series({"n": n, "roi": 100 * roi, "se": 100 * se,
                      "hit": 100 * g["win"].sum() / max(g["settled"].sum(), 1),
                      "implied": 100 * g["implied"].mean(), "avg_odd": g["odd"].mean()})


def run_bias(matches: pd.DataFrame, odds_table: pd.DataFrame, cfg: dict, include_holdout: bool = False) -> dict:
    """Calcule la carte des biais. Renvoie {'cells', 'global', 'seasons'} (DataFrames)."""
    bc = cfg.get("bias") or {}
    bands = bc.get("bands", DEFAULT_BANDS)
    min_bets = int(bc.get("min_bets", 50))
    min_season = int(bc.get("min_season_bets", 10))
    b = _bets(matches, odds_table, cfg, include_holdout)
    labels = [f"{lo:.2f}-{hi:.2f}" for lo, hi in zip(bands[:-1], bands[1:])]
    b["band"] = pd.cut(b["odd"], bands, labels=labels, right=False)
    b = b[b["band"].notna()]

    keys = ["league", "bet", "band"]
    cells = b.groupby(keys, observed=True).apply(_stats, include_groups=False).reset_index()
    glob = b.groupby(["bet", "band"], observed=True).apply(_stats, include_groups=False).reset_index()
    seas = (b.groupby(keys + ["season"], observed=True)
            .agg(n=("profit", "size"), roi=("profit", "mean")).reset_index())
    seas["roi"] *= 100
    s_ok = seas[seas["n"] >= min_season]
    sp = s_ok.groupby(keys, observed=True).agg(seasons=("roi", "size"),
                                                seasons_pos=("roi", lambda r: int((r > 0).sum())),
                                                worst_season=("roi", "min")).reset_index()
    cells = cells.merge(sp, on=keys, how="left")
    n_avail = seas.groupby("league")["season"].nunique()
    cells["seasons_avail"] = cells["league"].map(n_avail)

    # Rétrécissement vers la moyenne du même pari et de la même tranche dans les autres ligues
    cells = cells[cells["n"] >= min_bets].copy()
    shr = []
    for (bet, band), g in cells.groupby(["bet", "band"], observed=True):
        v = g["se"] ** 2
        w = 1 / v.replace(0, np.nan)
        mu = float((w * g["roi"]).sum() / w.sum()) if w.notna().any() else float(g["roi"].mean())
        if len(g) > 1:
            q = float((w * (g["roi"] - mu) ** 2).sum())
            c = float(w.sum() - (w ** 2).sum() / w.sum())
            tau2 = max(0.0, (q - (len(g) - 1)) / c) if c > 0 else 0.0
        else:
            tau2 = 0.0
        k = tau2 / (tau2 + v)
        var_mu = float(1 / w.sum()) if w.notna().any() else float(v.mean())   # incertitude sur la moyenne
        shr.append(pd.DataFrame({"shrunk": mu + k * (g["roi"] - mu),
                                 "shrunk_se": np.sqrt(k * v + (1 - k) ** 2 * var_mu),
                                 "pool_mean": mu, "tau": np.sqrt(tau2)}, index=g.index))
    cells = cells.join(pd.concat(shr)) if shr else cells.assign(shrunk=np.nan, shrunk_se=np.nan,
                                                                  pool_mean=np.nan, tau=np.nan)

    # Régularité : signe du ROI dans les tranches voisines (même ligue, même pari)
    order = {lab: i for i, lab in enumerate(labels)}
    cells["band_i"] = cells["band"].map(order).astype(int)
    idx = cells.set_index(["league", "bet", "band_i"])["roi"]
    prev = idx.reindex(list(zip(cells["league"], cells["bet"], cells["band_i"] - 1))).to_numpy()
    nxt = idx.reindex(list(zip(cells["league"], cells["bet"], cells["band_i"] + 1))).to_numpy()
    cells["neighbours_pos"] = [int(p > 0) + int(q > 0) for p, q in zip(prev, nxt)]      # NaN > 0 vaut False
    cells["neighbours_n"] = [int(not np.isnan(p)) + int(not np.isnan(q)) for p, q in zip(prev, nxt)]
    cells["shrunk_low"] = cells["shrunk"] - 1.645 * cells["shrunk_se"]
    cells["solid"] = ((cells["shrunk_low"] > 0)
                      & (cells["seasons_pos"] == cells["seasons_avail"])
                      & (cells["neighbours_pos"] == cells["neighbours_n"]) & (cells["neighbours_n"] > 0))
    return {"cells": cells.drop(columns="band_i"), "global": glob, "seasons": seas, "labels": labels,
            "n_bets": len(b), "n_matches": b.index.nunique()}


def render(res: dict, top: int = 20) -> str:
    """Biais favori / outsider toutes ligues, puis cases les plus solides après rétrécissement."""
    g, c, labels = res["global"], res["cells"], res["labels"]
    lines = [f"Carte des biais de prix — {res['n_matches']} matchs, {res['n_bets']} paris possibles "
             "(cote de décision, aucun modèle)",
             "",
             "Toutes ligues : ROI en % de chaque pari selon la tranche de cote (nombre de paris entre parenthèses)",
             "Le biais favori / outsider classique : ROI moins négatif pour les petites cotes que pour les grosses."]
    head = f"    {'Pari':<11}" + "".join(f"{lab:>14}" for lab in labels)
    lines.append(head)
    for bet in MAIN_BETS:
        gb = g[g["bet"] == bet].set_index("band")
        if gb.empty:
            continue
        cellstr = []
        for lab in labels:
            if lab in gb.index and gb.loc[lab, "n"] >= 30:
                cellstr.append(f"{gb.loc[lab, 'roi']:+.1f} ({int(gb.loc[lab, 'n'])})")
            else:
                cellstr.append("·")
        lines.append(f"    {bet:<11}" + "".join(f"{x:>14}" for x in cellstr))

    n_pos = int((c["shrunk"] > 0).sum())
    lines += ["",
              f"Cases ligue x pari x tranche : {len(c)} avec assez de paris, {int((c['roi'] > 0).sum())} au ROI positif, "
              f"{n_pos} encore positives après rétrécissement, {int(c['solid'].sum())} solides "
              "(borne basse > 0 après rétrécissement, chaque saison positive, tranches voisines positives).",
              "",
              f"  Les {top} cases à la meilleure borne basse après rétrécissement :",
              f"    {'Ligue':<16}{'Pari':<11}{'Cote':>12}{'Paris':>7}{'Réussite':>10}{'Implicite':>11}"
              f"{'ROI':>8}{'Ramené':>8}{'±':>6}{'Saisons +':>11}{'Voisines +':>12}{'Solide':>8}"]
    for _, r in c.sort_values("shrunk_low", ascending=False).head(top).iterrows():
        sp = f"{int(r['seasons_pos']) if pd.notna(r['seasons_pos']) else 0}/{int(r['seasons_avail'])}"
        lines.append(f"    {r['league'][:16]:<16}{r['bet']:<11}{r['band']:>12}{int(r['n']):>7}{r['hit']:>9.1f}%"
                     f"{r['implied']:>10.1f}%{r['roi']:>+8.1f}{r['shrunk']:>+8.1f}{1.645 * r['shrunk_se']:>6.1f}"
                     f"{sp:>11}{str(r['neighbours_pos']) + '/' + str(r['neighbours_n']):>12}"
                     f"{'OUI' if r['solid'] else '':>8}")
    lines.append("\nRamené = ROI tiré vers la moyenne des autres ligues pour ce pari et cette tranche de cote ;"
                 " ± = demi-largeur de l'IC90 après rétrécissement.")
    return "\n".join(lines)


def save_csv(res: dict, cfg: dict, now: pd.Timestamp) -> Path:
    """Écrit la carte complète (une ligne par case) dans rapports/biais_AAAAMMJJ.csv (séparateur ;)."""
    out_dir = Path(cfg.get("_path", ".")).parent / "rapports"
    out_dir.mkdir(parents=True, exist_ok=True)
    path = out_dir / f"biais_{now:%Y%m%d_%H%M}.csv"
    res["cells"].to_csv(path, sep=";", decimal=",", index=False, encoding="utf-8-sig")
    return path
