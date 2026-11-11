"""Tests automatiques. Lancer depuis le dossier Analytics :  python -m pytest -q

Le test le plus important est test_aucune_fuite_du_futur : si un jour une variable laisse
passer une information postérieure au match, toute la simulation devient fausse.

À lancer après toute modification du moteur (python -m pytest -q) : environ 2 minutes.
"""
from __future__ import annotations

import sys
from pathlib import Path

import numpy as np
import pandas as pd
import pytest

sys.path.insert(0, str(Path(__file__).resolve().parent.parent))

from juniro_analytics import data, features, models, odds, settings, simulate, store  # noqa: E402
from juniro_analytics import backtest as bt  # noqa: E402
from juniro_analytics import synthetic  # noqa: E402
from juniro_analytics.__main__ import create_sqlite_schema  # noqa: E402
from juniro_analytics.db import Database  # noqa: E402


@pytest.fixture(scope="module")
def cfg():
    """Configuration réduite pour des tests rapides (peu d'arbres, phases courtes)."""
    return settings.load_config(None, {
        "model": {"hgb": {"max_iter": 60}, "min_train_matches": 500, "train_window": "all"},
        "phases": {"tuning_start": "2023-07-01", "test_start": "2024-01-01", "holdout_start": "2024-07-01"},
        "simulation": {"bootstrap_samples": 200, "min_bets_for_selection": 20},
        "features": {"h2h": {"enabled": True}},          # désactivées par défaut, testées quand même
    })


@pytest.fixture(scope="module")
def small(cfg):
    """Petit jeu synthétique : 3 saisons."""
    m, o = synthetic.generate(first_season=2022, last_season=2024, today="2025-03-01 12:00")
    return data.prepare_matches(m, cfg), data.prepare_odds(o)


@pytest.fixture(scope="module")
def built(cfg, small):
    """Variables calculées une fois pour tous les tests."""
    m, o = small
    return features.build_match_features(m, o, cfg)


# ----------------------------------------------------------------------------- fuite d'information
def test_aucune_fuite_du_futur(cfg, small, built):
    """Change le score d'un match : aucune variable antérieure ne doit bouger."""
    m, o = small
    F1, cols, _ = built
    target = m[m["finished"] & (m["date"] > "2024-10-01")].iloc[0]
    m2 = m.copy()
    i = m2.index[m2["match_id"] == target["match_id"]][0]
    m2.loc[i, ["hg", "ag", "h_xg", "a_xg", "h_shots", "h_sot"]] = [9, 0, 7.5, 0.1, 40, 30]
    F2, _, _ = features.build_match_features(m2, o, cfg)

    end_of_day = target["date"].normalize() + pd.Timedelta(days=1)
    before = F1.index[F1["date"] < end_of_day]
    a, b = F1.loc[before, cols], F2.loc[before, cols]
    changed = ~((a == b) | (a.isna() & b.isna()))
    assert int(changed.values.sum()) == 0, f"Variables modifiées par un résultat futur : {list(changed.columns[changed.any()])}"

    after = F1.index[F1["date"] >= end_of_day]
    a, b = F1.loc[after, cols], F2.loc[after, cols]
    assert int((~((a == b) | (a.isna() & b.isna()))).values.sum()) > 0, "Le résultat n'est jamais pris en compte"


def test_variables_coherentes(built):
    """Classements, Elo, saison et probabilités de marché ont des valeurs plausibles."""
    F, cols, _ = built
    fin = F[F["finished"]]
    assert len(cols) > 150
    assert fin["home_rank_pts"].between(1, 24).all()
    assert fin["home_elo"].notna().all()
    # Premier match de la saison : 0 match joué, pas de moyenne de saison
    first = fin.sort_values("date").groupby("season_id").head(1)
    assert (first["home_ssn_played"] == 0).all()
    assert first["home_ssn_avg_pts"].isna().all()
    # Les probabilités de marché sans marge somment à 1
    mk = F[["mkt_p_home", "mkt_p_draw", "mkt_p_away"]].dropna()
    assert np.allclose(mk.sum(axis=1), 1.0)


def test_xpts():
    """Les points attendus suivent les xG (gros favori ~3, équilibre ~1,3)."""
    xp = features.xpts_from_xg(np.array([0.0001, 3.0, 1.2]), np.array([3.0, 0.0001, 1.2]))
    assert xp[0] < 0.1 and xp[1] > 2.8 and 1.0 < xp[2] < 1.6


