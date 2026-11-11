"""Simulation de paris sur les prédictions hors échantillon du walk-forward.

Une stratégie = des conditions sur les sorties des modèles + des paris autorisés + une mise.
Deux familles, définies dans config.json > simulation > strategies :

  « combo »  vos règles de concordance entre modèles, par exemple
             buts attendus >= 2,8  ET  P(BTTS) >= 55 %  ET  P(+2,5) >= 55 %  =>  on joue +2,5
  « value »  on joue quand proba du modèle x cote > 1 + marge minimale (edge)

Les deux peuvent se combiner : une règle combo avec "min_edge" n'accepte le pari que si la
cote offerte est supérieure au prix « juste » du modèle.

Le champ "grid" essaie plusieurs seuils. Chaque variante est évaluée et enregistrée
(sim.Runs). La meilleure est choisie sur la phase TUNING uniquement ; seul son résultat
en TEST compte comme preuve. Plus on essaie de variantes, plus le meilleur résultat en
TUNING est flatteur par pur hasard : c'est pourquoi le nombre d'essais est enregistré.

Place dans la chaîne : étape 5 (paris). Appelé par : __main__._simulate, live.py.
Pour ajouter une stratégie : un bloc dans config.json > simulation > strategies (aucun code à écrire).
Documentation : DOCUMENTATION.md, chapitre 8.
"""
from __future__ import annotations

import copy
import itertools
import json

import numpy as np
import pandas as pd

OPS = {">=": np.greater_equal, "<=": np.less_equal, ">": np.greater, "<": np.less, "==": np.equal}

# Pour chaque sélection : (proba de gain, proba de remboursement) à partir des sorties du modèle
# Probabilités du marché exposées aux conditions des stratégies (nom -> sélection)
MARKET_PROBS = {"mkt_p_home": "1X2_HOME", "mkt_p_draw": "1X2_DRAW", "mkt_p_away": "1X2_AWAY",
                "mkt_p_btts": "BTTS_YES", "mkt_p_over15": "OU15_OVER", "mkt_p_over25": "OU25_OVER",
                "mkt_p_over35": "OU35_OVER"}


def _probabilities(p: pd.DataFrame) -> dict[str, tuple[pd.Series, pd.Series]]:
    """Pour chaque pari possible : (probabilité de gagner, probabilité d'être remboursé) selon le modèle."""
    zero = pd.Series(0.0, index=p.index)
    ph, pd_, pa = p["p_home"], p["p_draw"], p["p_away"]
    pb, po = p["p_btts_yes"], p["p_over25"]
    nan = pd.Series(np.nan, index=p.index)
    # Lignes 1,5 et 3,5 : mélange modèle de score + classifieur (comme 2,5) ; les prédictions
    # d'avant le 10/11/2026 n'ont que la sortie du modèle de score.
    def line(final: str, score: str) -> pd.Series:
        """Probabilité finale si elle existe, sinon celle du modèle de score (ligne par ligne)."""
        out = p[final] if final in p else nan
        return out.fillna(p[score]) if score in p else out
    p15 = line("p_over15", "score_p_over15")
    p35 = line("p_over35", "score_p_over35")
    return {
        "1X2_HOME": (ph, zero), "1X2_DRAW": (pd_, zero), "1X2_AWAY": (pa, zero),
        "BTTS_YES": (pb, zero), "BTTS_NO": (1 - pb, zero),
        "OU25_OVER": (po, zero), "OU25_UNDER": (1 - po, zero),
        "OU15_OVER": (p15, zero), "OU15_UNDER": (1 - p15, zero),
        "OU35_OVER": (p35, zero), "OU35_UNDER": (1 - p35, zero),
        "DC_1X": (ph + pd_, zero), "DC_X2": (pd_ + pa, zero), "DC_12": (ph + pa, zero),
        "DNB_HOME": (ph, pd_), "DNB_AWAY": (pa, pd_),
    }


