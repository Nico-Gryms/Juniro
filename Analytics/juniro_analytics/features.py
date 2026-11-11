"""Moteur de variables : tout est calculé AU COUP D'ENVOI, sans information future.

Principe unique, appliqué partout :
  1. on calcule l'« état après match » de chaque équipe (moyennes glissantes, cumul de
     saison, classement...) en incluant le match ;
  2. on rattache à chaque match l'état le plus récent STRICTEMENT ANTÉRIEUR à son coup
     d'envoi (jointure « as-of », allow_exact_matches=False).
Le résultat d'un match ne peut donc jamais entrer dans ses propres variables, ni dans
celles d'un match joué au même moment. Un test automatique le vérifie.

Place dans la chaîne : étape 2 (variables). Appelé par : __main__.build, live, tune.
Résultat : une ligne par match, colonnes home_* / away_* / diff_* / lg_* / mkt_* / ctx_*, et les
cibles y_* (résultat, BTTS, +2.5) pour l'entraînement. Enregistré dans feat.MatchFeatures.
Pour ajouter une statistique : l'ajouter à BASE_METRICS (et à team_long si elle est nouvelle) ;
elle sera automatiquement calculée sur 3/5/10 matchs, avant chaque match.
Documentation : DOCUMENTATION.md, chapitre 6.
"""
from __future__ import annotations

import warnings

import numpy as np
import pandas as pd
from scipy.stats import poisson

from . import elo as elo_mod
from . import odds as odds_mod

warnings.filterwarnings("ignore", category=pd.errors.PerformanceWarning)

# Statistiques par équipe et par match (point de vue de l'équipe)
BASE_METRICS = ["gf", "ga", "xgf", "xga", "shots_f", "shots_a", "sot_f", "sot_a",
                "corners_f", "corners_a", "poss", "pts", "xpts", "btts", "over25", "cs", "fts"]
VENUE_METRICS = ["gf", "ga", "xgf", "xga", "sot_f", "sot_a", "pts", "btts", "over25"]
EWM_METRICS = ["gf", "ga", "xgf", "xga", "sot_f", "sot_a", "pts", "xpts"]
SEASON_METRICS = ["pts", "gf", "ga", "xgf", "xga", "xpts"]


# ----------------------------------------------------------------------------- utilitaires
def xpts_from_xg(xg_for: np.ndarray, xg_against: np.ndarray, max_goals: int = 10) -> np.ndarray:
    """Points attendus d'après les xG du match (deux lois de Poisson indépendantes)."""
    xf = np.nan_to_num(np.asarray(xg_for, dtype=float), nan=0.0)
    xa = np.nan_to_num(np.asarray(xg_against, dtype=float), nan=0.0)
    g = np.arange(max_goals + 1)
    pf = poisson.pmf(g[None, :], np.clip(xf, 1e-6, None)[:, None])
    pa = poisson.pmf(g[None, :], np.clip(xa, 1e-6, None)[:, None])
    joint = pf[:, :, None] * pa[:, None, :]
    win = np.tril(np.ones((max_goals + 1, max_goals + 1)), -1)
    p_win = (joint * win).sum(axis=(1, 2))
    p_draw = np.trace(joint, axis1=1, axis2=2)
    out = 3 * p_win + p_draw
    out[np.isnan(np.asarray(xg_for, dtype=float)) | np.isnan(np.asarray(xg_against, dtype=float))] = np.nan
    return out


