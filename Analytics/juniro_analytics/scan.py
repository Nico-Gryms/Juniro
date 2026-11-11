"""Balayage des seuils : « quand le modèle dit X, que se passe-t-il si l'on parie Y ? »

Exemple : buts attendus >= 2,7  =>  Plus de 1,5. Pour chaque sortie des modèles (le
« signal »), chaque seuil et chaque pari possible, on mesure sur les matchs déjà joués :
  - le taux de réussite et la cote minimale qu'il faudrait pour ne pas perdre (cote d'équilibre) ;
  - quand la cote existe en base : cote moyenne, probabilité implicite, ROI, intervalle de confiance, CLV.

Ce balayage est séparé des stratégies (sim.Runs) : il ne choisit rien, il cartographie. Les
résultats vont dans sim.RuleScan, phase par phase. Une règle n'est intéressante que si elle
tient sur TUNING ET sur TEST : avec des milliers de combinaisons, des dizaines paraîtront
rentables sur une seule période par pur hasard.

Place dans la chaîne : étape 5 (paris), en parallèle de simulate.py.
Appelé par : __main__._scan (fin de chaque backtest, ou commande scan).
Deux familles de signaux :
  - signals     : sorties des modèles (buts attendus, probabilités) ;
  - raw_signals : variables BRUTES, avant tout modèle (écart d'Elo, écart de xG, forme, confrontations
                  directes...). Contrôle : si une variable seule gagnait, le marché la négligerait.
Pour changer les signaux et seuils : config.json > scan > signals / raw_signals.
Documentation : DOCUMENTATION.md, chapitre 8.3.
"""
from __future__ import annotations

import numpy as np
import pandas as pd

from .simulate import _outcomes, early_season_mask

BET_KEYS = ["OU15_OVER", "OU15_UNDER", "OU25_OVER", "OU25_UNDER", "OU35_OVER", "OU35_UNDER",
            "BTTS_YES", "BTTS_NO", "1X2_HOME", "1X2_DRAW", "1X2_AWAY",
            "DC_1X", "DC_X2", "DC_12", "DNB_HOME", "DNB_AWAY"]


def thresholds(spec) -> np.ndarray:
    """Liste des seuils à essayer : {from, to, step} ou liste explicite."""
    if isinstance(spec, dict):
        return np.round(np.arange(spec["from"], spec["to"] + spec["step"] / 2, spec["step"]), 4)
    return np.asarray(spec, dtype=float)