def _outcomes(hg: pd.Series, ag: pd.Series) -> dict[str, pd.Series]:
    """WIN / LOSE / PUSH pour chaque sélection (NaN si le match n'est pas joué)."""
    played = hg.notna() & ag.notna()
    h, d, a = hg > ag, hg == ag, hg < ag
    btts = (hg > 0) & (ag > 0)
    over = (hg + ag) > 2.5
    over15, over35 = (hg + ag) > 1.5, (hg + ag) > 3.5

    def res(win, push=None):
        """Résultat WIN / LOSE / PUSH d'un pari selon le score (NaN si le match n'est pas joué)."""
        r = pd.Series(np.where(win, "WIN", "LOSE"), index=hg.index, dtype=object)
        if push is not None:
            r[push] = "PUSH"
        r[~played] = None
        return r

    return {
        "1X2_HOME": res(h), "1X2_DRAW": res(d), "1X2_AWAY": res(a),
        "BTTS_YES": res(btts), "BTTS_NO": res(~btts),
        "OU25_OVER": res(over), "OU25_UNDER": res(~over),
        "OU15_OVER": res(over15), "OU15_UNDER": res(~over15),
        "OU35_OVER": res(over35), "OU35_UNDER": res(~over35),
        "DC_1X": res(h | d), "DC_X2": res(d | a), "DC_12": res(h | a),
        "DNB_HOME": res(h, d), "DNB_AWAY": res(a, d),
    }


def season_matches_played(matches: pd.DataFrame) -> pd.Series:
    """Pour chaque match : nombre de matchs de championnat déjà joués dans la saison par l'équipe
    qui en a joué le MOINS (avant ce match, jour strictement antérieur). 0 = première journée.
    Sert au filtre de début de saison (config.json > simulation > skip_first_season_matches)."""
    m = matches[["match_id", "date", "season_id", "home_id", "away_id"]].copy()
    m["day"] = m["date"].dt.normalize()
    long = pd.concat([m.rename(columns={"home_id": "team"})[["match_id", "day", "season_id", "team"]],
                      m.rename(columns={"away_id": "team"})[["match_id", "day", "season_id", "team"]]])
    long = long.sort_values(["season_id", "team", "day", "match_id"])
    # rang du match dans la saison de l'équipe ; les matchs du même jour ne se comptent pas entre eux
    long["n_before"] = long.groupby(["season_id", "team"])["day"].rank(method="min").astype(int) - 1
    return long.groupby("match_id")["n_before"].min()


def early_season_mask(matches: pd.DataFrame, skip_first: int) -> pd.Series:
    """True pour les matchs où au moins une équipe a joué moins de `skip_first` matchs dans la saison :
    effectifs remaniés par les transferts, moyennes sur 3 / 5 / 10 matchs encore issues de la saison
    précédente. On n'y parie pas. skip_first = 0 : aucun match exclu."""
    n = season_matches_played(matches)
    return n < skip_first if skip_first else pd.Series(False, index=n.index)


def candidate_bets(preds: pd.DataFrame, matches: pd.DataFrame, odds_table: pd.DataFrame,
                   skip_first: int = 0) -> pd.DataFrame:
    """Tous les paris possibles (match x sélection) avec cote de décision, proba, edge, issue.
    skip_first : matchs de début de saison exclus (voir early_season_mask)."""
    m = matches.set_index("match_id")
    # Lignes 1,5 / 3,5 : probabilité finale, sinon modèle de score (anciens backtests) ; utilisable
    # dans les conditions des stratégies (ex. ["p_over35", ">=", 0.40]).
    for final, score in (("p_over15", "score_p_over15"), ("p_over35", "score_p_over35")):
        if score in preds.columns:
            filled = preds[final].fillna(preds[score]) if final in preds.columns else preds[score]
            preds = preds.assign(**{final: filled})
    if skip_first:
        early = early_season_mask(matches, skip_first)
        preds = preds[~preds.index.isin(early[early].index)]
    base = preds.join(m[["date", "league_name", "season_name", "hg", "ag"]], how="left")
    base = base.join(odds_table, how="left")
    probs = _probabilities(base)
    outs = _outcomes(base["hg"], base["ag"])
    keep = [c for c in preds.columns if c not in ("trained_until",)]
    # Probabilités du MARCHÉ (cote de décision sans marge), utilisables dans les conditions des
    # stratégies : ex. ["mkt_p_away", ">=", 0.40] = l'équipe extérieure est nettement favorite.
    for name, key in MARKET_PROBS.items():
        if name not in base.columns:
            base[name] = base[f"dec_p_{key}"] if f"dec_p_{key}" in base.columns else np.nan
        if name not in keep:
            keep.append(name)
    frames = []
    for key, (p_win, p_push) in probs.items():
        col = f"dec_{key}"
        if col not in base.columns:
            continue
        group = key.rsplit("_", 1)[0]
        f = base[["date", "league_name", "season_name"] + keep].copy()
        f["key"] = key
        f["odd"] = base[col]
        f["odd_label"] = base.get(f"dec_label_{group}")
        f["odd_close"] = base.get(f"close_{key}")
        f["prob"] = p_win
        f["p_push"] = p_push
        f["edge"] = p_win * (f["odd"] - 1) - (1 - p_win - p_push)
        f["outcome"] = outs[key]
        frames.append(f[f["odd"].notna()])
    cands = pd.concat(frames)
    cands.index.name = "match_id"
    return cands.reset_index()