def team_long(m: pd.DataFrame) -> pd.DataFrame:
    """Deux lignes par match, du point de vue de chaque équipe."""
    common = ["match_id", "date", "league_id", "season_id", "season_start", "tier", "round", "finished"]
    sides = []
    for is_home, me, op in ((1, "h", "a"), (0, "a", "h")):
        t = m[common].copy()
        t["is_home"] = is_home
        t["team_id"] = m["home_id"] if is_home else m["away_id"]
        t["opp_id"] = m["away_id"] if is_home else m["home_id"]
        t["gf"] = m["hg"] if is_home else m["ag"]
        t["ga"] = m["ag"] if is_home else m["hg"]
        t["xgf"], t["xga"] = m[f"{me}_xg"], m[f"{op}_xg"]
        t["shots_f"], t["shots_a"] = m[f"{me}_shots"], m[f"{op}_shots"]
        t["sot_f"], t["sot_a"] = m[f"{me}_sot"], m[f"{op}_sot"]
        t["corners_f"], t["corners_a"] = m[f"{me}_corners"], m[f"{op}_corners"]
        t["poss"] = m[f"{me}_poss"]
        sides.append(t)
    tm = pd.concat(sides, ignore_index=True)
    tm["pts"] = np.where(tm["gf"] > tm["ga"], 3.0, np.where(tm["gf"] == tm["ga"], 1.0, 0.0))
    tm["gd"] = tm["gf"] - tm["ga"]
    tm["xpts"] = xpts_from_xg(tm["xgf"].to_numpy(), tm["xga"].to_numpy())
    tm["btts"] = ((tm["gf"] > 0) & (tm["ga"] > 0)).astype(float)
    tm["over25"] = ((tm["gf"] + tm["ga"]) > 2.5).astype(float)
    tm["cs"] = (tm["ga"] == 0).astype(float)
    tm["fts"] = (tm["gf"] == 0).astype(float)
    unfinished = ~tm["finished"]
    tm.loc[unfinished, ["pts", "gd", "xpts", "btts", "over25", "cs", "fts"]] = np.nan
    return tm.sort_values(["date", "match_id", "is_home"], ascending=[True, True, False]).reset_index(drop=True)


def asof(target: pd.DataFrame, state: pd.DataFrame, by: list[str], cols: list[str]) -> pd.DataFrame:
    """Rattache à chaque ligne de `target` l'état le plus récent strictement antérieur
    au JOUR du match (minuit) : les paris sont décidés le matin, avant le premier coup
    d'envoi, donc aucun résultat du jour même ne doit entrer dans les variables."""
    left = target[["_row"] + by].assign(date=target["date"].dt.normalize()).sort_values("date")
    right = state[["date"] + by + cols].sort_values("date")
    merged = pd.merge_asof(left, right, on="date", by=by, allow_exact_matches=False, direction="backward")
    return merged.set_index("_row")[cols].reindex(target["_row"]).set_axis(target.index)


# ----------------------------------------------------------------------------- blocs de variables
def rolling_state(fin: pd.DataFrame, group: list[str], metrics: list[str], windows, prefix: str) -> pd.DataFrame:
    """État après chaque match : moyennes sur les N derniers matchs (par équipe, ou par équipe et lieu)."""
    st = fin[["date"] + group].copy()
    g = fin.groupby(group, sort=False)
    for n in windows:
        r = g[metrics].rolling(n, min_periods=1).mean().reset_index(level=list(range(len(group))), drop=True)
        for c in metrics:
            st[f"{prefix}{c}_{n}"] = r[c]
    st[f"{prefix}n"] = g.cumcount() + 1
    return st


def ewm_state(fin: pd.DataFrame, metrics: list[str], halflife: float) -> pd.DataFrame:
    """État après chaque match : moyennes pondérées (les matchs récents comptent plus)."""
    st = fin[["date", "team_id"]].copy()
    g = fin.groupby("team_id", sort=False)
    for c in metrics:
        st[f"ewm_{c}"] = g[c].transform(lambda s: s.ewm(halflife=halflife, ignore_na=True).mean())
    return st


def season_state(fin: pd.DataFrame) -> pd.DataFrame:
    """État après chaque match : cumuls de la saison (points, buts, xG, xPts, matchs joués)."""
    st = fin[["date", "team_id", "season_id"]].copy()
    g = fin.groupby(["team_id", "season_id"], sort=False)
    st["ssn_played"] = g.cumcount() + 1
    for c in SEASON_METRICS + ["gd"]:
        st[f"ssn_sum_{c}"] = g[c].cumsum()
    st["ssn_xg_played"] = g["xgf"].transform(lambda s: s.notna().cumsum())
    st["ssn_sum_xgf"] = g["xgf"].transform(lambda s: s.fillna(0).cumsum())
    st["ssn_sum_xga"] = g["xga"].transform(lambda s: s.fillna(0).cumsum())
    st["ssn_sum_xpts"] = g["xpts"].transform(lambda s: s.fillna(0).cumsum())
    st["last_date"] = fin["date"]
    return st


