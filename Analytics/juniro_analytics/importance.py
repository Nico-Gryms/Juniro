"""Quelles données comptent vraiment ? Importance des variables, par famille et une par une.

Principe : on entraîne les modèles UNE fois, sur tout ce qui précède la période de TEST
(config.json > phases > test_start), puis on mesure leur perte logarithmique sur les matchs
de TEST (1X2, BTTS, Plus de 2,5). Ensuite :

  1. Retrait d'une famille (ablation) : on réentraîne SANS la famille (Elo, xG, cotes,
     confrontations directes...) et l'on mesure la perte. C'est la mesure la plus honnête :
     si la perte ne monte pas, le modèle n'a pas besoin de cette famille (les autres variables
     portent déjà la même information).
  2. Permutation : sans réentraîner, on mélange au hasard les valeurs d'une famille (ou d'une
     variable) entre les matchs de TEST. Ce que la perte perd = ce que le modèle en tirait.
     Rapide, mais une variable très corrélée à d'autres paraît moins importante qu'elle n'est.
  3. Marché : perte des cotes elles-mêmes, la référence à battre.

Lecture : écart = perte sans la famille - perte avec tout. Positif = la famille aide.
Ordre de grandeur : 0,001 sur le 1X2, c'est l'écart actuel entre le modèle et le marché.

Place dans la chaîne : diagnostic, hors chaîne quotidienne (commande « importance »).
Appelé par : __main__.cmd_importance. Documentation : DOCUMENTATION.md, chapitre 7.6.
"""
from __future__ import annotations

import re
import time

import numpy as np
import pandas as pd

from .models import JuniroModels, log_loss_1x2, log_loss_binary

# Familles de variables, reconnues par leur nom. La PREMIÈRE famille qui correspond l'emporte.
FAMILIES = [
    ("Cotes (marché)", r"^mkt_"),
    ("Confrontations directes", r"^h2h_"),
    ("Elo", r"elo"),
    ("Classements", r"rank_|gap_leader|n_teams"),
    ("xG et points attendus", r"xg|xatt|xdef|xpts|xexp"),
    ("Tirs", r"shots|sot"),
    ("Corners", r"corners"),
    ("Possession", r"poss"),
    ("Buts et résultats", r"_gf|_ga|_pts|_gd|att_strength|def_strength|naive_exp|btts|over25|_cs|_fts|luck|momentum"),
    ("Contexte (repos, ligue, saison)", r"rest_days|ctx_|promoted|relegated|new_in_data|^lg_|played|tier|_n$"),
]


def family_of(col: str) -> str:
    """Famille d'une variable d'après son nom (voir FAMILIES) ; « Autres » sinon."""
    for name, pat in FAMILIES:
        if re.search(pat, col):
            return name
    return "Autres"


def _losses(models: JuniroModels, T: pd.DataFrame) -> dict:
    """Pertes logarithmiques des probabilités finales sur les matchs T."""
    p = models.predict(T)
    return {"1x2": log_loss_1x2(p, T["y_result"]),
            "btts": log_loss_binary(p["p_btts_yes"], T["y_btts"]),
            "over25": log_loss_binary(p["p_over25"], T["y_over25"])}


def _market_losses(T: pd.DataFrame) -> dict:
    """Pertes des probabilités du marché (sans marge) sur les matchs qui ont des cotes."""
    out = {}
    ok = T[["mkt_p_home", "mkt_p_draw", "mkt_p_away"]].notna().all(axis=1) if "mkt_p_home" in T else None
    if ok is not None and ok.any():
        out["1x2"] = log_loss_1x2(T[ok], T.loc[ok, "y_result"], cols=("mkt_p_home", "mkt_p_draw", "mkt_p_away"))
    for key, col, y in (("btts", "mkt_p_btts", "y_btts"), ("over25", "mkt_p_over25", "y_over25")):
        if col in T and T[col].notna().any():
            k = T[col].notna()
            out[key] = log_loss_binary(T.loc[k, col], T.loc[k, y])
    return out


