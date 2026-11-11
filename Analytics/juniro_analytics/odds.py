"""Cotes : choix du relevé, retrait de la marge, variables de marché.

Règle anti-fuite : la cote utilisée pour décider (et comme variable) est la plus récente
DISPONIBLE AU MOMENT DU PARI. En simulation historique, on prend le relevé du jour (DAY,
pris par le service à 03h00), puis J-1, J-2, J-3. La clôture ne sert qu'à mesurer le CLV,
jamais à décider.

Place dans la chaîne : étape 2 (variables de marché) et 5 (prix des paris).
Appelé par : features.build_match_features, __main__.load_run.
Pour changer la cote de décision : config.json > data > decision_labels.
Documentation : DOCUMENTATION.md, chapitre 6.6.
"""
from __future__ import annotations

import numpy as np
import pandas as pd

GROUPS = {
    "1X2": ["1X2_HOME", "1X2_DRAW", "1X2_AWAY"],
    "BTTS": ["BTTS_YES", "BTTS_NO"],
    "OU15": ["OU15_OVER", "OU15_UNDER"],
    "OU25": ["OU25_OVER", "OU25_UNDER"],
    "OU35": ["OU35_OVER", "OU35_UNDER"],
    # Le scraper ne relève que 1X et X2 (pas 12) : exiger les trois rendait ce marché
    # toujours « incomplet », et les paris double chance n'étaient jamais simulés.
    "DC": ["DC_1X", "DC_X2"],
    "DNB": ["DNB_HOME", "DNB_AWAY"],
}
ALL_KEYS = [k for keys in GROUPS.values() for k in keys]


def group_of(key: str) -> str:
    """Marché d'une sélection : '1X2_HOME' -> '1X2'."""
    return key.rsplit("_", 1)[0]


def choose_bookmaker(odds: pd.DataFrame, preferred: list[int]) -> pd.DataFrame:
    """Un seul bookmaker par match : le premier de la liste préférée, sinon le plus petit identifiant."""
    if odds.empty:
        return odds
    pref = {b: i for i, b in enumerate(preferred or [])}
    rank = odds["bookmaker_id"].map(lambda b: pref.get(int(b), 1000 + int(b)))
    best = rank.groupby(odds["match_id"]).transform("min")
    return odds[rank == best]


def pick_snapshot(odds: pd.DataFrame, labels: list[str], prefix: str) -> pd.DataFrame:
    """Pour chaque match et chaque marché, le premier relevé de `labels` où le marché est complet.

    Renvoie une ligne par match : colonnes {prefix}_{clé} (cote) et {prefix}_label_{marché}.
    Toutes les sélections d'un même marché viennent du même relevé (sinon la marge est fausse).
    """
    o = odds[odds["label"].isin(labels)]
    out = pd.DataFrame(index=pd.Index(sorted(odds["match_id"].unique()), name="match_id"))
    if o.empty:
        return out
    wide = o.pivot_table(index=["match_id", "label"], columns="key", values="odd", aggfunc="first")
    wide = wide.reset_index()
    order = {lab: i for i, lab in enumerate(labels)}
    wide["_rank"] = wide["label"].map(order)

    for group, keys in GROUPS.items():
        present = [k for k in keys if k in wide.columns]
        if len(present) < len(keys):
            continue
        ok = wide[present].notna().all(axis=1)
        best = (wide[ok].sort_values(["match_id", "_rank"])
                .drop_duplicates("match_id")
                .set_index("match_id"))
        for k in keys:
            out[f"{prefix}_{k}"] = best[k]
        out[f"{prefix}_label_{group}"] = best["label"]
    return out


def demargin(odds_cols: pd.DataFrame) -> tuple[pd.DataFrame, pd.Series]:
    """Méthode proportionnelle : p_i = (1/o_i) / somme(1/o). Renvoie (probabilités, marge)."""
    inv = 1.0 / odds_cols
    total = inv.sum(axis=1, min_count=odds_cols.shape[1])
    return inv.div(total, axis=0), total - 1.0


def build_odds_table(odds: pd.DataFrame, cfg: dict, decision_labels: list[str]) -> pd.DataFrame:
    """Table par match : cotes de décision, d'ouverture, de clôture + probabilités sans marge."""
    d = cfg["data"]
    odds = choose_bookmaker(odds, d.get("preferred_bookmaker_ids") or [])
    dec = pick_snapshot(odds, decision_labels, "dec")
    opn = pick_snapshot(odds, d["opening_labels"], "open")
    cls = pick_snapshot(odds, d["closing_labels"], "close")
    t = dec.join(opn, how="outer").join(cls, how="outer")
    estimate_decision(t, d.get("decision_mode", "estimated_fallback"))

    for prefix in ("dec", "open"):
        for group, keys in [("1X2", GROUPS["1X2"]), ("BTTS", GROUPS["BTTS"]), ("OU25", GROUPS["OU25"])]:
            cols = [f"{prefix}_{k}" for k in keys]
            if not all(c in t.columns for c in cols):
                for k in keys:
                    t[f"{prefix}_p_{k}"] = np.nan
                t[f"{prefix}_margin_{group}"] = np.nan
                continue
            probs, margin = demargin(t[cols])
            for k, c in zip(keys, cols):
                t[f"{prefix}_p_{k}"] = probs[c]
            t[f"{prefix}_margin_{group}"] = margin
    return t