def league_state(fin_matches: pd.DataFrame, window: int) -> pd.DataFrame:
    """Moyennes de la ligue (buts et xG, domicile/extérieur) sur les `window` derniers matchs."""
    f = fin_matches.sort_values(["date", "match_id"])
    st = f[["date", "league_id"]].copy()
    g = f.groupby("league_id", sort=False)
    for src, name in (("hg", "lg_home_goals"), ("ag", "lg_away_goals"),
                      ("h_xg", "lg_home_xg"), ("a_xg", "lg_away_xg")):
        st[name] = g[src].transform(lambda s: s.rolling(window, min_periods=20).mean())
    draws = (f["hg"] == f["ag"]).astype(float)
    st["lg_draw_rate"] = draws.groupby(f["league_id"]).transform(lambda s: s.rolling(window, min_periods=20).mean())
    btts = ((f["hg"] > 0) & (f["ag"] > 0)).astype(float)
    st["lg_btts_rate"] = btts.groupby(f["league_id"]).transform(lambda s: s.rolling(window, min_periods=20).mean())
    ov = ((f["hg"] + f["ag"]) > 2.5).astype(float)
    st["lg_over25_rate"] = ov.groupby(f["league_id"]).transform(lambda s: s.rolling(window, min_periods=20).mean())
    return st


def standings(tm: pd.DataFrame, fin: pd.DataFrame, rolling_pts: pd.Series) -> pd.DataFrame:
    """Classement réel, classement aux points attendus et classement de forme, AVANT chaque match.

    Pour chaque ligue-saison, on reconstitue le classement complet à chaque date de match
    (toutes les équipes, y compris celles qui n'ont pas encore joué), à partir des seuls
    matchs terminés strictement avant cette date.
    """
    st = fin[["date", "team_id", "season_id"]].copy()
    g = fin.groupby(["team_id", "season_id"], sort=False)
    st["c_pts"] = g["pts"].cumsum()
    st["c_gd"] = g["gd"].cumsum()
    st["c_gf"] = g["gf"].cumsum()
    st["c_xpts"] = g["xpts"].transform(lambda s: s.fillna(0).cumsum())
    st["c_n"] = g.cumcount() + 1
    st["form10"] = rolling_pts.loc[fin.index]

    out = []
    for (league, season), grp in tm.groupby(["league_id", "season_id"], sort=False):
        teams = grp["team_id"].unique()
        dates = np.sort(grp["date"].dt.normalize().unique())
        grid = pd.DataFrame({"date": np.repeat(dates, len(teams)), "team_id": np.tile(teams, len(dates))})
        grid["season_id"] = season
        grid["_row"] = np.arange(len(grid))
        s = st[st["season_id"] == season]
        vals = asof(grid, s, ["team_id", "season_id"], ["c_pts", "c_gd", "c_gf", "c_xpts", "c_n"])
        # Forme : 10 derniers matchs, toutes compétitions de la base (peut remonter à la saison précédente)
        form = asof(grid, st, ["team_id"], ["form10"])
        grid = pd.concat([grid, vals.fillna(0.0), form], axis=1)
        n = len(teams)
        key = grid["c_pts"] * 1e6 + grid["c_gd"] * 1e3 + grid["c_gf"]
        grid["rank_pts"] = key.groupby(grid["date"]).rank(ascending=False, method="min")
        grid["rank_xpts"] = grid["c_xpts"].groupby(grid["date"]).rank(ascending=False, method="min")
        grid["rank_form"] = grid["form10"].fillna(grid["form10"].mean()).groupby(grid["date"]).rank(
            ascending=False, method="min")
        leader = grid["c_pts"].groupby(grid["date"]).transform("max")
        grid["gap_leader"] = leader - grid["c_pts"]
        for c in ("rank_pts", "rank_xpts", "rank_form"):
            grid[c + "_pct"] = (grid[c] - 1) / max(n - 1, 1)
        grid["n_teams"] = n
        out.append(grid[["date", "team_id", "season_id", "rank_pts", "rank_xpts", "rank_form",
                         "rank_pts_pct", "rank_xpts_pct", "rank_form_pct", "gap_leader", "n_teams"]])
    table = pd.concat(out, ignore_index=True)
    keys = tm[["team_id", "season_id"]].assign(date=tm["date"].dt.normalize())
    return keys.merge(table, on=["date", "team_id", "season_id"], how="left") \
        .drop(columns=["date", "team_id", "season_id"]).set_axis(tm.index)



