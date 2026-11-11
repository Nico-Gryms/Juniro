"""Ligne de commande Juniro Analytics.

  python -m juniro_analytics check                    vérifie la connexion, les vues et le volume de données
  python -m juniro_analytics features                 recalcule et enregistre feat.MatchFeatures
  python -m juniro_analytics backtest                 walk-forward + simulation de toutes les stratégies
  python -m juniro_analytics simulate --run-id 12     rejoue seulement la simulation sur un backtest existant
  python -m juniro_analytics scan [--run-id 12]       balayage des seuils (buts attendus >= 2,7 => +1,5 ...)
  python -m juniro_analytics tune [--trials 16]       réglage fin des modèles, écrit tuned.json
  python -m juniro_analytics daily                    matchs à venir : prédictions + paris proposés
  python -m juniro_analytics tune-elo                 cherche les meilleurs réglages Elo
  python -m juniro_analytics importance               quelles familles de variables comptent (Elo, xG, cotes, H2H...)
  python -m juniro_analytics placebo [--reps 10]      combien de « stratégies » le hasard produit (balayage sur faux résultats)
  python -m juniro_analytics biais                    carte des biais de prix : ligue x pari x tranche de cote
  python -m juniro_analytics demo                     tout le circuit sur données synthétiques, sans base

Options communes : --config chemin, --connection "chaîne", --source db|synthetic
Comparer avec / sans confrontations directes : backtest --no-h2h --label "sans H2H"

Place dans la chaîne : point d'entrée. Chaque commande enchaîne les autres modules :
  données (data.py) -> variables (features.py, elo.py, odds.py) -> modèles (models.py)
  -> walk-forward (backtest.py) -> paris (simulate.py, scan.py) -> base (store.py, db.py).
Appelé par : Services/AnalyticsJob.cs (commande daily, chaque matin) ou à la main.
Pour ajouter une commande : écrire une fonction cmd_xxx, l'ajouter à `choices` et au
dictionnaire de main(). Documentation : DOCUMENTATION.md, chapitre 5.
"""
from __future__ import annotations

import argparse
import sys
import time
import traceback

import pandas as pd

from . import __version__
from . import backtest as bt
from . import data, features, live, odds as odds_mod, scan, settings, simulate, store
from .db import Database


def log(msg: str = ""):
    """Affiche une ligne immédiatement (le service C# lit cette sortie en direct)."""
    print(msg, flush=True)


# ----------------------------------------------------------------------------- chargement commun
def open_db(args, cfg) -> Database:
    """Ouvre la base : SQL Server (chaîne lue dans appsettings) ou SQLite en mémoire pour --source synthetic."""
    if args.source == "synthetic":
        db = Database.sqlite()
        create_sqlite_schema(db)
        return db
    return Database.sqlserver(settings.resolve_connection(cfg, args.connection))


def load_data(args, cfg, db):
    """Étape 1 du calcul : lit les matchs et les cotes (vues core.*) puis les met en forme (data.py)."""
    t = time.time()
    if args.source == "synthetic":
        from . import synthetic
        raw_m, raw_o = synthetic.generate(today=args.today or "2026-11-01 12:00")
    else:
        raw_m, raw_o = data.load_from_db(db)
    m = data.prepare_matches(raw_m, cfg)
    o = data.prepare_odds(raw_o)
    log(f"Données : {len(m):,} matchs ({int(m['finished'].sum()):,} terminés), {len(o):,} cotes "
        f"— {time.time() - t:.0f} s".replace(",", " "))
    return m, o


def build(args, cfg, m, o):
    """Étape 2 du calcul : construit les ~280 variables par match (features.py)."""
    t = time.time()
    F, cols, odds_table = features.build_match_features(m, o, cfg)
    log(f"Variables : {len(cols)} par match — {time.time() - t:.0f} s")
    log(odds_mod.render_estimate(odds_table.attrs.get("estimate")))
    return F, cols, odds_table


def now_paris(args) -> pd.Timestamp:
    """Date et heure de référence : --today si fourni (tests), sinon l'heure de Paris."""
    if args.today:
        return pd.Timestamp(args.today)
    return pd.Timestamp.now(tz="Europe/Paris").tz_localize(None)


# ----------------------------------------------------------------------------- commandes
def cmd_features(args, cfg):
    """Commande « features » : recalcule les variables et réécrit feat.MatchFeatures."""
    db = open_db(args, cfg)
    m, o = load_data(args, cfg, db)
    F, cols, _ = build(args, cfg, m, o)
    n = store.save_features(db, F, cols)
    log(f"feat.MatchFeatures : {n} lignes enregistrées.")