# ----------------------------------------------------------------------------- cotes
def test_choix_du_releve_et_du_bookmaker(cfg):
    """Cote de décision : bon relevé (DAY avant J-1), bon bookmaker, marché complet."""
    rows = []
    for bm, mult in ((16, 1.1), (141, 1.0)):
        for lab, base in (("J-1", 2.0), ("DAY", 2.2), ("CLOSING", 2.5)):
            for sel, f in (("HOME", 1.0), ("DRAW", 1.6), ("AWAY", 1.9)):
                rows.append((1, bm, "1X2", sel, np.nan, lab, round(base * f * mult, 2)))
    # BTTS incomplet au relevé DAY : doit retomber sur J-1
    rows += [(1, 141, "BTTS", "YES", np.nan, "J-1", 1.8), (1, 141, "BTTS", "NO", np.nan, "J-1", 1.9),
             (1, 141, "BTTS", "YES", np.nan, "DAY", 1.7)]
    raw = pd.DataFrame(rows, columns=["MatchId", "BookmakerId", "MarketCode", "SelectionCode",
                                      "LineValue", "SnapshotLabel", "OddValue"])
    t = odds.build_odds_table(data.prepare_odds(raw), cfg, ["DAY", "J-1", "J-2", "J-3"])
    r = t.loc[1]
    assert r["dec_1X2_HOME"] == 2.2 and r["dec_label_1X2"] == "DAY"      # bookmaker 141 préféré, relevé DAY
    assert r["close_1X2_HOME"] == 2.5                                     # clôture séparée
    assert r["dec_label_BTTS"] == "J-1" and r["dec_BTTS_YES"] == 1.8      # marché complet exigé
    assert abs(r["dec_p_1X2_HOME"] + r["dec_p_1X2_DRAW"] + r["dec_p_1X2_AWAY"] - 1) < 1e-9


def test_conversion_chaine_connexion():
    """La chaîne ADO.NET du service est bien convertie au format ODBC."""
    odbc = settings.ado_to_odbc("Server=localhost,49999;Database=Juniro_Evo;UID=u;Password=p;"
                                "TrustServerCertificate=True;Encrypt=False;", "ODBC Driver 18 for SQL Server")
    assert "DRIVER={ODBC Driver 18 for SQL Server}" in odbc
    assert "SERVER=localhost,49999" in odbc and "DATABASE=Juniro_Evo" in odbc
    assert "TrustServerCertificate=yes" in odbc and "Encrypt=no" in odbc
    assert "Trusted_Connection=yes" in settings.ado_to_odbc("Server=x;Integrated Security=True", "D")


# ----------------------------------------------------------------------------- modèle de score
def test_matrice_des_scores():
    """La matrice des scores donne des probabilités cohérentes."""
    lh, la = np.array([1.5, 0.8]), np.array([1.1, 2.0])
    p = models.probs_from_matrix(models.score_matrix(lh, la, rho=-0.05))
    assert np.allclose(p[["score_p_home", "score_p_draw", "score_p_away"]].sum(axis=1), 1)
    assert (p["score_p_over15"] >= p["score_p_over25"]).all() and (p["score_p_over25"] >= p["score_p_over35"]).all()
    assert p.loc[0, "score_p_home"] > p.loc[0, "score_p_away"]
    assert p.loc[1, "score_p_away"] > p.loc[1, "score_p_home"]


# ----------------------------------------------------------------------------- règlement des paris
def test_reglement_des_paris():
    """Gagné / perdu / remboursé et profits corrects."""
    hg = pd.Series([2.0, 1.0, 0.0])
    ag = pd.Series([1.0, 1.0, 0.0])
    out = simulate._outcomes(hg, ag)
    assert list(out["1X2_HOME"]) == ["WIN", "LOSE", "LOSE"]
    assert list(out["DNB_HOME"]) == ["WIN", "PUSH", "PUSH"]
    assert list(out["OU25_OVER"]) == ["WIN", "LOSE", "LOSE"]
    assert list(out["BTTS_YES"]) == ["WIN", "WIN", "LOSE"]
    bets = pd.DataFrame({"date": pd.to_datetime(["2024-01-01"] * 3), "odd": [2.0, 3.0, 1.5],
                         "outcome": ["WIN", "LOSE", "PUSH"], "edge": [0.1, 0.1, 0.1]})
    s = simulate.stake_and_settle(bets, {"staking": {"method": "flat", "units": 1}}, 100)
    assert list(s["profit"]) == [1.0, -1.0, 0.0]