def run_scan(preds: pd.DataFrame, matches: pd.DataFrame, odds_table: pd.DataFrame, cfg: dict,
             phases=("TUNING", "TEST"), features: pd.DataFrame | None = None,
             scores: pd.DataFrame | None = None, by_season: bool = True) -> pd.DataFrame:
    """Pour chaque ligue, phase, signal, sens (>= / <=), seuil et pari : réussite, cote d'équilibre,
    et si la cote existe ROI, IC90 et CLV. Calcul matriciel : quelques secondes pour ~150 000 lignes.

    features : table des variables, nécessaire pour les raw_signals (sinon ils sont ignorés).
    scores   : scores de remplacement (colonnes hg, ag, index match_id) ; sert au placebo
               (placebo.py), qui rejoue le balayage sur des résultats tirés au hasard.
    by_season: calcule aussi le ROI de chaque règle SAISON PAR SAISON (rangé dans
               res.attrs["seasons"], non enregistré en base) pour les filtres durs (candidates).
    """
    sc = cfg["scan"]
    m = matches.set_index("match_id")
    base = preds.join(m[["hg", "ag", "league_name", "season_name"]], how="left")
    if scores is not None:
        base["hg"] = scores["hg"].reindex(base.index)
        base["ag"] = scores["ag"].reindex(base.index)
    raw = dict(sc.get("raw_signals") or {})
    if features is not None and raw:
        cols = [c for c in raw if c in features.columns and c not in base.columns]
        base = base.join(features[cols], how="left")
    signals = {**sc["signals"], **raw}
    base = base[base["hg"].notna() & base["ag"].notna() & base["phase"].isin(phases)]
    skip = int(cfg["simulation"].get("skip_first_season_matches", 0) or 0)
    if skip:                                      # même filtre de début de saison que la simulation
        early = early_season_mask(matches, skip)
        base = base[~base.index.isin(early[early].index)]
    outs = _outcomes(base["hg"], base["ag"])
    keys = [k for k in sc.get("bets", BET_KEYS) if k in outs]

    W = np.column_stack([(outs[k] == "WIN").to_numpy(float) for k in keys])
    P = np.column_stack([(outs[k] == "PUSH").to_numpy(float) for k in keys])
    L = 1.0 - W - P
    ot = odds_table.reindex(base.index)
    O = np.column_stack([ot[f"dec_{k}"].to_numpy(float) if f"dec_{k}" in ot else np.full(len(base), np.nan)
                         for k in keys])
    C = np.column_stack([ot[f"close_{k}"].to_numpy(float) if f"close_{k}" in ot else np.full(len(base), np.nan)
                         for k in keys])
    V = ~np.isnan(O)
    mats = {
        "W": W, "P": P, "L": L, "V": V.astype(float),
        "PR": np.where(V, W * (np.nan_to_num(O) - 1) - L, 0.0),          # gain par pari d'une unité
        "IMP": np.where(V, 1.0 / np.where(V, O, 1.0), 0.0),               # probabilité implicite brute
        "ODD": np.where(V, np.nan_to_num(O), 0.0),
        "CLV": np.where(V & ~np.isnan(C), np.nan_to_num(O) / np.where(np.isnan(C), 1.0, C) - 1, 0.0),
        "VC": (V & ~np.isnan(C)).astype(float),
    }
    mats["PR2"] = mats["PR"] ** 2

    phase = base["phase"].to_numpy()
    season = base["season_name"].fillna("?").to_numpy()
    league = base["league_name"].fillna("?").to_numpy()
    scopes = []
    if sc.get("by_league", True):
        scopes += [(lg, league == lg) for lg in sorted(set(league))]
    if sc.get("include_all_leagues", False) or not scopes:
        scopes.append(("Toutes ligues", np.ones(len(base), dtype=bool)))
    sig_values = {s_: base[s_].to_numpy(float) for s_ in signals if s_ in base.columns}

    blocks, season_blocks = [], []
    for lg, in_lg in scopes:
        periods = [("phase", ph, (phase == ph) & in_lg) for ph in phases]
        if by_season:
            periods += [("season", ss, (season == ss) & in_lg) for ss in sorted(set(season[in_lg]))]
        for kind, label, sel in periods:
            if not sel.any():
                continue
            sub = {k: v[sel] for k, v in mats.items()}
            for signal, x_all in sig_values.items():
                x = x_all[sel]
                ts = thresholds(signals[signal])
                for op in (">=", "<="):
                    M = (x[:, None] >= ts[None, :]) if op == ">=" else (x[:, None] <= ts[None, :])
                    M = (M & ~np.isnan(x)[:, None]).astype(float)        # matchs x seuils
                    blk = _block(M, sub, ts, keys, sc["min_matches"], light=(kind == "season"))
                    if blk is None:
                        continue
                    blk.insert(0, "league", lg)
                    blk.insert(1, "phase", label)
                    blk.insert(2, "signal", signal)
                    blk.insert(3, "op", op)
                    (season_blocks if kind == "season" else blocks).append(blk)
    res = pd.concat(blocks, ignore_index=True) if blocks else pd.DataFrame()
    seasons = (pd.concat(season_blocks, ignore_index=True).rename(columns={"phase": "season"})
               if season_blocks else pd.DataFrame())
    # Les ROI saison par saison sont gardés à part (un DataFrame dans .attrs gênerait pandas) :
    # res.attrs ne contient qu'un jeton, season_table(res) les retrouve.
    global _SEASON_TOKEN
    _SEASON_TOKEN += 1
    _SEASONS[_SEASON_TOKEN] = seasons
    for old in [k for k in _SEASONS if k <= _SEASON_TOKEN - 4]:
        del _SEASONS[old]
    res.attrs["season_token"] = _SEASON_TOKEN
    return res