# ----------------------------------------------------------------------------- stratégies
def expand_grid(spec: dict) -> list[dict]:
    """Transforme une stratégie avec « grid » en toutes ses variantes (produit des seuils)."""
    grid = spec.get("grid") or {}
    if not grid:
        s = copy.deepcopy(spec)
        s["_family_size"] = 1
        s["_is_grid"] = False
        return [s]
    keys = list(grid)
    variants = []
    for combo in itertools.product(*(grid[k] for k in keys)):
        s = copy.deepcopy(spec)
        for k, v in zip(keys, combo):
            if k.startswith("conditions."):
                s["conditions"][int(k.split(".")[1])][2] = v
            else:
                s[k] = v
        variants.append(s)
    for s in variants:
        s["_family_size"] = len(variants)
        s["_is_grid"] = True
    return variants


def describe(spec: dict) -> str:
    """Nom lisible d'une variante, ex. « combo_plus25 | exp_total_goals>=2.8 & ... => OU25_OVER »."""
    parts = [f"{f}{op}{v:g}" if isinstance(v, (int, float)) else f"{f}{op}{v}" for f, op, v in spec.get("conditions", [])]
    if spec.get("min_edge") is not None:
        parts.append(f"edge>={spec['min_edge']:g}")
    bets = ",".join(spec["bets"])
    return f"{spec['name']} | {' & '.join(parts) or 'sans condition'} => {bets}"


def select_bets(cands: pd.DataFrame, spec: dict) -> pd.DataFrame:
    """Paris retenus par une stratégie : paris autorisés, ligues, cotes min/max, conditions, edge,
    et un seul pari par match si demandé.
    """
    c = cands[cands["key"].isin(spec["bets"])]
    if spec.get("leagues"):                       # stratégie réservée à certaines ligues
        c = c[c["league_name"].isin(spec["leagues"])]
    if spec.get("min_odd") is not None:
        c = c[c["odd"] >= spec["min_odd"]]
    if spec.get("max_odd") is not None:
        c = c[c["odd"] <= spec["max_odd"]]
    for field, op, value in spec.get("conditions", []):
        if field not in c.columns:
            raise KeyError(f"Condition inconnue '{field}' dans la stratégie {spec['name']}. "
                           f"Champs possibles : {sorted(x for x in c.columns if x.startswith(('p_', 'exp_', 'mkt_', 'score_', 'clf_')))}")
        c = c[OPS[op](c[field], value)]
    if spec.get("min_edge") is not None:
        c = c[c["edge"] >= spec["min_edge"]]
    if spec.get("one_bet_per_match"):
        c = c.sort_values("edge", ascending=False).drop_duplicates("match_id")
    return c.sort_values(["date", "match_id"])


def stake_and_settle(bets: pd.DataFrame, spec: dict, bankroll0: float) -> pd.DataFrame:
    """Mise et résultat de chaque pari : mise fixe, ou Kelly fractionné calculé sur la bankroll du matin."""
    b = bets.copy()
    st = spec.get("staking") or {"method": "flat", "units": 1.0}
    win, push = b["outcome"] == "WIN", b["outcome"] == "PUSH"
    if st["method"] == "flat":
        b["stake"] = float(st.get("units", 1.0))
        b["profit"] = np.where(win, b["stake"] * (b["odd"] - 1), np.where(push, 0.0, -b["stake"]))
        b["bankroll"] = bankroll0 + b["profit"].cumsum()
        return b

    # Kelly fractionné : mises calculées sur la bankroll du matin, réglées le soir
    frac, cap = float(st.get("fraction", 0.25)), float(st.get("max_pct", 0.03))
    kelly = (b["edge"] / (b["odd"] - 1)).clip(lower=0)
    pct = np.minimum(frac * kelly, cap)
    stakes, profits, banks = [], [], []
    bank = bankroll0
    for day, g in b.groupby(b["date"].dt.normalize(), sort=True):
        s = pct.loc[g.index] * bank
        p = np.where(win.loc[g.index], s * (g["odd"] - 1), np.where(push.loc[g.index], 0.0, -s))
        bank += p.sum()
        stakes.append(s)
        profits.append(pd.Series(p, index=g.index))
        banks.append(pd.Series(bank, index=g.index))
    if stakes:
        b["stake"] = pd.concat(stakes)
        b["profit"] = pd.concat(profits)
        b["bankroll"] = pd.concat(banks)
    else:
        b["stake"] = b["profit"] = b["bankroll"] = pd.Series(dtype=float)
    return b