# ----------------------------------------------------------------------------- confrontations directes
H2H_COLS = ["h2h_n", "h2h_pts", "h2h_gd", "h2h_goals", "h2h_btts", "h2h_over25", "h2h_xgd",
            "h2h_venue_n", "h2h_venue_pts", "h2h_days_since"]


def h2h_features(m: pd.DataFrame, last_n: int = 5) -> pd.DataFrame:
    """Confrontations directes : les `last_n` derniers matchs TERMINÉS entre les deux mêmes équipes,
    joués un jour STRICTEMENT antérieur (lieu et compétition indifférents), vus du côté de l'équipe
    qui reçoit aujourd'hui.

      h2h_n          nombre de confrontations trouvées (0 à last_n)
      h2h_pts        points moyens pris par l'équipe à domicile aujourd'hui (3 / 1 / 0)
      h2h_gd         différence de buts moyenne (même point de vue)
      h2h_goals      buts moyens par match ; h2h_btts, h2h_over25 : fréquences
      h2h_xgd        différence de xG moyenne (matchs où les xG existent)
      h2h_venue_n / h2h_venue_pts  même chose, seulement quand elle recevait déjà
      h2h_days_since jours depuis la dernière confrontation
    Aucune confrontation : NaN (le boosting sait traiter l'absence), h2h_n = 0.
    Option : config.json > features > h2h (enabled, last_n). Documentation : DOCUMENTATION.md, chapitre 6.7.
    """
    out = pd.DataFrame(np.nan, index=m["match_id"].to_numpy(), columns=H2H_COLS)
    out["h2h_n"] = 0.0
    out["h2h_venue_n"] = 0.0
    lo = np.minimum(m["home_id"].to_numpy(), m["away_id"].to_numpy())
    hi = np.maximum(m["home_id"].to_numpy(), m["away_id"].to_numpy())
    d = m.assign(_lo=lo, _hi=hi, _day=m["date"].dt.normalize()).sort_values(["date", "match_id"])
    hxg = d["h_xg"] if "h_xg" in d else pd.Series(np.nan, index=d.index)
    axg = d["a_xg"] if "a_xg" in d else pd.Series(np.nan, index=d.index)
    d = d.assign(_hxg=hxg, _axg=axg)
    res = {c: [] for c in ["match_id"] + H2H_COLS}
    for _, g in d.groupby(["_lo", "_hi"], sort=False):
        if len(g) < 2:
            continue
        ids = g["match_id"].to_numpy()
        home = g["home_id"].to_numpy()
        hg, ag = g["hg"].to_numpy(float), g["ag"].to_numpy(float)
        hx, ax = g["_hxg"].to_numpy(float), g["_axg"].to_numpy(float)
        fin = g["finished"].to_numpy(bool)
        day = g["_day"].to_numpy()
        for i in range(1, len(g)):
            prior = np.flatnonzero(fin[:i] & (day[:i] < day[i]))[-last_n:]
            if len(prior) == 0:
                continue
            same = home[prior] == home[i]                   # le receveur du jour recevait-il déjà ?
            gf = np.where(same, hg[prior], ag[prior])
            ga = np.where(same, ag[prior], hg[prior])
            xd = np.where(same, hx[prior] - ax[prior], ax[prior] - hx[prior])
            pts = np.where(gf > ga, 3.0, np.where(gf == ga, 1.0, 0.0))
            res["match_id"].append(ids[i])
            res["h2h_n"].append(float(len(prior)))
            res["h2h_pts"].append(pts.mean())
            res["h2h_gd"].append((gf - ga).mean())
            res["h2h_goals"].append((gf + ga).mean())
            res["h2h_btts"].append(((gf > 0) & (ga > 0)).mean())
            res["h2h_over25"].append(((gf + ga) > 2.5).mean())
            res["h2h_xgd"].append(np.nanmean(xd) if np.isfinite(xd).any() else np.nan)
            res["h2h_venue_n"].append(float(same.sum()))
            res["h2h_venue_pts"].append(pts[same].mean() if same.any() else np.nan)
            res["h2h_days_since"].append((day[i] - day[prior[-1]]) / np.timedelta64(1, "D"))
    if res["match_id"]:
        r = pd.DataFrame(res).set_index("match_id")
        out.loc[r.index, H2H_COLS] = r[H2H_COLS].to_numpy()
    return out