_SEASONS: dict[int, pd.DataFrame] = {}
_SEASON_TOKEN = 0


def season_table(scan: pd.DataFrame) -> pd.DataFrame:
    """ROI de chaque règle saison par saison (calculé par run_scan avec by_season=True)."""
    return _SEASONS.get(scan.attrs.get("season_token"), pd.DataFrame())


def _block(M: np.ndarray, sub: dict, ts: np.ndarray, keys: list[str], min_matches: int,
           light: bool = False) -> pd.DataFrame | None:
    """Statistiques de toutes les règles (seuil x pari) d'un même signal, sens et période.
    light : seulement le nombre de paris et le ROI (calcul saison par saison)."""
    n = M.sum(axis=0)
    keep_t = n >= min_matches
    if not keep_t.any():
        return None
    M = M[:, keep_t]
    ts, n = ts[keep_t], n[keep_t]
    T, K = len(ts), len(keys)
    agg = {k: M.T @ sub[k] for k in (["V", "PR"] if light else sub)}
    n_odds = agg["V"]
    with np.errstate(divide="ignore", invalid="ignore"):
        mean = np.where(n_odds >= min_matches, agg["PR"] / n_odds, np.nan)
    out = {"threshold": np.repeat(ts.astype(float), K), "bet": np.tile(keys, T),
           "n_odds": n_odds.ravel().astype(int), "roi_pct": 100 * mean.ravel()}
    if light:
        out["profit"] = np.where(n_odds >= min_matches, agg["PR"], np.nan).ravel()
        return pd.DataFrame(out)
    w, pu, lo = agg["W"], agg["P"], agg["L"]
    with np.errstate(divide="ignore", invalid="ignore"):
        ok = n_odds >= min_matches
        sd = np.sqrt(np.maximum(agg["PR2"] / n_odds - (agg["PR"] / n_odds) ** 2, 0.0))
        half = 1.645 * sd / np.sqrt(n_odds)
        out.update({
            "n": np.repeat(n.astype(int), K), "wins": w.ravel().astype(int), "pushes": pu.ravel().astype(int),
            "hit_rate_pct": np.where(w + lo > 0, 100 * w / (w + lo), np.nan).ravel(),
            "break_even_odd": np.where(w > 0, 1 + lo / w, np.nan).ravel(),
            "avg_odd": np.where(ok, agg["ODD"] / n_odds, np.nan).ravel(),
            "avg_implied_pct": np.where(ok, 100 * agg["IMP"] / n_odds, np.nan).ravel(),
            "profit": np.where(ok, agg["PR"], np.nan).ravel(),
            "roi_ci_low_pct": np.where(ok, 100 * (mean - half), np.nan).ravel(),
            "roi_ci_high_pct": np.where(ok, 100 * (mean + half), np.nan).ravel(),
            "roi_se_pct": np.where(ok, 100 * sd / np.sqrt(n_odds), np.nan).ravel(),
            "avg_clv_pct": np.where(ok & (agg["VC"] > 0), 100 * agg["CLV"] / np.where(agg["VC"] > 0, agg["VC"], 1),
                                    np.nan).ravel(),
        })
    df = pd.DataFrame(out)
    return df[["threshold", "bet", "n", "wins", "pushes", "hit_rate_pct", "break_even_odd", "n_odds",
               "avg_odd", "avg_implied_pct", "profit", "roi_pct", "roi_ci_low_pct", "roi_ci_high_pct",
               "roi_se_pct", "avg_clv_pct"]]