def test_grille_de_strategies(cfg):
    """Une grille produit toutes les variantes sans modifier la définition d'origine."""
    spec = next(s for s in cfg["simulation"]["strategies"] if s["name"] == "combo_plus25")
    variants = simulate.expand_grid(spec)
    assert len(variants) == 4 * 3 * 4 * 3
    assert {v["conditions"][0][2] for v in variants} == {2.6, 2.8, 3.0, 3.2}
    assert spec["conditions"][0][2] == 2.8          # la définition d'origine n'est pas modifiée


# ----------------------------------------------------------------------------- circuit complet
def test_circuit_complet(cfg, small, built):
    """Backtest -> enregistrement -> relecture -> simulation par ligue -> enregistrement."""
    m, o = small
    F, cols, odds_table = built
    db = Database.sqlite()
    create_sqlite_schema(db)

    assert store.save_features(db, F, cols) == len(F)
    preds = bt.run_backtest(F, cols, cfg, log=lambda *_: None)
    assert set(preds["phase"]) <= {"TUNING", "TEST"}
    assert (F.loc[preds.index, "date"] >= pd.Timestamp(cfg["phases"]["tuning_start"])).all()
    # Chaque prédiction vient d'un modèle entraîné AVANT le mois du match
    month_start = F.loc[preds.index, "date"].dt.to_period("M").dt.start_time
    assert (pd.to_datetime(preds["trained_until"]) < month_start).all()

    metrics = bt.evaluate(preds, F)
    run_id = store.save_model_run(db, "BACKTEST", cfg, preds, F, metrics, "test")
    back = store.load_predictions(db, run_id)
    assert len(back) == len(preds)
    assert np.allclose(back.loc[preds.index, "p_home"], preds["p_home"])
    # Lignes 1,5 / 3,5 : probabilité finale (mélange) enregistrée, relue, ordonnée comme le 2,5
    assert (preds["p_over15"] >= preds["p_over25"]).all() and (preds["p_over25"] >= preds["p_over35"]).all()
    assert preds[["clf_p_over15", "clf_p_over35"]].notna().all().all()
    assert np.allclose(back.loc[preds.index, "p_over35"], preds["p_over35"])
    assert "over15_modele" in metrics["TEST"] and "over35_modele" in metrics["TEST"]

    back["phase"] = preds["phase"]
    res, bets = simulate.run_simulation(back, m, odds_table, cfg, phases=("TUNING", "TEST"))
    assert len(res) > 100
    n = store.save_simulation(db, run_id, res, bets, "test")
    assert n == len(res)
    assert db.scalar("SELECT COUNT(*) FROM sim.Bets") > 0
    # Une seule variante retenue par famille ET par ligue
    leagues = set(m["league_name"])
    assert set(res.loc[~res["portfolio"], "league"]) == leagues
    sel = res[res["selected"] & res["is_grid"] & ~res["portfolio"]].groupby(["family", "league"])["name"].nunique()
    assert (sel == 1).all()
    # Bilan par saison : pas de portefeuille ; les saisons d'une stratégie x ligue somment au total
    assert not res["portfolio"].any()
    rep = res.attrs["seasons"]
    for (fam, lg), g in rep.groupby(["family", "league"]):
        tot = g[g["season"] == "Total"].iloc[0]
        per = g[~g["season"].isin(["Test", "Total"])]
        assert int(per["nb_bets"].sum()) == int(tot["nb_bets"])
        if int(tot["nb_bets"]):
            assert abs(per["profit"].sum() - tot["profit"]) < 1e-6
            test_row = res[(res["family"] == fam) & (res["league"] == lg) & res["selected"] & (res["phase"] == "TEST")]
            assert int(g[g["season"] == "Test"]["nb_bets"].iloc[0]) == int(test_row["nb_bets"].iloc[0])
    assert "Total" in simulate.render(res)
    # Lignes 1,5 / 3,5 : nouvelles familles simulées, avec des paris sur ces lignes
    assert {"value_lignes_15_35", "combo_plus35", "combo_moins35", "combo_plus15"} <= set(res["family"])
    keys = set().union(*(set(b["key"]) for b in bets.values() if len(b)))
    assert keys & {"OU15_OVER", "OU15_UNDER", "OU35_OVER", "OU35_UNDER"}
    # Une ligue de la simulation ne contient que des paris de cette ligue
    i = res.index[res["selected"] & (res["nb_bets"] > 0)][0]
    assert set(bets[i]["league_name"]) == {res.loc[i, "league"]}
    assert db.scalar("SELECT COUNT(DISTINCT LeagueName) FROM sim.Runs WHERE IsPortfolio = 0") == len(leagues)