def estimate_decision(t: pd.DataFrame, mode: str = "estimated_fallback") -> dict:
    """Cote de décision ESTIMÉE quand le relevé du jour / de la veille n'existe pas.

    Cas d'usage : matchs entrés en base par le rattrapage (août-octobre 2026), qui n'ont que la
    cote d'ouverture et la cote finale. On estime la cote de décision entre les deux :

        probabilité implicite estimée = ouverture + w x (clôture - ouverture)      (w entre 0 et 1)

    w est mesuré, marché par marché, sur tous les matchs qui ont les trois relevés (ouverture,
    décision, clôture) : c'est la part du mouvement ouverture -> clôture déjà faite au moment de
    la décision. Si la clôture manque, l'estimation est la cote d'ouverture (w n'a pas d'objet).
    Les cotes estimées portent l'étiquette « EST » (dec_label_<marché>), visible dans sim.Bets.

    Attention : la clôture contient des informations arrivées APRÈS la décision (compositions,
    blessures...). Une partie de ce mouvement entre dans l'estimation : c'est une légère fuite.
    Le mode "estimated_all" permet de la mesurer (voir DOCUMENTATION.md, chapitre 6.6).

    mode : "real"               cotes réellement relevées uniquement (aucune estimation)
           "estimated_fallback" estimation seulement quand le relevé de décision manque (défaut)
           "estimated_all"      estimation pour TOUS les matchs (validation : comparer à "real")
    Renvoie (et range dans t.attrs["estimate"]) : w par marché, nombre de matchs estimés, et
    l'erreur moyenne de l'estimation sur les matchs où le vrai relevé existe.
    """
    info = {"mode": mode, "markets": {}}
    if mode == "real":
        t.attrs["estimate"] = info
        return info
    for group, keys in GROUPS.items():
        dc = [f"dec_{k}" for k in keys]
        oc = [f"open_{k}" for k in keys]
        cc = [f"close_{k}" for k in keys]
        if not all(c in t.columns for c in oc):
            continue
        has_open = t[oc].notna().all(axis=1)
        has_close = t[cc].notna().all(axis=1) if all(c in t.columns for c in cc) else pd.Series(False, index=t.index)
        if not all(c in t.columns for c in dc):
            for c in dc:
                t[c] = np.nan
            t[f"dec_label_{group}"] = None
        has_dec = t[dc].notna().all(axis=1)

        io = 1.0 / t[oc].to_numpy(float)                       # probabilités implicites d'ouverture
        ic = 1.0 / t[cc].to_numpy(float) if all(c in t.columns for c in cc) else None   # de clôture
        idc = 1.0 / t[dc].to_numpy(float)                      # du vrai relevé de décision
        # Mesure de w sur les matchs complets où la cote a bougé entre ouverture et clôture
        full = (has_open & has_close & has_dec).to_numpy()
        w = 0.0
        if ic is not None and full.any():
            mv = (ic - io)[full]
            num = np.nansum((idc - io)[full] * mv)
            den = np.nansum(mv * mv)
            w = float(np.clip(num / den, 0.0, 1.0)) if den > 0 else 0.0

        est = io.copy()
        if ic is not None:
            both = (has_open & has_close).to_numpy()
            est[both] = io[both] + w * (ic[both] - io[both])
        est_odds = 1.0 / est

        # Qualité : écart moyen entre cote estimée et cote réellement relevée
        mae = float(np.nanmean(np.abs(est_odds[full] - t[dc].to_numpy(float)[full]))) if full.any() else None

        target = has_open.copy()
        if mode == "estimated_fallback":
            target &= ~has_dec
        n = int(target.sum())
        if n:
            idx = target.to_numpy()
            for j, c in enumerate(dc):
                col = t[c].to_numpy(float).copy()
                col[idx] = np.round(est_odds[idx, j], 3)
                t[c] = col
            lab = t[f"dec_label_{group}"].astype(object).copy()
            lab[target] = "EST"
            t[f"dec_label_{group}"] = lab
        info["markets"][group] = {"w": round(w, 3), "estimated": n, "mae_odd": None if mae is None else round(mae, 4),
                                  "reference_matches": int(full.sum())}
    t.attrs["estimate"] = info
    return info


def render_estimate(info: dict) -> str:
    """Résumé lisible de l'estimation des cotes de décision (affiché par backtest et daily)."""
    if not info or info.get("mode") == "real" or not info.get("markets"):
        return "Cotes de décision : relevés réels uniquement (aucune estimation)."
    parts = [f"{g} {m['estimated']} (w={m['w']:.2f}, écart moyen {m['mae_odd']:.3f})"
             for g, m in info["markets"].items() if m["estimated"]]
    mode = "toutes estimées (validation)" if info["mode"] == "estimated_all" else "estimées si le relevé manque"
    return f"Cotes de décision {mode} : " + (", ".join(parts) if parts else "aucune")


def market_features(odds_table: pd.DataFrame) -> pd.DataFrame:
    """Variables de marché pour les modèles (relevé de décision + mouvement depuis l'ouverture)."""
    t = odds_table
    f = pd.DataFrame(index=t.index)
    pairs = {
        "mkt_p_home": "1X2_HOME", "mkt_p_draw": "1X2_DRAW", "mkt_p_away": "1X2_AWAY",
        "mkt_p_btts": "BTTS_YES", "mkt_p_over25": "OU25_OVER",
    }
    for name, key in pairs.items():
        f[name] = t.get(f"dec_p_{key}")
        f[name.replace("mkt_p_", "mkt_move_")] = t.get(f"dec_p_{key}") - t.get(f"open_p_{key}")
    # Lignes 1,5 et 3,5 : probabilité du marché seulement (pas de mouvement : trop peu de relevés).
    for name, key in (("mkt_p_over15", "OU15_OVER"), ("mkt_p_over35", "OU35_OVER")):
        col = f"dec_p_{key}"
        f[name] = pd.to_numeric(t[col], errors="coerce") if col in t.columns else np.nan
    f["mkt_margin_1x2"] = t.get("dec_margin_1X2")
    return f