def render(scan: pd.DataFrame, top: int = 15, raw_signals=None, cfg: dict | None = None) -> str:
    """Les règles qui tiennent sur les DEUX périodes (ROI positif en TUNING et en TEST)."""
    if scan.empty or "roi_pct" not in scan:
        return "Balayage : aucune cote disponible pour calculer un ROI (seuls les taux de réussite sont enregistrés)."
    key = ["league", "signal", "op", "threshold", "bet"]
    if "league" not in scan:
        scan = scan.assign(league="Toutes ligues")
    t = scan[scan["phase"] == "TUNING"].set_index(key)
    s = scan[scan["phase"] == "TEST"].set_index(key)
    both = t[["n_odds", "roi_pct"]].join(s[["n_odds", "roi_pct", "roi_ci_low_pct", "hit_rate_pct",
                                            "break_even_odd", "avg_odd"]], lsuffix="_tun", rsuffix="_test",
                                         how="inner").dropna(subset=["roi_pct_tun", "roi_pct_test"])
    total = len(both)
    good = both[(both["roi_pct_tun"] > 0) & (both["roi_pct_test"] > 0)]
    lines = [f"Balayage des seuils : {total} règles avec cotes sur les deux périodes, "
             f"{len(good)} positives sur les deux ({100 * len(good) / max(total, 1):.0f} %)."]
    if total:
        lines.append("Attention : les règles se recoupent (2,6 / 2,7 / 2,8 ... sélectionnent presque les mêmes matchs)\n"
                     "et des milliers ont été testées. Seule une borne basse d'IC90 positive en TEST compte.")
    h = scan[scan["phase"] == "HOLDOUT"].set_index(key)
    per = both.assign(ok=(both["roi_pct_tun"] > 0) & (both["roi_pct_test"] > 0),
                      solid=(both["roi_pct_tun"] > 0) & (both["roi_ci_low_pct"] > 0)).groupby(level="league")
    lines.append(f"\n    {'Ligue':<18}{'règles':>8}{'positives 2 périodes':>22}{'IC90 TEST > 0':>15}")
    for lg, g in per:
        lines.append(f"    {lg[:18]:<18}{len(g):>8}{int(g['ok'].sum()):>22}{int(g['solid'].sum()):>15}")
    raw = set(raw_signals or [])
    is_raw = good.index.get_level_values("signal").isin(raw)
    lines.append("")
    if raw:
        rb = both.index.get_level_values("signal").isin(raw)
        lines.append(f"Variables brutes : {int(rb.sum())} règles, {int(is_raw.sum())} positives sur les deux périodes, "
                     f"{int((good[is_raw]['roi_ci_low_pct'] > 0).sum())} avec IC90 TEST > 0.")
        lines.append("Sorties des modèles : "
                     f"{int((~rb).sum())} règles, {int((~is_raw).sum())} positives sur les deux périodes, "
                     f"{int((good[~is_raw]['roi_ci_low_pct'] > 0).sum())} avec IC90 TEST > 0.\n")
    sections = [("", good[~is_raw] if raw else good, top)]
    if raw:
        sections.append(("\n  Meilleures règles sur variables BRUTES :", good[is_raw], 10))
    for title, sub, k_top in sections:
        if title:
            lines.append(title)
        _render_rules(lines, sub, h, k_top)
    cands, funnel, near = candidates(scan, cfg)
    if funnel:
        lines.append("\n" + render_candidates(cands, funnel, near))
    return "\n".join(lines)