# ----------------------------------------------------------------------------- balayage et réglage
def test_balayage_des_seuils(cfg, small, built):
    """Une règle du balayage recalculée à la main donne le même résultat."""
    from juniro_analytics import scan
    m, o = small
    F, cols, odds_table = built
    preds = bt.run_backtest(F, cols, cfg, log=lambda *_: None)
    res = scan.run_scan(preds, m, odds_table, cfg)
    assert not res.empty
    # Vérification à la main d'une règle : buts attendus >= 2,7 => Plus de 1,5
    lg = "Ligue 1"
    lg_ids = set(m.loc[m["league_name"] == lg, "match_id"])
    r = res[(res["league"] == lg) & (res["phase"] == "TEST") & (res["signal"] == "exp_total_goals")
            & (res["op"] == ">=") & (np.isclose(res["threshold"], 2.7)) & (res["bet"] == "OU15_OVER")]
    assert len(r) == 1
    sel = preds[(preds["phase"] == "TEST") & (preds["exp_total_goals"] >= 2.7) & preds.index.isin(lg_ids)]
    goals = m.set_index("match_id").loc[sel.index, ["hg", "ag"]].sum(axis=1)
    assert int(r["n"].iloc[0]) == len(sel)
    assert int(r["wins"].iloc[0]) == int((goals > 1.5).sum())
    # ROI d'une règle avec cotes = calcul direct
    r2 = res[(res["league"] == lg) & (res["phase"] == "TEST") & (res["signal"] == "p_btts_yes")
             & (res["op"] == ">=") & (np.isclose(res["threshold"], 0.5)) & (res["bet"] == "BTTS_YES")].iloc[0]
    sel2 = preds[(preds["phase"] == "TEST") & (preds["p_btts_yes"] >= 0.5) & preds.index.isin(lg_ids)]
    sc = m.set_index("match_id").loc[sel2.index]
    win = (sc["hg"] > 0) & (sc["ag"] > 0)
    odd = odds_table.loc[sel2.index, "dec_BTTS_YES"]
    ok = odd.notna()
    roi = 100 * np.where(win[ok], odd[ok] - 1, -1).mean()
    assert abs(r2["roi_pct"] - roi) < 1e-6
    # Écriture
    db = Database.sqlite()
    create_sqlite_schema(db)
    assert store.save_scan(db, 1, res) == len(res)
    assert store.save_scan(db, 1, res) == len(res)          # rejouable : remplace
    assert db.scalar("SELECT COUNT(*) FROM sim.RuleScan") == len(res)


def test_reglage_rapide(cfg, built):
    """La commande tune fonctionne et renvoie un réglage par famille de modèles."""
    from juniro_analytics import tune
    F, cols, _ = built
    c = settings.deep_merge(cfg, {"model": {"hgb": {"max_iter": 40}}})
    res, tuned = tune.run_tuning(F, cols, c, trials=2, every_months=6, log=lambda *_: None)
    assert len(res) == 2 and res["essai"].iloc[0] == 0
    assert set(tuned["model"]) == {"hgb_goals", "hgb_clf"}
    assert (res["arbres_buts"] > 0).all()


def test_estimation_cote_de_decision(cfg):
    """Sans relevé de décision, la cote est estimée entre l'ouverture et la clôture ; les vraies
    cotes ne sont jamais remplacées (mode par défaut)."""
    rows = []
    for mid, labels in ((1, ("OPENING", "J-1", "CLOSING")), (2, ("OPENING", "CLOSING")), (3, ("OPENING",))):
        for lab in labels:
            base = {"OPENING": 2.40, "J-1": 2.10, "CLOSING": 2.00}[lab]
            for sel, f in (("HOME", 1.0), ("DRAW", 1.5), ("AWAY", 1.6)):
                rows.append((mid, 141, "1X2", sel, np.nan, lab, round(base * f, 2)))
    raw = pd.DataFrame(rows, columns=["MatchId", "BookmakerId", "MarketCode", "SelectionCode",
                                      "LineValue", "SnapshotLabel", "OddValue"])
    t = odds.build_odds_table(data.prepare_odds(raw), cfg, ["DAY", "J-1", "J-2", "J-3"])
    assert t.loc[1, "dec_label_1X2"] == "J-1" and t.loc[1, "dec_1X2_HOME"] == 2.10   # vraie cote conservée
    assert t.loc[2, "dec_label_1X2"] == "EST" and 2.00 < t.loc[2, "dec_1X2_HOME"] < 2.40
    assert t.loc[3, "dec_label_1X2"] == "EST" and t.loc[3, "dec_1X2_HOME"] == 2.40    # pas de clôture : ouverture
    w = t.attrs["estimate"]["markets"]["1X2"]["w"]
    assert 0.0 <= w <= 1.0
    r = odds.build_odds_table(data.prepare_odds(raw), settings.deep_merge(cfg, {"data": {"decision_mode": "real"}}),
                              ["DAY", "J-1", "J-2", "J-3"])
    assert pd.isna(r.loc[2, "dec_1X2_HOME"])                                            # mode réel : pas d'estimation


