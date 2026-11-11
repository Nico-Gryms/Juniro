"""Walk-forward : on rejoue l'histoire mois par mois, comme en conditions réelles.

Pour chaque mois M (à partir du début de la phase de réglage) :
  - les modèles sont entraînés sur les seuls matchs terminés AVANT le 1er du mois M ;
  - ils prédisent les matchs du mois M, qu'ils n'ont jamais vus.
Toutes les prédictions obtenues sont donc « hors échantillon », exactement comme celles que
le service produira chaque jour. C'est la seule façon honnête de mesurer un modèle de paris.

Phases (config.json > phases) :
  BURN_IN  avant tuning_start : sert uniquement d'historique d'entraînement
  TUNING   on y choisit les seuils des stratégies
  TEST     on y mesure la stratégie choisie, sans plus rien toucher
  HOLDOUT  réserve, exclue par défaut ; à n'ouvrir qu'une fois, pour la validation finale

Place dans la chaîne : étape 4 (après les variables, avant les paris).
Appelé par : __main__.cmd_backtest. Utilise models.JuniroModels.
Pour modifier les périodes : config.json > phases. Documentation : DOCUMENTATION.md, chapitre 7.
"""
from __future__ import annotations

import time

import numpy as np
import pandas as pd

from .models import JuniroModels, log_loss_1x2, log_loss_binary


def phase_of(dates: pd.Series, cfg: dict) -> pd.Series:
    """Phase de chaque date : BURN_IN, TUNING, TEST ou HOLDOUT (dates dans config.json > phases)."""
    p = cfg["phases"]
    t, s, h = (pd.Timestamp(p[k]) for k in ("tuning_start", "test_start", "holdout_start"))
    return pd.Series(np.select([dates < t, dates < s, dates < h], ["BURN_IN", "TUNING", "TEST"], "HOLDOUT"),
                     index=dates.index)


def run_backtest(F: pd.DataFrame, feature_cols: list[str], cfg: dict, include_holdout: bool = False,
                 log=print) -> pd.DataFrame:
    """Walk-forward : pour chaque mois, entraîne sur le passé et prédit le mois. Renvoie une ligne
    de prédictions par match terminé, avec sa phase. --include-holdout ajoute la saison en cours.
    """
    p = cfg["phases"]
    start = pd.Timestamp(p["tuning_start"])
    fin = F[F["finished"]]
    end = fin["date"].max()
    if not include_holdout:
        end = min(end, pd.Timestamp(p["holdout_start"]) - pd.Timedelta(seconds=1))

    months = pd.date_range(start.normalize().replace(day=1), end, freq="MS")
    preds = []
    last_dropped: list[str] = []
    t0 = time.time()
    for i, ms in enumerate(months, start=1):
        me = ms + pd.offsets.MonthBegin(1)
        test = fin[(fin["date"] >= ms) & (fin["date"] < me) & (fin["date"] <= end)]
        if test.empty:
            continue
        try:
            models = JuniroModels(cfg).fit(F, feature_cols, cutoff=ms)
        except ValueError as e:
            log(f"  {ms:%Y-%m} ignoré : {e}")
            continue
        if models.goal_cal and ms.month in (1, 7):
            h, a = models.goal_cal["home"], models.goal_cal["away"]
            log(f"  Recalage des buts : domicile {h[0]:+.2f} + {h[1]:.2f} x prédit, "
                f"extérieur {a[0]:+.2f} + {a[1]:.2f} x prédit")
        if models.dropped and models.dropped != last_dropped:
            log(f"  Variables vides ou presque (< 50 valeurs) écartées ({len(models.dropped)}) : {', '.join(models.dropped[:12])}"
                f"{' ...' if len(models.dropped) > 12 else ''}")
        last_dropped = models.dropped
        pr = models.predict(test)
        preds.append(pr)
        log(f"  {ms:%Y-%m}  entraînement {models.n_train:>6} matchs  ->  {len(test):>4} prédits"
            f"   ({i}/{len(months)}, {time.time() - t0:,.0f} s)")
    if not preds:
        raise RuntimeError("Aucune prédiction produite : vérifiez les dates des phases et le volume de données.")
    out = pd.concat(preds)
    out["phase"] = phase_of(F.loc[out.index, "date"], cfg)
    return out