def _render_rules(lines: list, good: pd.DataFrame, h: pd.DataFrame, top: int):
    """Ajoute à `lines` les `top` règles les plus solides (borne basse IC90 en TEST)."""
    for k, r in good.sort_values("roi_ci_low_pct", ascending=False).head(top).iterrows():
        lg, sig, op, thr, bet = k
        hold = ""
        if k in h.index and pd.notna(h.loc[k, "roi_pct"]):
            hold = f"   EN COURS {h.loc[k, 'roi_pct']:+6.1f} % ({int(h.loc[k, 'n_odds'])})"
        lines.append(f"  {lg[:16]:<16} {sig} {op} {thr:g} => {bet:<10}  TUNING {r['roi_pct_tun']:+6.1f} % ({int(r['n_odds_tun'])})"
                     f"   TEST {r['roi_pct_test']:+6.1f} % ({int(r['n_odds_test'])}, IC90 bas {r['roi_ci_low_pct']:+.1f})"
                     f"   réussite {r['hit_rate_pct']:.1f} %  cote moy. {r['avg_odd']:.2f}{hold}")


# ----------------------------------------------------------------------------- filtres durs
CANDIDATE_DEFAULTS = {"min_season_bets": 15, "fdr": 0.10, "require_all_seasons": True,
                      "require_neighbours": True, "require_clv": False}

KEY = ["league", "signal", "op", "threshold", "bet"]


def candidates(scan: pd.DataFrame, cfg: dict | None = None):
    """Règles qui passent TOUS les filtres durs (config.json > scan > candidates), dans l'ordre :

      1. ROI positif en réglage ET en test ;
      2. borne basse de l'IC90 du test > 0 ;
      3. positives sur CHAQUE saison (au moins `min_season_bets` paris par saison, toutes les
         saisons de réglage et de test de la ligue) ;
      4. régulières : les seuils voisins (juste au-dessus et juste en dessous) sont aussi
         positifs en test, sinon c'est un pic isolé, typique du hasard ;
      5. correction pour tests multiples (Benjamini-Hochberg sur TOUTES les règles testées) :
         au plus `fdr` (10 %) de fausses découvertes attendues dans la liste ;
      6. (si require_clv) CLV moyen du test >= 0.

    Renvoie (règles retenues, entonnoir [(étape, nombre de règles)], règles qui passent tout
    sauf la correction pour tests multiples).
    """
    from scipy.stats import norm
    c = {**CANDIDATE_DEFAULTS, **(((cfg or {}).get("scan") or {}).get("candidates") or {})}
    if scan.empty or "roi_pct" not in scan:
        return pd.DataFrame(), [], pd.DataFrame()
    t = scan[scan["phase"] == "TUNING"].set_index(KEY)
    s = scan[scan["phase"] == "TEST"].set_index(KEY)
    both = t[["n_odds", "roi_pct"]].join(
        s[["n_odds", "roi_pct", "roi_ci_low_pct", "roi_se_pct", "hit_rate_pct", "avg_odd", "avg_clv_pct"]],
        lsuffix="_tun", rsuffix="_test", how="inner").dropna(subset=["roi_pct_tun", "roi_pct_test"])
    funnel = [("règles avec cotes en réglage et en test", len(both))]
    if both.empty:
        return both, funnel, both

    # 5 (calculé sur tout l'univers testé) : p-valeur unilatérale du ROI de test, puis Benjamini-Hochberg
    z = both["roi_pct_test"] / both["roi_se_pct"].replace(0, np.nan)
    pval = pd.Series(norm.sf(z.fillna(-np.inf)), index=both.index)
    m_tests = len(pval)
    order = pval.sort_values()
    crit = c["fdr"] * np.arange(1, m_tests + 1) / m_tests
    passed = order.to_numpy() <= crit
    k = int(np.max(np.nonzero(passed)[0]) + 1) if passed.any() else 0
    bh_ok = pd.Series(False, index=both.index)
    bh_ok.loc[order.index[:k]] = True
    both["p_value"] = pval

    # 4 : seuils voisins dans le test
    st = s.reset_index().sort_values(["league", "signal", "op", "bet", "threshold"])
    g = st.groupby(["league", "signal", "op", "bet"])["roi_pct"]
    st["prev"], st["next"] = g.shift(1), g.shift(-1)
    st["neigh_ok"] = ((st["prev"].isna() | (st["prev"] > 0)) & (st["next"].isna() | (st["next"] > 0))
                      & ~(st["prev"].isna() & st["next"].isna()))
    neigh = st.set_index(KEY)["neigh_ok"].reindex(both.index).fillna(False)

    # 3 : saison par saison
    seasons = season_table(scan)
    if not seasons.empty:
        ss = seasons[seasons["n_odds"] >= c["min_season_bets"]]
        n_avail = seasons.groupby("league")["season"].nunique()
        agg = ss.groupby(KEY).agg(n_ok=("roi_pct", "size"), n_pos=("roi_pct", lambda r: int((r > 0).sum())),
                                  worst=("roi_pct", "min"))
        agg = agg.reindex(both.index)
        need = pd.Series(both.index.get_level_values("league"), index=both.index).map(n_avail)
        season_ok = (agg["n_ok"] >= need) & (agg["n_pos"] == agg["n_ok"])
        both["seasons_pos"] = agg["n_pos"].fillna(0).astype(int).astype(str) + "/" + need.fillna(0).astype(int).astype(str)
        both["worst_season_pct"] = agg["worst"]
    else:
        season_ok = pd.Series(True, index=both.index)
        both["seasons_pos"], both["worst_season_pct"] = "?", np.nan

    f = (both["roi_pct_tun"] > 0) & (both["roi_pct_test"] > 0)
    funnel.append(("1. positives en réglage et en test", int(f.sum())))
    f &= both["roi_ci_low_pct"] > 0
    funnel.append(("2. + borne basse IC90 du test > 0", int(f.sum())))
    if c["require_all_seasons"]:
        f &= season_ok.fillna(False)
        funnel.append((f"3. + positives sur chaque saison (>= {c['min_season_bets']} paris)", int(f.sum())))
    if c["require_neighbours"]:
        f &= neigh.astype(bool)
        funnel.append(("4. + seuils voisins positifs (régularité)", int(f.sum())))
    f_before_bh = f.copy()
    f &= bh_ok
    funnel.append((f"5. + correction tests multiples (Benjamini-Hochberg, {100 * c['fdr']:.0f} %)", int(f.sum())))
    if c["require_clv"]:
        f &= both["avg_clv_pct_test"].fillna(-1) >= 0
        funnel.append(("6. + CLV du test >= 0", int(f.sum())))
    out = both[f].sort_values("roi_ci_low_pct", ascending=False)
    near = both[f_before_bh & ~f].sort_values("p_value")        # passent tout sauf la correction
    return out, funnel, near