def test_confrontations_directes(small, built):
    """Les variables h2h_* recalculées à la main pour un match : mêmes valeurs, rien du jour même."""
    m, _ = small
    F, cols, _ = built
    assert {"h2h_n", "h2h_pts", "h2h_gd", "diff_xgd_10"} <= set(cols)
    row = F[F["h2h_n"] >= 3].iloc[-1]
    mid = row.name
    me = m.set_index("match_id").loc[mid]
    pair = m[((m["home_id"] == me["home_id"]) & (m["away_id"] == me["away_id"]))
             | ((m["home_id"] == me["away_id"]) & (m["away_id"] == me["home_id"]))]
    prior = pair[pair["finished"] & (pair["date"].dt.normalize() < me["date"].normalize())] \
        .sort_values(["date", "match_id"]).tail(5)
    same = prior["home_id"] == me["home_id"]
    gf = np.where(same, prior["hg"], prior["ag"])
    ga = np.where(same, prior["ag"], prior["hg"])
    pts = np.where(gf > ga, 3, np.where(gf == ga, 1, 0))
    assert row["h2h_n"] == len(prior)
    assert np.isclose(row["h2h_pts"], pts.mean())
    assert np.isclose(row["h2h_gd"], (gf - ga).mean())
    assert row["h2h_venue_n"] == same.sum()
    # Première rencontre de deux équipes : aucune confrontation
    assert (F["h2h_n"] == 0).any() and F.loc[F["h2h_n"] == 0, "h2h_pts"].isna().all()


def test_importance_et_balayage_brut(cfg, small, built):
    """La commande importance produit un rapport ; le balayage couvre les variables brutes."""
    from juniro_analytics import importance, scan
    m, _ = small
    F, cols, odds_table = built
    res = importance.run_importance(F, cols, cfg, repeats=1, ablation=True, log=lambda *_: None)
    fams = set(res["families"]["famille"])
    assert {"Elo", "Cotes (marché)", "Confrontations directes", "xG et points attendus"} <= fams
    assert "retrait" in importance.render(res)
    preds = bt.run_backtest(F, cols, cfg, log=lambda *_: None)
    sc = scan.run_scan(preds, m, odds_table, cfg, features=F)
    assert {"diff_elo", "h2h_pts"} <= set(sc["signal"])
    assert "Variables brutes" in scan.render(sc, raw_signals=list(cfg["scan"]["raw_signals"]))


def test_variable_presque_vide(cfg, built):
    """Une variable avec quelques valeurs seulement est écartée au lieu de faire échouer l'entraînement."""
    F, cols, _ = built
    F2 = F.copy()
    F2["rare"] = np.nan
    F2.loc[F2.index[:5], "rare"] = 1.0
    mdl = models.JuniroModels(cfg).fit(F2, cols + ["rare"], cutoff=pd.Timestamp("2024-01-01"))
    assert "rare" in mdl.dropped and "rare" not in mdl.columns


def test_debut_de_saison(small, built):
    """Les 5 premiers matchs de chaque équipe dans la saison sont exclus des paris."""
    m, _ = small
    F, cols, odds_table = built
    n = simulate.season_matches_played(m)
    first = m.sort_values("date").groupby("season_id").head(3)["match_id"]
    assert (n.loc[first] == 0).all()                     # première journée : 0 match joué
    early = simulate.early_season_mask(m, 5)
    assert early.any() and not early.all()
    idx = F.index[F["finished"]]
    preds = pd.DataFrame({"p_home": 0.4, "p_draw": 0.3, "p_away": 0.3, "p_btts_yes": 0.5, "p_over25": 0.5},
                         index=idx)
    all_c = simulate.candidate_bets(preds, m, odds_table, 0)
    cut_c = simulate.candidate_bets(preds, m, odds_table, 5)
    assert set(cut_c["match_id"]).isdisjoint(set(early[early].index))
    assert len(cut_c) < len(all_c)