def cmd_backtest(args, cfg):
    """Commande « backtest » : variables -> walk-forward mois par mois -> précision vs marché
    -> enregistrement (ml.*) -> simulation des stratégies et balayage des seuils (sim.*).
    """
    db = open_db(args, cfg)
    m, o = load_data(args, cfg, db)
    F, cols, odds_table = build(args, cfg, m, o)
    if not args.no_save_features:
        store.save_features(db, F, cols)
    log(f"\nWalk-forward — fenêtre '{cfg['model']['train_window']}', "
        f"variables de marché : {'oui' if cfg['model']['use_market_features'] else 'non'}, "
        f"réglage : {'tuned.json' if cfg.get('_tuned') else 'config.json'}")
    preds = bt.run_backtest(F, cols, cfg, include_holdout=args.include_holdout, log=log)
    skip = int(cfg["simulation"].get("skip_first_season_matches", 0) or 0)
    early = simulate.early_season_mask(m, skip) if skip else None
    metrics = bt.evaluate(preds, F, early)
    log("\n" + bt.render_metrics(metrics))
    run_id = store.save_model_run(db, "BACKTEST", cfg, preds, F, metrics, args.label)
    log(f"\nml.ModelRuns : RunId = {run_id}  ({len(preds)} prédictions)")
    res = None
    if not args.no_simulate:
        res = _simulate(db, cfg, run_id, preds, m, odds_table, args.label)
        _scan(db, cfg, run_id, preds, m, odds_table, F)
    return db, run_id, res


def _scan(db, cfg, run_id, preds, m, odds_table, F=None):
    """Balayage des seuils (scan.py), ligue par ligue, enregistré dans sim.RuleScan si la table existe.
    F (table des variables) permet de balayer aussi les variables brutes (config.json > scan > raw_signals)."""
    phases = tuple(ph for ph in ("TUNING", "TEST", "HOLDOUT") if ph in set(preds["phase"]))
    t = time.time()
    res = scan.run_scan(preds, m, odds_table, cfg, phases=phases, features=F)
    if db.table_exists("sim", "RuleScan"):
        n = store.save_scan(db, run_id, res)
        log(f"\nBalayage des seuils : {n} lignes dans sim.RuleScan — {time.time() - t:.0f} s")
    else:
        log("\nBalayage des seuils : table sim.RuleScan absente, résultats non enregistrés "
            "(exécutez SQL/5_Analytique/A4 puis la commande scan).")
    log(scan.render(res, raw_signals=list((cfg["scan"].get("raw_signals") or {}) if F is not None else []), cfg=cfg))
    return res


def _simulate(db, cfg, run_id, preds, m, odds_table, label):
    """Simulation des stratégies (simulate.py), ligue par ligue, enregistrée dans sim.Runs / sim.Bets."""
    phases = sorted(set(preds["phase"]) - {"BURN_IN"}, key=["TUNING", "TEST", "HOLDOUT"].index)
    t = time.time()
    res, bets = simulate.run_simulation(preds, m, odds_table, cfg, phases=tuple(phases))
    n = store.save_simulation(db, run_id, res, bets, label)
    log(f"\nSimulation : {n} lignes dans sim.Runs ({res['family'].nunique()} familles x "
        f"{res.loc[~res['portfolio'], 'league'].nunique()} ligues) — {time.time() - t:.0f} s\n")
    log(simulate.render(res))
    return res


def load_run(args, cfg, db):
    """Prédictions d'un backtest existant (le dernier si --run-id est omis) + données associées."""
    run_id = args.run_id or db.scalar("SELECT MAX(RunId) FROM ml.ModelRuns WHERE RunType = 'BACKTEST'")
    if run_id is None:
        raise ValueError("Aucun backtest en base : lancez d'abord la commande backtest.")
    log(f"Backtest utilisé : RunId = {run_id}")
    preds = store.load_predictions(db, int(run_id))
    m, o = load_data(args, cfg, db)
    odds_table = odds_mod.build_odds_table(o, cfg, cfg["data"]["decision_labels"])
    dates = m.set_index("match_id")["date"].reindex(preds.index)
    preds = preds[dates.notna()].copy()
    preds["phase"] = bt.phase_of(dates[dates.notna()], cfg)
    if not args.include_holdout:
        preds = preds[preds["phase"] != "HOLDOUT"]
    return int(run_id), preds, m, odds_table