def run_importance(F: pd.DataFrame, feature_cols: list[str], cfg: dict, repeats: int = 3,
                   ablation: bool = True, top: int = 25, log=print) -> dict:
    """Mesure l'importance des familles de variables (ablation + permutation) et des variables
    une par une (permutation). Renvoie un dictionnaire de tableaux (pandas) prêt pour render()."""
    cutoff = pd.Timestamp(cfg["phases"]["test_start"])
    end = pd.Timestamp(cfg["phases"]["holdout_start"])
    T = F[F["finished"] & (F["date"] >= cutoff) & (F["date"] < end)].copy()
    if T.empty:
        raise ValueError("Aucun match terminé dans la période de TEST.")
    t0 = time.time()
    base_models = JuniroModels(cfg).fit(F, feature_cols, cutoff=cutoff)
    base = _losses(base_models, T)
    log(f"Modèles entraînés jusqu'au {cutoff:%Y-%m-%d} ({base_models.n_train} matchs), "
        f"mesurés sur {len(T)} matchs de TEST — {time.time() - t0:.0f} s")

    used = base_models.columns
    fam = pd.Series({c: family_of(c) for c in used})
    rng = np.random.default_rng(0)

    def permuted_loss(cols: list[str]) -> dict:
        acc = {k: 0.0 for k in base}
        for _ in range(repeats):
            Tp = T.copy()
            perm = rng.permutation(len(Tp))
            Tp[cols] = Tp[cols].to_numpy()[perm]          # mêmes lignes mélangées pour toute la famille
            for k, v in _losses(base_models, Tp).items():
                acc[k] += v / repeats
        return acc

    rows = []
    for name, cols in fam.groupby(fam).groups.items():
        cols = list(cols)
        t = time.time()
        row = {"famille": name, "nb_variables": len(cols)}
        pl = permuted_loss(cols)
        for k in base:
            row[f"perm_{k}"] = pl[k] - base[k]
        if ablation:
            drop = set(cols)
            keep = [c for c in feature_cols if c not in drop]
            try:
                ab = _losses(JuniroModels(cfg).fit(F, keep, cutoff=cutoff), T)
                for k in base:
                    row[f"ablation_{k}"] = ab[k] - base[k]
            except ValueError as e:
                log(f"  {name} : ablation impossible ({e})")
        rows.append(row)
        log(f"  {name:<34} {len(cols):>4} variables — {time.time() - t:.0f} s")
    families = pd.DataFrame(rows).sort_values("ablation_1x2" if ablation else "perm_1x2", ascending=False)

    t = time.time()
    single = []
    for c in used:
        pl = permuted_loss([c])
        single.append({"variable": c, "famille": fam[c], **{f"perm_{k}": pl[k] - base[k] for k in base}})
    single = pd.DataFrame(single)
    single["perm_total"] = single[[f"perm_{k}" for k in base]].sum(axis=1)
    single = single.sort_values("perm_total", ascending=False)
    log(f"  Permutation variable par variable ({len(used)} variables) — {time.time() - t:.0f} s")

    return {"base": base, "market": _market_losses(T), "families": families, "single": single,
            "n_test": len(T), "cutoff": cutoff, "top": top, "repeats": repeats}


def render(res: dict) -> str:
    """Rapport lisible : référence, familles (ablation et permutation), meilleures variables."""
    b, mk = res["base"], res["market"]
    f = lambda v: "      ·" if v is None or pd.isna(v) else f"{v:+.4f}"
    lines = [f"Importance des variables — entraînement jusqu'au {res['cutoff']:%Y-%m-%d}, "
             f"{res['n_test']} matchs de TEST",
             "Perte logarithmique (plus bas = meilleur)      1X2      BTTS     +2,5",
             f"  modèle complet                          {b['1x2']:.5f}  {b['btts']:.5f}  {b['over25']:.5f}",
             "  marché (cotes sans marge)               " + "  ".join(
                 f"{mk[k]:.5f}" if k in mk else "      ·" for k in ("1x2", "btts", "over25")),
             "",
             "Familles — écart de perte quand on RETIRE la famille (réentraînement) / quand on la MÉLANGE",
             "Positif = la famille aide le modèle. 0,001 ~ l'écart actuel entre le modèle et le marché.",
             f"    {'Famille':<34}{'var.':>5}   {'retrait 1X2':>11} {'BTTS':>8} {'+2,5':>8}   "
             f"{'mélange 1X2':>11} {'BTTS':>8} {'+2,5':>8}"]
    for _, r in res["families"].iterrows():
        lines.append(f"    {r['famille'][:34]:<34}{int(r['nb_variables']):>5}   "
                     f"{f(r.get('ablation_1x2')):>11} {f(r.get('ablation_btts')):>8} {f(r.get('ablation_over25')):>8}   "
                     f"{f(r['perm_1x2']):>11} {f(r['perm_btts']):>8} {f(r['perm_over25']):>8}")
    lines += ["", f"Les {res['top']} variables les plus utiles une par une (mélange, somme des trois marchés) :",
              f"    {'Variable':<34}{'Famille':<26}{'1X2':>9}{'BTTS':>9}{'+2,5':>9}"]
    for _, r in res["single"].head(res["top"]).iterrows():
        lines.append(f"    {r['variable'][:34]:<34}{r['famille'][:25]:<26}"
                     f"{f(r['perm_1x2']):>9}{f(r['perm_btts']):>9}{f(r['perm_over25']):>9}")
    useless = int((res["single"]["perm_total"] <= 0).sum())
    lines.append(f"\n{useless} variables sur {len(res['single'])} n'apportent rien une fois mélangées "
                 "(souvent parce qu'une variable voisine porte la même information).")
    lines.append("Mélange : une variable très corrélée à d'autres paraît inutile ; fiez-vous au retrait de famille.")
    return "\n".join(lines)
