"""Modèles : un modèle de score + un classifieur par marché, puis mélange.

1) Modèle de score (cœur du système)
   Deux régressions de Poisson (gradient boosting) prédisent les buts attendus de chaque
   équipe (lambda domicile, lambda extérieur). On en déduit la matrice de tous les scores
   (0-0, 1-0, ... 10-10), avec la correction Dixon-Coles pour les petits scores. De cette
   seule matrice sortent TOUTES les probabilités : 1X2, BTTS, Plus/Moins 1.5/2.5/3.5.
   Elles sont donc cohérentes entre elles par construction : « 2,8 buts attendus » et
   « Plus de 2,5 à 45 % » ne peuvent pas se contredire.

2) Classifieurs dédiés (1X2, BTTS, Plus/Moins 1.5, 2.5 et 3.5)
   Chacun optimise directement son marché, avec calibration des probabilités.

3) Probabilité finale = mélange pondéré des deux (paramètre blend_weight_score_model).

Place dans la chaîne : étape 3 (modèles). Appelé par : backtest.py, live.py, tune.py.
Réglages : config.json > model (hgb, hgb_goals, hgb_clf, train_window, blend_weight_score_model...),
surchargés par tuned.json. Documentation : DOCUMENTATION.md, chapitre 7.
"""
from __future__ import annotations

import re

import numpy as np
import pandas as pd
from scipy.optimize import minimize_scalar
from scipy.stats import poisson
from sklearn.calibration import CalibratedClassifierCV
from sklearn.ensemble import HistGradientBoostingClassifier, HistGradientBoostingRegressor

MAX_GOALS = 10


# ----------------------------------------------------------------------------- matrice des scores
def _tau(hg, ag, lh, la, rho):
    """Correction Dixon-Coles des scores 0-0, 1-0, 0-1 et 1-1 (rho négatif = plus de nuls serrés)."""
    t = np.ones_like(lh, dtype=float)
    t = np.where((hg == 0) & (ag == 0), 1 - lh * la * rho, t)
    t = np.where((hg == 0) & (ag == 1), 1 + lh * rho, t)
    t = np.where((hg == 1) & (ag == 0), 1 + la * rho, t)
    t = np.where((hg == 1) & (ag == 1), 1 - rho, t)
    return np.clip(t, 1e-6, None)


def fit_rho(hg, ag, lh, la, weights=None) -> float:
    """Paramètre Dixon-Coles par maximum de vraisemblance (corrige 0-0, 1-0, 0-1, 1-1)."""
    w = np.ones_like(lh, dtype=float) if weights is None else weights

    def nll(rho):
        """Vraisemblance négative de rho (à minimiser)."""
        return -np.sum(w * np.log(_tau(hg, ag, lh, la, rho)))

    return float(minimize_scalar(nll, bounds=(-0.25, 0.25), method="bounded").x)


def score_matrix(lh: np.ndarray, la: np.ndarray, rho: float = 0.0) -> np.ndarray:
    """Probabilité de chaque score de 0-0 à 10-10, à partir des buts attendus des deux équipes."""
    g = np.arange(MAX_GOALS + 1)
    ph = poisson.pmf(g[None, :], lh[:, None])
    pa = poisson.pmf(g[None, :], la[:, None])
    mat = ph[:, :, None] * pa[:, None, :]
    if rho:
        mat[:, 0, 0] *= np.clip(1 - lh * la * rho, 1e-6, None)
        mat[:, 0, 1] *= np.clip(1 + lh * rho, 1e-6, None)
        mat[:, 1, 0] *= np.clip(1 + la * rho, 1e-6, None)
        mat[:, 1, 1] *= np.clip(1 - rho, 1e-6, None)
    return mat / mat.sum(axis=(1, 2), keepdims=True)


def probs_from_matrix(mat: np.ndarray) -> pd.DataFrame:
    """Additionne les cases de la matrice : 1X2, BTTS, Plus 1.5/2.5/3.5, score le plus probable."""
    n = mat.shape[1]
    i, j = np.meshgrid(np.arange(n), np.arange(n), indexing="ij")
    tot = i + j
    out = {
        "score_p_home": (mat * (i > j)).sum((1, 2)),
        "score_p_draw": (mat * (i == j)).sum((1, 2)),
        "score_p_away": (mat * (i < j)).sum((1, 2)),
        "score_p_btts_yes": (mat * ((i > 0) & (j > 0))).sum((1, 2)),
        "score_p_over15": (mat * (tot > 1.5)).sum((1, 2)),
        "score_p_over25": (mat * (tot > 2.5)).sum((1, 2)),
        "score_p_over35": (mat * (tot > 3.5)).sum((1, 2)),
    }
    flat = mat.reshape(len(mat), -1).argmax(axis=1)
    out["most_likely_score"] = [f"{k // n}-{k % n}" for k in flat]
    return pd.DataFrame(out)