def render_candidates(cands: pd.DataFrame, funnel: list, near: pd.DataFrame | None = None, top: int = 15) -> str:
    """Entonnoir des filtres durs et règles retenues (ou les plus proches si aucune ne passe)."""
    lines = ["Filtres durs (une règle doit tout passer) :"]
    for label, n in funnel:
        lines.append(f"    {label:<62}{n:>8}")
    show, title = cands, "Candidates (passent tous les filtres) :"
    if cands.empty:
        show = near
        title = "Aucune candidate. Les plus proches (tout sauf la correction pour tests multiples) :"
    if show is None or show.empty:
        return "\n".join(lines)
    lines.append("\n  " + title)
    for k, r in show.head(top).iterrows():
        lg, sig, op, thr, bet = k
        lines.append(f"  {lg[:16]:<16} {sig} {op} {thr:g} => {bet:<10}  TUNING {r['roi_pct_tun']:+6.1f} %"
                     f"   TEST {r['roi_pct_test']:+6.1f} % ({int(r['n_odds_test'])}, IC90 bas {r['roi_ci_low_pct']:+.1f},"
                     f" p={r['p_value']:.4f})   saisons + {r['seasons_pos']}  pire {r['worst_season_pct']:+.0f} %"
                     f"   cote {r['avg_odd']:.2f}")
    return "\n".join(lines)