def cmd_simulate(args, cfg):
    """Commande « simulate » : rejoue les stratégies sur un backtest existant, sans réentraîner."""
    db = open_db(args, cfg)
    run_id, preds, m, odds_table = load_run(args, cfg, db)
    _simulate(db, cfg, run_id, preds, m, odds_table, args.label)


def cmd_scan(args, cfg):
    """Commande « scan » : rejoue le balayage des seuils sur un backtest existant
    (variables recalculées pour balayer aussi les variables brutes)."""
    db = open_db(args, cfg)
    run_id, preds, m, odds_table = load_run(args, cfg, db)
    F = None
    if cfg["scan"].get("raw_signals"):
        # Les variables brutes (Elo, xG, H2H...) ne dépendent pas des cotes : calcul sans elles, plus rapide.
        F, _, _ = features.build_match_features(m, None, cfg)
    _scan(db, cfg, run_id, preds, m, odds_table, F)


def cmd_tune(args, cfg):
    """Commande « tune » : essaie plusieurs réglages des modèles et écrit le meilleur dans tuned.json."""
    from . import tune
    db = open_db(args, cfg)
    m, o = load_data(args, cfg, db)
    F, cols, _ = build(args, cfg, m, o)
    res, tuned = tune.run_tuning(F, cols, cfg, trials=args.trials, every_months=args.every, log=log)
    log("\n" + tune.render(res))
    if args.source == "db":
        path = tune.save_tuned(cfg, tuned, res)
        log(f"\nRéglage enregistré : {path}\nRelancez maintenant : backtest --label \"réglé\" "
            f"(c'est la période TEST qui dira si le gain est réel).")


def cmd_placebo(args, cfg):
    """Commande « placebo » : le balayage rejoué sur des résultats tirés au hasard (cotes justes),
    pour savoir combien de « stratégies gagnantes » le hasard produit à lui seul (placebo.py)."""
    from . import placebo
    db = open_db(args, cfg)
    run_id, preds, m, odds_table = load_run(args, cfg, db)
    F = features.build_match_features(m, None, cfg)[0] if cfg["scan"].get("raw_signals") else None
    t = time.time()
    res = placebo.run_placebo(preds, m, odds_table, cfg, features=F, reps=args.reps, log=log)
    log("\n" + placebo.render(res) + f"\n\n({time.time() - t:.0f} s)")


def cmd_bias(args, cfg):
    """Commande « biais » : carte ligue x pari x tranche de cote, saison par saison (bias.py)."""
    from . import bias
    db = open_db(args, cfg)
    m, o = load_data(args, cfg, db)
    odds_table = odds_mod.build_odds_table(o, cfg, cfg["data"]["decision_labels"])
    log(odds_mod.render_estimate(odds_table.attrs.get("estimate")))
    res = bias.run_bias(m, odds_table, cfg, include_holdout=args.include_holdout)
    log("\n" + bias.render(res))
    path = bias.save_csv(res, cfg, now_paris(args))
    log(f"\nCarte complète : {path}")


def cmd_importance(args, cfg):
    """Commande « importance » : quelles familles de variables (Elo, xG, cotes, confrontations
    directes...) et quelles variables les modèles utilisent vraiment (importance.py)."""
    from . import importance
    db = open_db(args, cfg)
    m, o = load_data(args, cfg, db)
    F, cols, _ = build(args, cfg, m, o)
    t = time.time()
    res = importance.run_importance(F, cols, cfg, repeats=args.repeats, ablation=not args.no_ablation, log=log)
    log("\n" + importance.render(res) + f"\n\n({time.time() - t:.0f} s)")