# ----------------------------------------------------------------------------- entraînement
def training_weights(dates: pd.Series, cutoff: pd.Timestamp, cfg: dict) -> np.ndarray:
    """Poids décroissants avec l'ancienneté (mode 'decay'), sinon None (tous les matchs comptent pareil).
    Attention : avec des poids, le gradient boosting est environ 10 fois plus lent."""
    mode = cfg["model"]["train_window"]
    if mode != "decay":
        return None
    age = (cutoff - dates).dt.total_seconds().to_numpy() / 86400.0
    return 0.5 ** (age / cfg["model"]["decay_halflife_days"])


def training_mask(dates: pd.Series, cutoff: pd.Timestamp, cfg: dict) -> np.ndarray:
    """Matchs utilisables pour l'entraînement : avant la date de coupure, et dans la fenêtre (1y / 2y)."""
    mode = cfg["model"]["train_window"]
    m = (dates < cutoff).to_numpy()
    if mode in ("1y", "2y"):
        years = 1 if mode == "1y" else 2
        m &= (dates >= cutoff - pd.DateOffset(years=years)).to_numpy()
    return m


def model_columns(feature_cols: list[str], cfg: dict) -> list[str]:
    """Variables vues par les modèles : toutes, sauf exclusions configurées (et le marché si désactivé).
    Les variables exclues restent calculées et visibles dans feat.MatchFeatures."""
    pats = [re.compile(p) for p in cfg["model"].get("exclude_feature_patterns") or []]
    cols = [c for c in feature_cols if not any(p.search(c) for p in pats)]
    if not cfg["model"]["use_market_features"]:
        cols = [c for c in cols if not c.startswith("mkt_")]
    return cols


# Nombre minimal de valeurs renseignées pour qu'une variable soit utilisée à l'entraînement.
MIN_FILLED = 50

