"""Classement Elo des équipes, calculé match après match dans l'ordre chronologique.

  E  = 1 / (1 + 10^(-(Rdom + avantage_domicile - Rext) / 400))
  R' = R + K * G * (S - E)          S = 1 / 0.5 / 0
  G  = 1 (écart 0-1), 1.5 (écart 2), (11 + écart) / 8 (écart >= 3)

- Une équipe de 2e division démarre plus bas (décalage configurable).
- Une équipe promue/reléguée garde son classement : l'Elo est continu entre divisions.
- À chaque nouvelle saison, le classement est ramené de 20 % vers la base de sa division.
- La valeur stockée pour un match est TOUJOURS celle d'avant le match.

Place dans la chaîne : étape 2 (variables). Appelé par : features.build_match_features.
Pour régler : config.json > elo (k, home_advantage, second_tier_offset, season_regression) ;
la commande tune-elo propose les meilleurs réglages. Documentation : DOCUMENTATION.md, chapitre 6.4.
"""
from __future__ import annotations

import math

import numpy as np
import pandas as pd


def _goal_mult(diff: int) -> float:
    """Multiplicateur selon l'écart de buts : une large victoire fait davantage bouger l'Elo."""
    diff = abs(diff)
    if diff <= 1:
        return 1.0
    if diff == 2:
        return 1.5
    return (11.0 + diff) / 8.0


def compute_elo(matches: pd.DataFrame, cfg: dict, windows=(3, 5, 10)) -> pd.DataFrame:
    """Calcule l'Elo d'avant-match de chaque équipe pour chaque match (terminé ou à venir),
    la différence, la probabilité Elo du domicile et l'évolution sur 3/5/10 matchs.
    """
    p = cfg["elo"]
    base, k, hfa = p["base"], p["k"], p["home_advantage"]
    offset, reg = p["second_tier_offset"], p["season_regression"]

    def tier_base(tier: int) -> float:
        """Elo de départ d'une division : 1500 en D1, 1500 - décalage en D2."""
        return base - offset * (tier - 1)

    rating: dict[int, float] = {}
    season_of: dict[int, int] = {}
    history: dict[int, list[float]] = {}

    def current(team: int, season: int, tier: int) -> tuple[float, bool]:
        """Classement d'avant-match ; renvoie aussi s'il faut mémoriser une régression de saison."""
        if team not in rating:
            return tier_base(tier), True
        r = rating[team]
        if season_of[team] != season:
            return (1 - reg) * r + reg * tier_base(tier), True
        return r, False

    rows = []
    fin = matches[matches["finished"]].sort_values(["date", "match_id"])
    for m in fin.itertuples(index=False):
        rh, _ = current(m.home_id, m.season_start, m.tier)
        ra, _ = current(m.away_id, m.season_start, m.tier)
        e_home = 1.0 / (1.0 + 10 ** (-(rh + hfa - ra) / 400.0))
        diff = int(m.hg - m.ag)
        s = 1.0 if diff > 0 else 0.5 if diff == 0 else 0.0
        delta = k * _goal_mult(diff) * (s - e_home)
        rows.append(_row(m.match_id, rh, ra, e_home, history, m.home_id, m.away_id, windows))
        rating[m.home_id], rating[m.away_id] = rh + delta, ra - delta
        season_of[m.home_id] = season_of[m.away_id] = m.season_start
        history.setdefault(m.home_id, []).append(rh)
        history.setdefault(m.away_id, []).append(ra)

    # Matchs à venir : classement actuel, sans le modifier.
    upcoming = matches[~matches["finished"]].sort_values(["date", "match_id"])
    for m in upcoming.itertuples(index=False):
        rh, _ = current(m.home_id, m.season_start, m.tier)
        ra, _ = current(m.away_id, m.season_start, m.tier)
        e_home = 1.0 / (1.0 + 10 ** (-(rh + hfa - ra) / 400.0))
        rows.append(_row(m.match_id, rh, ra, e_home, history, m.home_id, m.away_id, windows))

    cols = ["match_id", "home_elo", "away_elo", "diff_elo", "elo_exp_home"]
    cols += [f"{side}_elo_trend{n}" for side in ("home", "away") for n in windows]
    return pd.DataFrame(rows, columns=cols)


def _row(match_id, rh, ra, e_home, history, home, away, windows):
    """Construit la ligne de résultat d'un match (Elo des deux équipes + évolutions)."""
    out = [match_id, rh, ra, rh - ra, e_home]
    for team, r in ((home, rh), (away, ra)):
        h = history.get(team, [])
        for n in windows:
            # Évolution de l'Elo sur les n derniers matchs (momentum).
            out.append(r - h[-n] if len(h) >= n else np.nan)
    return out


def elo_brier(matches: pd.DataFrame, cfg: dict) -> float:
    """Erreur quadratique de l'espérance Elo vs résultat (pour régler K, avantage, décalage)."""
    e = compute_elo(matches, cfg).set_index("match_id")
    fin = matches[matches["finished"]].set_index("match_id")
    s = np.where(fin["hg"] > fin["ag"], 1.0, np.where(fin["hg"] == fin["ag"], 0.5, 0.0))
    # On ignore la première saison : l'Elo n'est pas encore stabilisé.
    keep = fin["season_start"] > fin["season_start"].min()
    exp = e.loc[fin.index, "elo_exp_home"].to_numpy()
    return float(np.mean((exp[keep.to_numpy()] - s[keep.to_numpy()]) ** 2))


def tune_elo(matches: pd.DataFrame, cfg: dict) -> pd.DataFrame:
    """Essaie 45 combinaisons (K, avantage du terrain, décalage D2) et les classe par erreur."""
    import copy
    res = []
    for k in (12, 16, 20, 25, 30):
        for hfa in (40, 60, 80):
            for off in (100, 150, 200):
                c = copy.deepcopy(cfg)
                c["elo"].update(k=k, home_advantage=hfa, second_tier_offset=off)
                res.append({"k": k, "home_advantage": hfa, "second_tier_offset": off,
                            "brier": elo_brier(matches, c)})
    return pd.DataFrame(res).sort_values("brier").reset_index(drop=True)