def cmd_daily(args, cfg, db=None):
    """Commande « daily » (lancée chaque matin par le service) : variables à jour -> modèles du mois
    -> prédictions des matchs à venir (ml.*) -> paris proposés par les stratégies actives (sim.LiveSignals).
    """
    db = db or open_db(args, cfg)
    now = now_paris(args)
    m, o = load_data(args, cfg, db)
    F, cols, odds_table = build(args, cfg, m, o)
    if not args.no_save_features:
        store.save_features(db, F, cols)
    models = live.get_models(F, cols, cfg, now, retrain=args.retrain, log=log)
    up = live.upcoming(F, now, cfg["live"]["days_ahead"])
    if up.empty:
        log("Aucun match à venir dans la fenêtre : rien à prédire.")
        return
    preds = models.predict(up)
    run_id = store.save_model_run(db, "LIVE", cfg, preds, F, None, args.label or f"Quotidien {now:%Y-%m-%d}")
    log(f"ml.ModelRuns : RunId = {run_id}  ({len(preds)} matchs à venir)")
    sig = live.live_signals(preds, m, odds_table, cfg)
    n = store.save_live_signals(db, run_id, sig)
    if n:
        log(f"\n{n} pari(s) proposé(s) :")
        view = sig.merge(F[["home_name", "away_name"]], left_on="match_id", right_index=True)
        for _, r in view.sort_values("date").iterrows():
            log(f"  {r['date']:%d/%m %H:%M}  {r['home_name']} - {r['away_name']}  {r['key']} @ {r['odd']:.2f}"
                f"  proba {100 * r['prob']:.0f} %  edge {100 * r['edge']:+.1f} %  mise {r['stake']:.2f}  [{r['strategy']}]")
    else:
        log("Aucun pari proposé (aucune stratégie active dans config.json > simulation > live_strategies, "
            "ou aucune condition remplie).")


def cmd_check(args, cfg):
    """Commande « check » : connexion, présence des vues et tables, volume de données par saison."""
    db = open_db(args, cfg)
    log("Connexion à la base : OK")
    missing = []
    for schema, obj in (("core", "vw_Matches"), ("core", "vw_OddsByLabel"), ("ml", "ModelRuns"),
                        ("ml", "MatchPredictions"), ("sim", "Runs"), ("sim", "Bets"), ("sim", "LiveSignals"),
                        ("sim", "RuleScan")):
        if args.source == "db":
            ok = db.scalar("SELECT OBJECT_ID(?)", (f"{schema}.{obj}",)) is not None
        else:
            ok = True
        log(f"  {schema}.{obj:<18} {'ok' if ok else 'ABSENT'}")
        if not ok:
            missing.append(f"{schema}.{obj}")
    if missing:
        raise RuntimeError("Objets absents : exécutez dans SSMS les scripts SQL/5_Analytique/A1 à A4.")
    m, o = load_data(args, cfg, db)
    s = m[m["finished"]].groupby("season_name").agg(matchs=("match_id", "size"), ligues=("league_id", "nunique"),
                                                     xg=("h_xg", lambda x: f"{100 * x.notna().mean():.0f} %"))
    log("\nMatchs terminés par saison :\n" + s.to_string())
    log("\nRelevés de cotes : " + ", ".join(f"{k} {v:,}".replace(",", " ")
                                           for k, v in o["label"].value_counts().items()))
    up = m[~m["finished"]]
    log(f"Matchs à venir : {len(up)}")


def cmd_tune_elo(args, cfg):
    """Commande « tune-elo » : teste 45 réglages Elo et affiche les meilleurs (à recopier dans config.json)."""
    from .elo import tune_elo
    db = open_db(args, cfg)
    m, _ = load_data(args, cfg, db)
    res = tune_elo(m, cfg)
    log(res.head(10).to_string(index=False))
    log(f"\nRéglage actuel : k={cfg['elo']['k']}, avantage={cfg['elo']['home_advantage']}, "
        f"décalage D2={cfg['elo']['second_tier_offset']}")


def cmd_demo(args, cfg):
    """Commande « demo » : tout le circuit sur données synthétiques, sans base (test d'installation)."""
    args.source = "synthetic"
    cfg = settings.deep_merge(cfg, {"model": {"train_window": "all"}})
    db, run_id, res = cmd_backtest(args, cfg)
    log("\n--- Exécution quotidienne simulée ---")
    # Comme en vrai : on active la variante retenue en TUNING de la stratégie combinée.
    chosen = res[res["selected"] & (res["family"] == "combo_plus25")]
    cfg["simulation"]["live_strategies"] = [chosen["spec"].iloc[0]] if len(chosen) else []
    log("Stratégie active : " + (chosen["name"].iloc[0] if len(chosen) else "aucune"))
    args.no_save_features = True
    cmd_daily(args, cfg, db)