class JuniroModels:
    """Les modèles entraînés ensemble à une date de coupure donnée (2 régressions, 5 classifieurs)."""

    def __init__(self, cfg: dict):
        """Modèles vides ; fit() les entraîne."""
        self.cfg = cfg
        self.columns: list[str] = []
        self.dropped: list[str] = []
        self.goal_cal: dict | None = None
        self.rho = 0.0
        self.trained_until: pd.Timestamp | None = None
        self.n_train = 0

    def hgb_params(self, kind: str) -> dict:
        """Paramètres du boosting : base "hgb", surchargée par "hgb_goals" (buts) ou "hgb_clf" (marchés).
        tuned.json (écrit par la commande tune) peut fournir ces surcharges."""
        m = self.cfg["model"]
        p = dict(m["hgb"])
        p.update(m.get("hgb_goals" if kind == "poisson" else "hgb_clf") or {})
        return p

    def _hgb(self, kind: str):
        """Crée un modèle de gradient boosting (Poisson pour les buts, classifieur pour les marchés)."""
        p = self.hgb_params(kind)
        common = dict(max_iter=p["max_iter"], learning_rate=p["learning_rate"],
                      max_leaf_nodes=p["max_leaf_nodes"], min_samples_leaf=p["min_samples_leaf"],
                      l2_regularization=p["l2_regularization"], max_bins=p.get("max_bins", 255),
                      early_stopping=p.get("early_stopping", "auto"),
                      n_iter_no_change=p.get("n_iter_no_change", 15),
                      validation_fraction=p.get("validation_fraction", 0.15), random_state=0)
        if p.get("max_features", 1.0) < 1.0:
            common["max_features"] = p["max_features"]
        if kind == "poisson":
            return HistGradientBoostingRegressor(loss="poisson", **common)
        return HistGradientBoostingClassifier(**common)

    def iterations(self) -> dict:
        """Nombre d'arbres réellement construits par modèle (l'arrêt anticipé décide)."""
        out = {}
        for name in ("reg_home", "reg_away", "clf_1x2", "clf_btts", "clf_over15", "clf_over25", "clf_over35"):
            mdl = getattr(self, name, None)
            out[name] = int(getattr(mdl, "n_iter_", 0) or 0)
        return out

    def _calibrated(self):
        """Classifieur, avec calibration sigmoïde si calibration_folds >= 2 (plus lent).
        Sans calibration, le boosting en perte logarithmique reste bien calibré en pratique :
        à contrôler avec la vue ml.vw_Calibration."""
        folds = int(self.cfg["model"].get("calibration_folds") or 0)
        if folds < 2:
            return self._hgb("clf")
        return CalibratedClassifierCV(self._hgb("clf"), method="sigmoid", cv=folds)

    def fit(self, F: pd.DataFrame, feature_cols: list[str], cutoff: pd.Timestamp) -> "JuniroModels":
        """Entraîne les 5 modèles sur les matchs terminés avant `cutoff` :
        2 régressions de Poisson (buts dom./ext.), rho Dixon-Coles, 5 classifieurs (1X2, BTTS,
        Plus 1.5 / 2.5 / 3.5).
        """
        mask = training_mask(F["date"], cutoff, self.cfg) & F["finished"].to_numpy()
        tr = F[mask]
        if len(tr) < self.cfg["model"]["min_train_matches"]:
            raise ValueError(f"Pas assez de matchs pour entraîner ({len(tr)} < "
                             f"{self.cfg['model']['min_train_matches']}) avant {cutoff:%Y-%m-%d}")
        # Une variable vide ou presque vide sur la période d'entraînement (ex. journée jamais
        # renseignée, confrontation au même endroit avant la 2e saison) est écartée : elle n'apporte
        # rien et fait échouer le découpage en classes (« window shape cannot be larger... ») si
        # ses rares valeurs tombent toutes dans la part gardée pour l'arrêt anticipé.
        cols = model_columns(feature_cols, self.cfg)
        filled = tr[cols].notna().sum() >= MIN_FILLED
        self.columns = [c for c in cols if filled[c]]
        self.dropped = [c for c in cols if not filled[c]]
        X = tr[self.columns].to_numpy(dtype=float)
        w = training_weights(tr["date"], cutoff, self.cfg)
        hg, ag = tr["hg"].to_numpy(), tr["ag"].to_numpy()

        # Recalage des buts attendus (avant l'entraînement final, sur des prédictions honnêtes)
        self.goal_cal = self._fit_goal_calibration(X, hg, ag, w)

        self.reg_home = self._hgb("poisson").fit(X, hg, sample_weight=w)
        self.reg_away = self._hgb("poisson").fit(X, ag, sample_weight=w)
        if self.cfg["model"]["dixon_coles"]:
            lh, la = self._lambdas(X)
            self.rho = fit_rho(hg, ag, lh, la, w)

        self.clf_1x2 = self._calibrated().fit(X, tr["y_result"].astype(int).to_numpy(), sample_weight=w)
        self.clf_btts = self._calibrated().fit(X, tr["y_btts"].astype(int).to_numpy(), sample_weight=w)
        self.clf_over25 = self._calibrated().fit(X, tr["y_over25"].astype(int).to_numpy(), sample_weight=w)
        total = tr["hg"].to_numpy() + tr["ag"].to_numpy()
        self.clf_over15 = self._calibrated().fit(X, (total > 1.5).astype(int), sample_weight=w)
        self.clf_over35 = self._calibrated().fit(X, (total > 3.5).astype(int), sample_weight=w)

        self.trained_until = tr["date"].max()
        self.n_train = len(tr)
        return self

    def _fit_goal_calibration(self, X, hg, ag, w) -> dict | None:
        """Recalage des buts attendus.

        Constat (ml.vw_GoalsCalibration) : le modèle sous-estime légèrement les buts, surtout quand il
        annonce un match très fermé (1,9 but attendu -> 2,26 réels). Correction :
          1. on entraîne provisoirement les modèles de buts sur les matchs les plus anciens
             (1 - calibration_fraction) ;
          2. on prédit les matchs les plus récents, qu'ils n'ont pas vus ;
          3. on mesure, pour chaque équipe (domicile, extérieur), la droite buts réels = a + b x buts prédits ;
          4. cette droite corrige ensuite toutes les prédictions du modèle final.
        b < 1 : le modèle exagère les écarts entre matchs ; a > 0 : il sous-estime en moyenne.
        Réglage : config.json > model > goal_calibration ("linear" ou "none"), calibration_fraction.
        """
        m = self.cfg["model"]
        if m.get("goal_calibration", "linear") == "none":
            return None
        n = len(X)
        cut = int(n * (1 - m.get("calibration_fraction", 0.2)))
        if cut < 500 or n - cut < 300:
            return None                      # trop peu de matchs pour une mesure fiable
        w_fit = None if w is None else w[:cut]
        rh = self._hgb("poisson").fit(X[:cut], hg[:cut], sample_weight=w_fit)
        ra = self._hgb("poisson").fit(X[:cut], ag[:cut], sample_weight=w_fit)
        cal = {}
        for side, mdl, y in (("home", rh, hg), ("away", ra, ag)):
            pred = np.clip(mdl.predict(X[cut:]), 0.05, 6.0)
            obs = y[cut:].astype(float)
            b, a = np.polyfit(pred, obs, 1)
            b = float(np.clip(b, 0.5, 1.5))
            a = float(obs.mean() - b * pred.mean())   # la moyenne recalée = la moyenne observée
            cal[side] = (a, b)
        return cal

    def _lambdas(self, X):
        """Buts attendus domicile et extérieur, recalés (voir _fit_goal_calibration), bornés entre 0,05 et 6."""
        lh = np.clip(self.reg_home.predict(X), 0.05, 6.0)
        la = np.clip(self.reg_away.predict(X), 0.05, 6.0)
        cal = getattr(self, "goal_cal", None)
        if cal:
            lh = np.clip(cal["home"][0] + cal["home"][1] * lh, 0.05, 6.0)
            la = np.clip(cal["away"][0] + cal["away"][1] * la, 0.05, 6.0)
        return lh, la

    def predict(self, F: pd.DataFrame) -> pd.DataFrame:
        """Toutes les sorties pour des matchs : buts attendus, probabilités du modèle de score,
        des classifieurs, et probabilité finale (mélange des deux).
        """
        X = F[self.columns].to_numpy(dtype=float)
        lh, la = self._lambdas(X)
        sp = probs_from_matrix(score_matrix(lh, la, self.rho))
        out = pd.DataFrame(index=F.index)
        out["exp_home_goals"] = lh
        out["exp_away_goals"] = la
        out["exp_total_goals"] = lh + la
        for c in sp.columns:
            out[c] = sp[c].to_numpy()

        p1x2 = self.clf_1x2.predict_proba(X)
        classes = list(self.clf_1x2.classes_)
        for k, name in enumerate(("home", "draw", "away")):
            out[f"clf_p_{name}"] = p1x2[:, classes.index(k)] if k in classes else 0.0
        out["clf_p_btts_yes"] = self.clf_btts.predict_proba(X)[:, 1]
        out["clf_p_over25"] = self.clf_over25.predict_proba(X)[:, 1]
        out["clf_p_over15"] = self.clf_over15.predict_proba(X)[:, 1]
        out["clf_p_over35"] = self.clf_over35.predict_proba(X)[:, 1]

        w = self.cfg["model"]["blend_weight_score_model"]
        for name in ("home", "draw", "away", "btts_yes", "over15", "over25", "over35"):
            out[f"p_{name}"] = w * out[f"score_p_{name}"] + (1 - w) * out[f"clf_p_{name}"]
        # Cohérence entre lignes : Plus de 1,5 est toujours au moins aussi probable que Plus de 2,5,
        # lui-même au moins aussi probable que Plus de 3,5 (les classifieurs sont entraînés séparément).
        out["p_over15"] = np.maximum(out["p_over15"], out["p_over25"])
        out["p_over35"] = np.minimum(out["p_over35"], out["p_over25"])
        s = out[["p_home", "p_draw", "p_away"]].sum(axis=1)
        for name in ("home", "draw", "away"):
            out[f"p_{name}"] /= s
        out["trained_until"] = self.trained_until
        return out


# ----------------------------------------------------------------------------- évaluation
def log_loss_1x2(p: pd.DataFrame, y: pd.Series, cols=("p_home", "p_draw", "p_away")) -> float:
    """Perte logarithmique du 1X2 : moyenne de -log(probabilité donnée au résultat réel)."""
    arr = np.clip(p[list(cols)].to_numpy(dtype=float), 1e-9, 1)
    yy = y.astype(int).to_numpy()
    return float(-np.mean(np.log(arr[np.arange(len(yy)), yy])))


def log_loss_binary(p: pd.Series, y: pd.Series) -> float:
    """Perte logarithmique d'un marché oui/non (BTTS, Plus 2.5)."""
    pp = np.clip(p.to_numpy(dtype=float), 1e-9, 1 - 1e-9)
    yy = y.to_numpy(dtype=float)
    return float(-np.mean(yy * np.log(pp) + (1 - yy) * np.log(1 - pp)))