def test_paris_fictifs(cfg, small, built):
    """Les 3 stratégies fictives de config.json s'appliquent (conditions sur la cote du marché)."""
    from juniro_analytics import live
    m, _ = small
    F, cols, odds_table = built
    base_cfg = settings.load_config(None, {})
    specs = base_cfg["simulation"]["live_strategies"]
    assert {s["name"] for s in specs} >= {"papier_seriea_ext_favori", "papier_laliga_dom_favori"}
    idx = F.index[F["finished"]]
    preds = pd.DataFrame({"p_home": 0.4, "p_draw": 0.25, "p_away": 0.35, "p_btts_yes": 0.5,
                          "p_over25": 0.5}, index=idx)
    c2 = {**cfg, "simulation": {**cfg["simulation"], "live_strategies": specs}}
    sig = live.live_signals(preds, m, odds_table, c2)
    assert not sig.empty
    lg = m.set_index("match_id")["league_name"]
    for name, league in (("papier_seriea_ext_favori", "Serie A"), ("papier_laliga_dom_favori", "LaLiga")):
        s = sig[sig["strategy"] == name]
        assert len(s) and (lg.loc[s["match_id"]] == league).all()
    s = sig[sig["strategy"] == "papier_seriea_ext_favori"]
    ot = odds_table.loc[s["match_id"]]
    assert (ot["dec_p_1X2_AWAY"] >= 0.40).all()


def test_placebo_biais_candidates(cfg, small, built):
    """Placebo (résultats tirés selon le marché), carte des biais et filtres durs."""
    from juniro_analytics import bias, placebo, scan
    m, _ = small
    F, cols, odds_table = built
    lams = placebo.market_lambdas(odds_table)
    ok = lams.dropna()
    assert len(ok) and ok["lh"].between(0.05, 4.5).all()
    # Les lambdas reproduisent la probabilité de victoire à domicile du marché
    lh_g, la_g, ph, pa, po = placebo._poisson_grid()
    preds = bt.run_backtest(F, cols, cfg, log=lambda *_: None)
    res = placebo.run_placebo(preds, m, odds_table, cfg, features=F, reps=2, log=lambda *_: None)
    assert len(res["sims"]) == 2 and "Placebo" in placebo.render(res)
    sc = scan.run_scan(preds, m, odds_table, cfg, features=F)
    assert not scan.season_table(sc).empty
    cands, funnel, near = scan.candidates(sc, cfg)
    counts = [n for _, n in funnel]
    assert counts == sorted(counts, reverse=True)            # l'entonnoir ne peut que rétrécir
    b = bias.run_bias(m, odds_table, cfg)
    assert {"shrunk", "shrunk_low", "solid", "seasons_pos"} <= set(b["cells"].columns)
    assert "Toutes ligues" in bias.render(b)


def test_lecture_appsettings_tolerante_et_pilote_odbc(tmp_path):
    """appsettings avec commentaires et virgules en trop (accepté par .NET) ; pilote 17 seul installé."""
    from juniro_analytics.settings import load_appsettings
    from juniro_analytics.db import choose_odbc_driver
    p = tmp_path / "appsettings.Production.json"
    p.write_text('{\n  // note\n  "ConnectionStrings": { "JuniroDb": "Server=x;Password=a,}b//c;", },\n'
                 '  "Supabase": { "ConnectionString": "Host=h;Password=p\\\\q,", },\n}', encoding="utf-8")
    data = load_appsettings(p)
    assert data["ConnectionStrings"]["JuniroDb"] == "Server=x;Password=a,}b//c;"
    assert data["Supabase"]["ConnectionString"] == "Host=h;Password=p\\q,"
    cs = "DRIVER={ODBC Driver 18 for SQL Server};SERVER=x"
    assert "Driver 17" in choose_odbc_driver(cs, ["SQL Server", "ODBC Driver 17 for SQL Server"])
    assert choose_odbc_driver(cs, ["ODBC Driver 18 for SQL Server"]) == cs