# ----------------------------------------------------------------------------- schéma SQLite (démo/tests)
SQLITE_DDL = [
    """CREATE TABLE ml.ModelRuns (RunId INTEGER PRIMARY KEY AUTOINCREMENT, RunType TEXT NOT NULL, Label TEXT,
       TrainWindow TEXT NOT NULL, UseMarketFeatures INTEGER NOT NULL, PeriodFrom DATE, PeriodTo DATE,
       NbMatchs INTEGER, ConfigJson TEXT, MetricsJson TEXT, CodeVersion TEXT,
       CreatedAtUtc TIMESTAMP DEFAULT CURRENT_TIMESTAMP)""",
    """CREATE TABLE ml.MatchPredictions (RunId INTEGER NOT NULL, MatchId INTEGER NOT NULL, TrainedUntil DATE NOT NULL,
       ExpHomeGoals REAL, ExpAwayGoals REAL, ExpTotalGoals REAL, MostLikelyScore TEXT,
       Score_PHome REAL, Score_PDraw REAL, Score_PAway REAL, Score_PBttsYes REAL, Score_POver15 REAL,
       Score_POver25 REAL, Score_POver35 REAL, Clf_PHome REAL, Clf_PDraw REAL, Clf_PAway REAL,
       Clf_PBttsYes REAL, Clf_POver25 REAL, PHome REAL, PDraw REAL, PAway REAL, PBttsYes REAL, POver25 REAL,
       Mkt_PHome REAL, Mkt_PDraw REAL, Mkt_PAway REAL, Mkt_PBttsYes REAL, Mkt_POver25 REAL,
       Clf_POver15 REAL, Clf_POver35 REAL, POver15 REAL, POver35 REAL, Mkt_POver15 REAL, Mkt_POver35 REAL,
       PRIMARY KEY (RunId, MatchId))""",
    """CREATE TABLE sim.Runs (SimRunId INTEGER PRIMARY KEY AUTOINCREMENT, ModelRunId INTEGER NOT NULL, BatchLabel TEXT,
       StrategyName TEXT NOT NULL, StrategyFamily TEXT NOT NULL, StrategyType TEXT NOT NULL, StrategyJson TEXT,
       LeagueName TEXT, IsPortfolio INTEGER NOT NULL DEFAULT 0,
       Phase TEXT NOT NULL, PeriodFrom DATE, PeriodTo DATE, NbBets INTEGER NOT NULL, Staked REAL, Profit REAL,
       RoiPct REAL, RoiCiLowPct REAL, RoiCiHighPct REAL, HitRatePct REAL, AvgOdd REAL, AvgEdgePct REAL,
       AvgClvPct REAL, BeatClosingPct REAL, MaxDrawdown REAL, FinalBankroll REAL, NbStrategiesTested INTEGER,
       IsSelected INTEGER NOT NULL DEFAULT 0, CreatedAtUtc TIMESTAMP DEFAULT CURRENT_TIMESTAMP)""",
    """CREATE TABLE sim.Bets (BetId INTEGER PRIMARY KEY AUTOINCREMENT, SimRunId INTEGER NOT NULL, MatchId INTEGER NOT NULL,
       MatchDate TIMESTAMP NOT NULL, Market TEXT NOT NULL, Selection TEXT NOT NULL, LineValue REAL,
       Probability REAL NOT NULL, OddTaken REAL NOT NULL, OddLabel TEXT, OddClose REAL, EdgePct REAL,
       Stake REAL NOT NULL, Outcome TEXT NOT NULL, Profit REAL NOT NULL, Bankroll REAL, ClvPct REAL)""",
    """CREATE TABLE sim.LiveSignals (SignalId INTEGER PRIMARY KEY AUTOINCREMENT, ModelRunId INTEGER NOT NULL,
       MatchId INTEGER NOT NULL, StrategyName TEXT NOT NULL, Market TEXT NOT NULL, Selection TEXT NOT NULL,
       LineValue REAL, Probability REAL NOT NULL, OddSeen REAL NOT NULL, OddLabel TEXT, EdgePct REAL,
       StakeUnits REAL, CreatedAtUtc TIMESTAMP DEFAULT CURRENT_TIMESTAMP)""",
    """CREATE TABLE sim.RuleScan (ModelRunId INTEGER NOT NULL, LeagueName TEXT NOT NULL, Phase TEXT NOT NULL, SignalName TEXT NOT NULL,
       Operator TEXT NOT NULL, Threshold REAL NOT NULL, BetKey TEXT NOT NULL, NbMatchs INTEGER NOT NULL,
       NbWin INTEGER NOT NULL, NbPush INTEGER NOT NULL, HitRatePct REAL, BreakEvenOdd REAL,
       NbWithOdds INTEGER NOT NULL, AvgOdd REAL, AvgImpliedPct REAL, Profit REAL, RoiPct REAL,
       RoiCiLowPct REAL, RoiCiHighPct REAL, AvgClvPct REAL, CreatedAtUtc TIMESTAMP DEFAULT CURRENT_TIMESTAMP)""",
]