# ----------------------------------------------------------------------------- assemblage
def build_team_features(m: pd.DataFrame, cfg: dict) -> pd.DataFrame:
    """Toutes les variables d'une équipe avant chacun de ses matchs (formes, saison, repos,
    promu/relégué, classements).
    """
    fc = cfg["features"]
    windows = fc["windows"]
    tm = team_long(m)
    tm["_row"] = np.arange(len(tm))
    fin = tm[tm["finished"]].sort_values(["date", "match_id"]).copy()

    parts = [tm]

    st_all = rolling_state(fin, ["team_id"], BASE_METRICS, windows, "all_")
    cols_all = [c for c in st_all.columns if c.startswith("all_")]
    parts.append(asof(tm, st_all, ["team_id"], cols_all))

    st_ven = rolling_state(fin, ["team_id", "is_home"], VENUE_METRICS, windows, "ven_")
    cols_ven = [c for c in st_ven.columns if c.startswith("ven_")]
    parts.append(asof(tm, st_ven, ["team_id", "is_home"], cols_ven))

    st_ewm = ewm_state(fin, EWM_METRICS, fc["ewm_halflife"])
    parts.append(asof(tm, st_ewm, ["team_id"], [c for c in st_ewm.columns if c.startswith("ewm_")]))

    st_ssn = season_state(fin)
    ssn_cols = [c for c in st_ssn.columns if c.startswith("ssn_")] + ["last_date"]
    ssn = asof(tm, st_ssn, ["team_id", "season_id"], ssn_cols)
    parts.append(ssn.drop(columns=["last_date"]))

    # Dernier match joué, toutes saisons confondues (repos)
    last = asof(tm, fin[["date", "team_id"]].assign(prev_date=fin["date"]), ["team_id"], ["prev_date"])
    parts.append(pd.DataFrame({"rest_days": (tm["date"] - last["prev_date"]).dt.total_seconds() / 86400.0},
                              index=tm.index))

    out = pd.concat(parts, axis=1)

    # Cumuls de saison -> moyennes ; une équipe qui n'a pas encore joué cette saison = 0 match.
    played = out["ssn_played"].fillna(0)
    out["ssn_played"] = played
    for c in SEASON_METRICS + ["gd"]:
        out[f"ssn_avg_{c}"] = out[f"ssn_sum_{c}"] / played.where(played > 0)
    xg_played = out["ssn_xg_played"].fillna(0)
    for c in ("xgf", "xga", "xpts"):
        out[f"ssn_avg_{c}"] = out[f"ssn_sum_{c}"] / xg_played.where(xg_played > 0)
    out["ssn_luck"] = out["ssn_avg_pts"] - out["ssn_avg_xpts"]   # points pris au-dessus/en dessous des xPts
    out["form_luck10"] = out["all_pts_10"] - out["all_xpts_10"]
    out["momentum_pts"] = out["all_pts_3"] - out["all_pts_10"]
    out["momentum_xgd"] = (out["all_xgf_3"] - out["all_xga_3"]) - (out["all_xgf_10"] - out["all_xga_10"])
    out = out.drop(columns=[c for c in out.columns if c.startswith("ssn_sum_")] + ["ssn_xg_played"])

    # Promu : a joué en division inférieure la saison précédente.
    tier_by_season = tm.groupby(["team_id", "season_start"])["tier"].min()
    prev = pd.Series(list(zip(tm["team_id"], tm["season_start"] - 1)), index=tm.index).map(
        lambda k: tier_by_season.get(k, np.nan))
    out["promoted"] = ((prev > tm["tier"])).astype(float)
    out["relegated"] = ((prev < tm["tier"])).astype(float)
    out["new_in_data"] = prev.isna().astype(float)

    # Classements
    st_form = rolling_state(fin, ["team_id"], ["pts"], [10], "f_")
    out = pd.concat([out, standings(tm, fin, st_form["f_pts_10"])], axis=1)
    out = out.loc[:, ~out.columns.duplicated()]
    return out