def metrics(b: pd.DataFrame, bankroll0: float, n_boot: int, rng) -> dict:
    """Bilan d'une liste de paris : nombre, mises, profit, ROI, réussite, cote moyenne, CLV, pire baisse."""
    n = len(b)
    if n == 0:
        return {"nb_bets": 0}
    staked, profit = float(b["stake"].sum()), float(b["profit"].sum())
    settled = b[b["outcome"] != "PUSH"]
    cum = b["profit"].cumsum()
    dd = float((cum.cummax().clip(lower=0) - cum).max())
    out = {
        "nb_bets": n, "staked": staked, "profit": profit,
        "roi_pct": 100 * profit / staked if staked else None,
        "hit_rate_pct": 100 * float((settled["outcome"] == "WIN").mean()) if len(settled) else None,
        "avg_odd": float(b["odd"].mean()), "avg_edge_pct": 100 * float(b["edge"].mean()),
        "max_drawdown": dd, "final_bankroll": bankroll0 + profit if "bankroll" not in b else float(b["bankroll"].iloc[-1]),
    }
    clv = (b["odd"] / b["odd_close"] - 1).dropna()
    out["avg_clv_pct"] = 100 * float(clv.mean()) if len(clv) else None
    out["beat_closing_pct"] = 100 * float((clv > 0).mean()) if len(clv) else None
    if n >= 10 and n_boot:
        idx = rng.integers(0, n, size=(n_boot, n))
        s = b["stake"].to_numpy()[idx].sum(axis=1)
        p = b["profit"].to_numpy()[idx].sum(axis=1)
        roi = 100 * p / np.where(s == 0, np.nan, s)
        out["roi_ci_low_pct"], out["roi_ci_high_pct"] = (float(x) for x in np.nanpercentile(roi, [5, 95]))
    return out


ALL_LEAGUES = "Toutes ligues"


def run_simulation(preds: pd.DataFrame, matches: pd.DataFrame, odds_table: pd.DataFrame, cfg: dict,
                   phases=("TUNING", "TEST", "HOLDOUT")) -> tuple[pd.DataFrame, dict[int, pd.DataFrame]]:
    """Évalue toutes les stratégies et toutes leurs variantes, LIGUE PAR LIGUE.

    Pour chaque famille et chaque ligue, la meilleure variante est choisie sur TUNING (avec un
    minimum de paris), puis jugée sur TEST. Le bilan saison par saison de chaque couple
    stratégie x ligue est rangé dans res.attrs["seasons"] (voir season_report) ; il n'y a pas de
    cumul entre ligues.
    Renvoie (résultats, paris détaillés par ligne de résultat).
    """
    sc = cfg["simulation"]
    rng = np.random.default_rng(42)
    cands = candidate_bets(preds, matches, odds_table, int(sc.get("skip_first_season_matches", 0) or 0))
    cands = cands[cands["outcome"].notna()]
    cands["phase"] = preds["phase"].reindex(cands["match_id"]).to_numpy()
    present = set(cands["phase"])
    phases = [ph for ph in phases if ph in present]

    scopes = []
    if sc.get("by_league", True):
        scopes += [(lg, cands[cands["league_name"] == lg]) for lg in sorted(cands["league_name"].dropna().unique())]
    if sc.get("include_all_leagues", False) or not scopes:
        scopes.append((ALL_LEAGUES, cands))

    rows, bets_by_row = [], {}
    for spec in sc["strategies"]:
        variants = expand_grid(spec)
        for variant in variants:
            for league, sub_c in scopes:
                chosen = select_bets(sub_c, variant)
                for phase in phases:
                    sub = chosen[chosen["phase"] == phase]
                    settled = stake_and_settle(sub, variant, sc["bankroll"])
                    met = metrics(settled, sc["bankroll"], 0, rng)      # IC calculé plus bas, pour les retenues
                    row = {"family": spec["name"], "league": league, "name": describe(variant), "phase": phase,
                           "type": "combo" if variant.get("conditions") else "value",
                           "spec": {k: v for k, v in variant.items() if not k.startswith("_") and k != "grid"},
                           "family_size": variant["_family_size"], "is_grid": variant["_is_grid"],
                           "period_from": sub["date"].min() if len(sub) else None,
                           "period_to": sub["date"].max() if len(sub) else None, **met}
                    bets_by_row[len(rows)] = settled
                    rows.append(row)
    res = pd.DataFrame(rows)

    # Sélection : pour chaque famille ET chaque ligue, la meilleure variante sur TUNING
    res["selected"] = False
    metric = sc.get("selection_metric", "roi_pct")
    min_bets = sc.get("min_bets_per_league", 30)
    for (fam, league), g in res.groupby(["family", "league"]):
        min_n = sc["min_bets_for_selection"] if league == ALL_LEAGUES else min_bets
        if not g["is_grid"].iloc[0]:
            res.loc[g.index, "selected"] = True
            continue
        tun = g[(g["phase"] == "TUNING") & (g["nb_bets"] >= min_n)]
        if tun.empty or metric not in tun or tun[metric].isna().all():
            continue
        best = tun.loc[tun[metric].idxmax(), "name"]
        res.loc[g.index[g["name"] == best], "selected"] = True

    # Intervalles de confiance des lignes retenues seulement (le bootstrap est coûteux)
    for i in res.index[res["selected"]]:
        b = bets_by_row[i]
        if len(b) >= 10:
            res.loc[i, ["roi_ci_low_pct", "roi_ci_high_pct"]] = _bootstrap_ci(b, sc["bootstrap_samples"], rng)

    res["portfolio"] = False                # plus de portefeuille toutes ligues : bilan par ligue et par saison
    res.attrs["seasons"] = season_report(res, bets_by_row)
    return res, bets_by_row