def evaluate(preds: pd.DataFrame, F: pd.DataFrame, early: pd.Series | None = None) -> dict:
    """Perte logarithmique du modèle face au marché (plus bas = meilleur), par phase.
    early (facultatif) : True pour les matchs de début de saison ; le 1X2 est alors aussi mesuré
    séparément sur ces matchs et sur le reste (clés "debut" et "reste")."""
    res = {}
    cols = ["y_result", "y_btts", "y_over25", "y_total", "mkt_p_home", "mkt_p_draw",
            "mkt_p_away", "mkt_p_btts", "mkt_p_over25", "mkt_p_over15", "mkt_p_over35"]
    d = preds.join(F[[c for c in cols if c in F.columns]])
    d["y_over15"] = (d["y_total"] > 1.5).astype(float).where(d["y_total"].notna())
    d["y_over35"] = (d["y_total"] > 3.5).astype(float).where(d["y_total"].notna())
    order = ["BURN_IN", "TUNING", "TEST", "HOLDOUT"]
    for phase, g in sorted(d.groupby("phase"), key=lambda kv: order.index(kv[0])):
        r = {"matchs": int(len(g))}
        r["buts_attendus_moyens"] = round(float(g["exp_total_goals"].mean()), 3)
        r["buts_reels_moyens"] = round(float(g["y_total"].mean()), 3)
        r["1x2_modele"] = round(log_loss_1x2(g, g["y_result"]), 5)
        r["1x2_score"] = round(log_loss_1x2(g, g["y_result"], ("score_p_home", "score_p_draw", "score_p_away")), 5)
        r["1x2_classifieur"] = round(log_loss_1x2(g, g["y_result"], ("clf_p_home", "clf_p_draw", "clf_p_away")), 5)
        r["btts_modele"] = round(log_loss_binary(g["p_btts_yes"], g["y_btts"]), 5)
        r["over25_modele"] = round(log_loss_binary(g["p_over25"], g["y_over25"]), 5)
        mk = g.dropna(subset=["mkt_p_home", "mkt_p_draw", "mkt_p_away"])
        if len(mk):
            r["matchs_avec_cotes"] = int(len(mk))
            r["1x2_marche"] = round(log_loss_1x2(mk, mk["y_result"], ("mkt_p_home", "mkt_p_draw", "mkt_p_away")), 5)
            r["1x2_modele_memes_matchs"] = round(log_loss_1x2(mk, mk["y_result"]), 5)
        mb = g.dropna(subset=["mkt_p_btts"])
        if len(mb):
            r["btts_marche"] = round(log_loss_binary(mb["mkt_p_btts"], mb["y_btts"]), 5)
            r["btts_modele_memes_matchs"] = round(log_loss_binary(mb["p_btts_yes"], mb["y_btts"]), 5)
        for line in ("15", "25", "35"):
            pc, mc, yc = f"p_over{line}", f"mkt_p_over{line}", f"y_over{line}"
            if pc not in g.columns:
                continue
            if line != "25":
                r[f"over{line}_modele"] = round(log_loss_binary(g[pc], g[yc]), 5)
            mo = g.dropna(subset=[mc]) if mc in g.columns else g.iloc[0:0]
            if len(mo):
                r[f"over{line}_marche"] = round(log_loss_binary(mo[mc], mo[yc]), 5)
                r[f"over{line}_modele_memes_matchs"] = round(log_loss_binary(mo[pc], mo[yc]), 5)
        if early is not None and len(mk):
            e = early.reindex(mk.index).fillna(False).astype(bool)
            for key, part in (("debut", mk[e]), ("reste", mk[~e])):
                if len(part):
                    r[key] = {"matchs": int(len(part)),
                              "modele": round(log_loss_1x2(part, part["y_result"]), 5),
                              "marche": round(log_loss_1x2(part, part["y_result"],
                                                           ("mkt_p_home", "mkt_p_draw", "mkt_p_away")), 5)}
        res[phase] = r
    return res


def render_metrics(metrics: dict) -> str:
    """Texte du bloc « Précision » : perte logarithmique modèle vs marché, par phase et par marché."""
    lines = ["Précision (perte logarithmique, plus bas = meilleur ; le marché est la référence à battre)"]
    for phase, r in metrics.items():
        lines.append(f"  {phase:<8} {r['matchs']:>6} matchs   buts attendus {r['buts_attendus_moyens']:.2f}"
                     f" / réels {r['buts_reels_moyens']:.2f}")
        for mkt, label in (("1x2", "1X2"), ("btts", "BTTS"), ("over15", "+1,5"), ("over25", "+2,5"),
                           ("over35", "+3,5")):
            if f"{mkt}_modele" not in r:
                continue
            m, k = r.get(f"{mkt}_modele_memes_matchs"), r.get(f"{mkt}_marche")
            if m is None or k is None:
                lines.append(f"           {label:<5} modèle {r[f'{mkt}_modele']:.5f}   (pas de cotes)")
                continue
            verdict = "modèle meilleur" if m < k else "marché meilleur"
            lines.append(f"           {label:<5} modèle {m:.5f}   marché {k:.5f}   écart {m - k:+.5f}  {verdict}")
        for key, label in (("debut", "début de saison"), ("reste", "reste de saison")):
            d = r.get(key)
            if d:
                lines.append(f"           1X2 {label:<16} {d['matchs']:>5} matchs   modèle {d['modele']:.5f}"
                             f"   marché {d['marche']:.5f}   écart {d['modele'] - d['marche']:+.5f}")
    return "\n".join(lines)