def build_match_features(m: pd.DataFrame, odds_raw: pd.DataFrame | None, cfg: dict,
                         decision_labels: list[str] | None = None):
    """Une ligne par match : variables domicile_/exterieur_, différences, ligue, Elo, marché.

    Renvoie (table, colonnes utilisables par les modèles, table des cotes par match).
    """
    fc = cfg["features"]
    tf = build_team_features(m, cfg)

    id_cols = {"match_id", "date", "league_id", "season_id", "season_start", "tier", "round", "finished",
               "is_home", "team_id", "opp_id", "_row", "gf", "ga", "xgf", "xga", "shots_f", "shots_a",
               "sot_f", "sot_a", "corners_f", "corners_a", "poss", "pts", "gd", "xpts", "btts", "over25",
               "cs", "fts"}
    feat_cols = [c for c in tf.columns if c not in id_cols]
    home = tf[tf["is_home"] == 1].set_index("match_id")[feat_cols].add_prefix("home_")
    away = tf[tf["is_home"] == 0].set_index("match_id")[feat_cols].add_prefix("away_")

    base = m.set_index("match_id")
    out = base[["date", "league_id", "league_name", "season_id", "season_name", "season_start", "tier",
                "round", "home_id", "home_name", "away_id", "away_name", "status", "finished",
                "hg", "ag"]].join(home).join(away)

    # Ligue (as-of)
    fin_m = m[m["finished"]]
    lg = league_state(fin_m, fc["league_avg_window"])
    tgt = m[["match_id", "date", "league_id"]].copy()
    tgt["_row"] = np.arange(len(tgt))
    lg_cols = [c for c in lg.columns if c.startswith("lg_")]
    lgf = asof(tgt, lg, ["league_id"], lg_cols)
    lgf.index = m["match_id"].to_numpy()
    out = out.join(lgf)
    out["lg_home_goals"] = out["lg_home_goals"].fillna(1.45)
    out["lg_away_goals"] = out["lg_away_goals"].fillna(1.15)

    # Forces attaque/défense, rétrécies vers la moyenne de la ligue en début de saison
    k = fc["shrink_k"]
    for side, opp, lg_for, lg_against in (("home", "away", "lg_home_goals", "lg_away_goals"),
                                          ("away", "home", "lg_away_goals", "lg_home_goals")):
        n = out[f"{side}_ssn_played"].fillna(0)
        gf = out[f"{side}_ssn_avg_gf"].fillna(0)
        ga = out[f"{side}_ssn_avg_ga"].fillna(0)
        lg_avg = (out["lg_home_goals"] + out["lg_away_goals"]) / 2
        out[f"{side}_att_strength"] = ((n * gf + k * lg_avg) / (n + k)) / lg_avg
        out[f"{side}_def_strength"] = ((n * ga + k * lg_avg) / (n + k)) / lg_avg
        xn = n
        xgf = out[f"{side}_ssn_avg_xgf"].fillna(lg_avg)
        xga = out[f"{side}_ssn_avg_xga"].fillna(lg_avg)
        out[f"{side}_xatt_strength"] = ((xn * xgf + k * lg_avg) / (xn + k)) / lg_avg
        out[f"{side}_xdef_strength"] = ((xn * xga + k * lg_avg) / (xn + k)) / lg_avg
    out["naive_exp_home"] = out["lg_home_goals"] * out["home_att_strength"] * out["away_def_strength"]
    out["naive_exp_away"] = out["lg_away_goals"] * out["away_att_strength"] * out["home_def_strength"]
    out["naive_xexp_home"] = out["lg_home_goals"] * out["home_xatt_strength"] * out["away_xdef_strength"]
    out["naive_xexp_away"] = out["lg_away_goals"] * out["away_xatt_strength"] * out["home_xdef_strength"]

    # Elo
    el = elo_mod.compute_elo(m, cfg, windows=fc["windows"]).set_index("match_id")
    out = out.join(el)

    # Différences utiles
    for c in ("all_pts_5", "all_pts_10", "all_xgf_10", "all_xga_10", "ewm_xgf", "ewm_xga", "ewm_pts",
              "rank_pts", "rank_xpts", "ssn_avg_pts", "ssn_avg_xpts", "rest_days"):
        out[f"diff_{c}"] = out[f"home_{c}"] - out[f"away_{c}"]
    # Écart de « différence de xG » sur 10 matchs (signal brut lisible, utilisé par le balayage)
    out["diff_xgd_10"] = ((out["home_all_xgf_10"] - out["home_all_xga_10"])
                          - (out["away_all_xgf_10"] - out["away_all_xga_10"]))

    # Confrontations directes (désactivables : config.json > features > h2h > enabled)
    h2h_cfg = fc.get("h2h") or {}
    if h2h_cfg.get("enabled", True):
        out = out.join(h2h_features(m, int(h2h_cfg.get("last_n", 5))))
    out["ctx_round"] = out["round"]
    out["ctx_month"] = out["date"].dt.month.astype(float)
    out["ctx_season_progress"] = (out["home_ssn_played"] + out["away_ssn_played"]) / (
        2 * (2 * (out["home_n_teams"].fillna(20) - 1)))

    # Marché
    odds_table = pd.DataFrame(index=out.index)
    if odds_raw is not None and not odds_raw.empty:
        labels = decision_labels or cfg["data"]["decision_labels"]
        odds_table = odds_mod.build_odds_table(odds_raw, cfg, labels)
        mf = odds_mod.market_features(odds_table)
        out = out.join(mf)
    else:
        for c in ("mkt_p_home", "mkt_p_draw", "mkt_p_away", "mkt_p_btts", "mkt_p_over25",
                  "mkt_p_over15", "mkt_p_over35"):
            out[c] = np.nan

    # Cibles
    out["y_total"] = out["hg"] + out["ag"]
    out["y_result"] = np.where(out["hg"] > out["ag"], 0, np.where(out["hg"] == out["ag"], 1, 2)).astype(float)
    out.loc[~out["finished"], "y_result"] = np.nan
    out["y_btts"] = ((out["hg"] > 0) & (out["ag"] > 0)).astype(float).where(out["finished"])
    out["y_over25"] = (out["y_total"] > 2.5).astype(float).where(out["finished"])
    out["y_over15"] = (out["y_total"] > 1.5).astype(float).where(out["finished"])
    out["y_over35"] = (out["y_total"] > 3.5).astype(float).where(out["finished"])

    non_features = {"date", "league_id", "league_name", "season_id", "season_name", "season_start", "round",
                    "home_id", "home_name", "away_id", "away_name", "status", "finished", "hg", "ag",
                    "y_total", "y_result", "y_btts", "y_over25", "y_over15", "y_over35"}
    feature_cols = [c for c in out.columns if c not in non_features]
    out.index.name = "match_id"
    return out.sort_values("date").copy(), feature_cols, odds_table


def market_columns(cols: list[str]) -> list[str]:
    """Liste des colonnes de variables qui viennent des cotes (mkt_*)."""
    return [c for c in cols if c.startswith("mkt_")]