def _bootstrap_ci(b: pd.DataFrame, n_boot: int, rng) -> tuple[float, float]:
    """Intervalle de confiance à 90 % du ROI par rééchantillonnage des paris (bootstrap)."""
    n = len(b)
    idx = rng.integers(0, n, size=(n_boot, n))
    s = b["stake"].to_numpy(float)[idx].sum(axis=1)
    p = b["profit"].to_numpy(float)[idx].sum(axis=1)
    roi = 100 * p / np.where(s == 0, np.nan, s)
    lo, hi = np.nanpercentile(roi, [5, 95])
    return float(lo), float(hi)


PHASE_LABEL = {"HOLDOUT": "EN COURS"}


PHASE_SHORT = {"TUNING": "réglage", "TEST": "test", "HOLDOUT": "en cours"}


def _season_metrics(b: pd.DataFrame) -> dict:
    """Bilan d'un ensemble de paris : nombre, mises, profit, ROI, réussite, cote moyenne, pire baisse."""
    if b.empty:
        return {"nb_bets": 0}
    settled = b[b["outcome"] != "PUSH"]
    cum = b.sort_values("date")["profit"].cumsum()
    staked = float(b["stake"].sum())
    return {"nb_bets": len(b), "staked": staked, "profit": float(b["profit"].sum()),
            "roi_pct": 100 * float(b["profit"].sum()) / staked if staked else None,
            "hit_rate_pct": 100 * float((settled["outcome"] == "WIN").mean()) if len(settled) else None,
            "avg_odd": float(b["odd"].mean()),
            "max_drawdown": float((cum.cummax().clip(lower=0) - cum).max())}


def season_report(res: pd.DataFrame, bets_by_row: dict) -> pd.DataFrame:
    """Bilan de chaque stratégie dans chaque ligue (variante retenue sur la période de réglage),
    SAISON PAR SAISON, puis deux totaux : « Test » (seule mesure honnête : saisons jamais utilisées
    pour choisir la variante) et « Total » (toutes les saisons, réglage compris).
    Une ligne par (stratégie, ligue, saison) ; season = 'Test' ou 'Total' pour les totaux.
    """
    rows = []
    sel = res[res["selected"] & ~res.get("portfolio", pd.Series(False, index=res.index))]
    for (fam, league), g in sel.groupby(["family", "league"], sort=False):
        parts = [bets_by_row[i] for i in g.index if i in bets_by_row and len(bets_by_row[i])]
        name = g["name"].iloc[0]
        if not parts:
            rows.append({"family": fam, "league": league, "name": name, "season": "Total", "phase": "",
                         "nb_bets": 0})
            continue
        b = pd.concat(parts)
        for season, gs in b.groupby("season_name", sort=True):
            rows.append({"family": fam, "league": league, "name": name, "season": season,
                         "phase": PHASE_SHORT.get(gs["phase"].iloc[0], gs["phase"].iloc[0]),
                         **_season_metrics(gs)})
        rows.append({"family": fam, "league": league, "name": name, "season": "Test", "phase": "test",
                     **_season_metrics(b[b["phase"] == "TEST"])})
        rows.append({"family": fam, "league": league, "name": name, "season": "Total", "phase": "",
                     **_season_metrics(b)})
    return pd.DataFrame(rows)