def create_sqlite_schema(db: Database):
    """Crée les tables ml.* et sim.* dans une base SQLite en mémoire (démo et tests)."""
    for ddl in SQLITE_DDL:
        db.execute(ddl)
    db.commit()


# ----------------------------------------------------------------------------- point d'entrée
def main(argv=None) -> int:
    """Lit la ligne de commande, charge config.json (+ tuned.json) et lance la commande demandée.
    Renvoie 0 si tout s'est bien passé, 1 sinon (le service C# s'en sert pour signaler l'échec).
    """
    ap = argparse.ArgumentParser(prog="juniro_analytics", description=__doc__,
                                 formatter_class=argparse.RawDescriptionHelpFormatter)
    ap.add_argument("command", choices=["check", "features", "backtest", "simulate", "scan", "tune", "daily",
                                        "tune-elo", "demo", "importance", "placebo", "biais"])
    ap.add_argument("--config")
    ap.add_argument("--connection", help="chaîne ADO.NET ou ODBC (sinon lue dans appsettings)")
    ap.add_argument("--source", choices=["db", "synthetic"], default="db")
    ap.add_argument("--label", help="nom libre de l'exécution, retrouvé dans ml.ModelRuns / sim.Runs")
    ap.add_argument("--window", choices=["all", "1y", "2y", "decay"], help="fenêtre d'entraînement")
    ap.add_argument("--no-market", action="store_true", help="modèles sans les cotes comme variables")
    ap.add_argument("--decision-mode", choices=["real", "estimated_fallback", "estimated_all"],
                    help="cotes de décision : réelles seulement, estimées si absentes (défaut), "
                         "ou toutes estimées (pour mesurer l'effet de l'estimation)")
    ap.add_argument("--include-holdout", action="store_true", help="ouvre la réserve (à ne faire qu'une fois)")
    ap.add_argument("--no-simulate", action="store_true")
    ap.add_argument("--no-save-features", action="store_true")
    ap.add_argument("--run-id", type=int)
    ap.add_argument("--retrain", action="store_true", help="force le réentraînement des modèles du mois")
    ap.add_argument("--today", help="date de référence AAAA-MM-JJ (tests)")
    ap.add_argument("--trials", type=int, default=16, help="tune : nombre de réglages essayés")
    ap.add_argument("--every", type=int, default=2, help="tune : réentraînement tous les N mois")
    ap.add_argument("--no-h2h", action="store_true", help="sans les variables de confrontations directes")
    ap.add_argument("--reps", type=int, default=10, help="placebo : nombre de tirages au hasard")
    ap.add_argument("--skip-first", type=int, default=None,
                    help="matchs de début de saison sans pari (remplace simulation.skip_first_season_matches)")
    ap.add_argument("--repeats", type=int, default=2, help="importance : nombre de mélanges par variable")
    ap.add_argument("--no-ablation", action="store_true", help="importance : sans réentraînement par famille (plus rapide)")
    args = ap.parse_args(argv)

    overrides = {"model": {}}
    if args.window:
        overrides["model"]["train_window"] = args.window
    if args.no_market:
        overrides["model"]["use_market_features"] = False
    if args.decision_mode:
        overrides["data"] = {"decision_mode": args.decision_mode}
    if args.no_h2h:
        overrides["features"] = {"h2h": {"enabled": False}}
    if args.skip_first is not None:
        overrides["simulation"] = {"skip_first_season_matches": args.skip_first}
    cfg = settings.load_config(args.config, overrides)

    log(f"Juniro Analytics {__version__} — {args.command} — {pd.Timestamp.now():%Y-%m-%d %H:%M}")
    t = time.time()
    try:
        {"check": cmd_check, "features": cmd_features, "backtest": cmd_backtest, "simulate": cmd_simulate, "scan": cmd_scan, "tune": cmd_tune,
         "daily": cmd_daily, "tune-elo": cmd_tune_elo, "demo": cmd_demo,
         "importance": cmd_importance, "placebo": cmd_placebo, "biais": cmd_bias}[args.command](args, cfg)
    except Exception as e:  # le service lit le code retour et la dernière ligne
        traceback.print_exc()
        log(f"ÉCHEC : {e}")
        return 1
    log(f"\nTerminé en {time.time() - t:,.0f} s".replace(",", " "))
    return 0


if __name__ == "__main__":
    sys.exit(main())