def _cell(r) -> str:
    """Cellule du tableau : « +5% (42) » ; « · » s'il n'y a eu aucun pari."""
    if r is None or not r.get("nb_bets"):
        return "·"
    return f"{r['roi_pct']:+.0f}% ({int(r['nb_bets'])})"


def render(res: pd.DataFrame) -> str:
    """Pour chaque stratégie : un tableau ligues x saisons (ROI et nombre de paris de la variante
    retenue dans la ligue), puis le bilan Test et le bilan Total de la stratégie dans la ligue."""
    rep = res.attrs.get("seasons")
    if rep is None or rep.empty:
        return "Aucune stratégie évaluée."
    seasons = sorted(s for s in rep["season"].unique() if s not in ("Test", "Total"))
    phase_of = (rep[rep["season"].isin(seasons)].drop_duplicates("season").set_index("season")["phase"])
    w = 13
    lines = ["Bilan par stratégie, par ligue et par saison (variante choisie sur la période de réglage de chaque ligue)",
             "Cellule = ROI (nombre de paris). Seule la colonne « Test » mesure la stratégie honnêtement."]
    for fam, g in rep.groupby("family", sort=False):
        size = int(res.loc[res["family"] == fam, "family_size"].iloc[0])
        lines.append(f"\n  {fam}" + (f"  —  {size} variantes essayées dans chaque ligue" if size > 1 else ""))
        lines.append("    " + f"{'Ligue':<16}" + "".join(f"{s:>{w}}" for s in seasons) + f"{'Test':>{w}}{'Total':>{w}}"
                     + "   Profit test  Verdict")
        lines.append("    " + " " * 16 + "".join(f"{phase_of.get(s, ''):>{w}}" for s in seasons))
        for league, gl in g.groupby("league", sort=True):
            by = gl.set_index("season")
            cells = "".join(f"{_cell(by.loc[s] if s in by.index else None):>{w}}" for s in seasons)
            t = by.loc["Test"] if "Test" in by.index else None
            tot = by.loc["Total"] if "Total" in by.index else None
            # Verdict : IC90 calculé sur la ligne TEST de sim.Runs pour cette ligue
            r = res[(res["family"] == fam) & (res["league"] == league) & res["selected"] & (res["phase"] == "TEST")]
            low = r["roi_ci_low_pct"].iloc[0] if len(r) and "roi_ci_low_pct" in r else np.nan
            if t is None or not t.get("nb_bets"):
                verdict, profit = "aucun pari", ""
            else:
                verdict = ("PROMETTEUSE" if pd.notna(low) and low > 0 else
                           "positive" if t["roi_pct"] > 0 else "perdante")
                profit = f"{t['profit']:+.1f} u"
            lines.append(f"    {league[:16]:<16}{cells}{_cell(t):>{w}}{_cell(tot):>{w}}   {profit:>11}  {verdict}")
        missing = sorted(set(res.loc[res["family"] == fam, "league"]) - set(g["league"]))
        if missing:
            lines.append(f"    sans variante retenue (trop peu de paris en réglage) : {', '.join(missing)}")
    test = rep[rep["season"] == "Test"]
    n_good = sum(1 for _, r in res[res["selected"] & (res["phase"] == "TEST")].iterrows()
                 if pd.notna(r.get("roi_ci_low_pct")) and r["roi_ci_low_pct"] > 0)
    lines.append(f"\nBilan : {int((test['roi_pct'] > 0).sum())} couple(s) stratégie x ligue positifs en test sur "
                 f"{int((test['nb_bets'] > 0).sum())}, dont {n_good} « PROMETTEUSE » (borne basse IC90 > 0).")
    lines.append("Rappel : la variante est choisie sur les saisons de réglage, son résultat y est donc flatteur.\n"
                 "Une stratégie n'est crédible dans une ligue que si elle gagne sur CHAQUE saison de test.")
    return "\n".join(lines)


def spec_json(spec: dict) -> str:
    """Définition d'une stratégie en JSON (enregistrée dans sim.Runs.StrategyJson)."""
    return json.dumps(spec, ensure_ascii=False, default=str)
