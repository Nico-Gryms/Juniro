"""Jeu de données synthétique au format exact des vues core.*.

Sert à tester toute la chaîne sans base de données : 10 ligues (5 pays x 2 divisions),
promotions/relégations, xG et statistiques, cotes avec marge à plusieurs relevés, matchs
à venir. Les « vraies » forces des équipes sont connues, ce qui permet de vérifier que les
modèles apprennent quelque chose.

Appelé par : la commande demo et les tests (aucun usage en production).
"""
from __future__ import annotations

import numpy as np
import pandas as pd
from scipy.stats import poisson

COUNTRIES = [  # pays, (ligue 1, nb équipes), (ligue 2, nb équipes)
    ("France", ("Ligue 1", 18), ("Ligue 2", 18)),
    ("Angleterre", ("Premier League", 20), ("Championship", 24)),
    ("Allemagne", ("Bundesliga", 18), ("2. Bundesliga", 18)),
    ("Espagne", ("LaLiga", 20), ("LaLiga2", 22)),
    ("Italie", ("Serie A", 20), ("Serie B", 20)),
]
LABEL_OFFSETS = [("OPENING", 6 * 24), ("J-3", 72), ("J-2", 48), ("J-1", 24), ("DAY", 12), ("H-1", 1), ("CLOSING", 0)]
LABEL_NOISE = {"OPENING": 0.16, "J-3": 0.13, "J-2": 0.12, "J-1": 0.11, "DAY": 0.10, "H-1": 0.08, "CLOSING": 0.07}


def round_robin(teams: list[int], rng) -> list[list[tuple[int, int]]]:
    """Calendrier aller-retour (méthode du cercle) pour une liste d'équipes."""
    t = list(teams)
    rng.shuffle(t)
    if len(t) % 2:
        t.append(-1)
    n = len(t)
    rounds = []
    for r in range(n - 1):
        pairs = []
        for i in range(n // 2):
            a, b = t[i], t[n - 1 - i]
            if a != -1 and b != -1:
                pairs.append((a, b) if (r + i) % 2 == 0 else (b, a))
        rounds.append(pairs)
        t = [t[0]] + [t[-1]] + t[1:-1]
    return rounds + [[(b, a) for a, b in rd] for rd in rounds]


def market_probs(lh: np.ndarray, la: np.ndarray, max_goals: int = 10) -> dict[str, np.ndarray]:
    """Probabilités exactes de chaque pari à partir de buts attendus (loi de Poisson)."""
    g = np.arange(max_goals + 1)
    ph = poisson.pmf(g[None, :], lh[:, None])
    pa = poisson.pmf(g[None, :], la[:, None])
    j = ph[:, :, None] * pa[:, None, :]
    home = np.tril(np.ones((max_goals + 1,) * 2), -1)
    p_h = (j * home).sum((1, 2))
    p_d = np.trace(j, axis1=1, axis2=2)
    p_a = 1 - p_h - p_d
    p_btts = (1 - ph[:, 0]) * (1 - pa[:, 0])
    tot = np.add.outer(g, g)
    p_o25 = (j * (tot > 2.5)).sum((1, 2))
    p_o15 = (j * (tot > 1.5)).sum((1, 2))
    p_o35 = (j * (tot > 3.5)).sum((1, 2))
    return {"OU15_OVER": p_o15, "OU15_UNDER": 1 - p_o15, "OU35_OVER": p_o35, "OU35_UNDER": 1 - p_o35,
            "1X2_HOME": p_h, "1X2_DRAW": p_d, "1X2_AWAY": p_a, "BTTS_YES": p_btts, "BTTS_NO": 1 - p_btts,
            "OU25_OVER": p_o25, "OU25_UNDER": 1 - p_o25, "DC_1X": p_h + p_d, "DC_X2": p_d + p_a,
            "DC_12": p_h + p_a, "DNB_HOME": p_h / (p_h + p_a), "DNB_AWAY": p_a / (p_h + p_a)}


def generate(seed: int = 7, first_season: int = 2022, last_season: int = 2026,
             today: str = "2026-11-01 12:00", odds_noise_scale: float = 1.0):
    """Fabrique matchs + cotes au format des vues core.* (5 saisons, 10 ligues, montées/descentes)."""
    rng = np.random.default_rng(seed)
    today_ts = pd.Timestamp(today)

    leagues, teams_rows = [], []
    league_teams: dict[int, list[int]] = {}
    att, dfn = {}, {}
    team_id = 1000
    league_id = 1
    country_leagues = []
    for country, (n1, s1), (n2, s2) in COUNTRIES:
        ids = []
        for name, size, tier in ((n1, s1, 1), (n2, s2, 2)):
            leagues.append({"LeagueId": league_id, "LeagueName": name, "CountryName": country,
                            "TierLevel": tier if league_id % 3 else None})  # certains TierLevel NULL, exprès
            members = []
            for _ in range(size):
                team_id += 1
                members.append(team_id)
                teams_rows.append({"TeamId": team_id, "TeamName": f"{country[:3]}-{team_id}"})
                shift = 0.12 if tier == 1 else -0.18
                att[team_id] = rng.normal(shift, 0.18)
                dfn[team_id] = rng.normal(shift, 0.18)
            league_teams[league_id] = members
            ids.append(league_id)
            league_id += 1
        country_leagues.append(ids)
    league_info = {l["LeagueId"]: l for l in leagues}

    matches, quotes = [], []
    match_id = 500000
    season_id = 0
    for season in range(first_season, last_season + 1):
        season_name = f"{season}-{season + 1}"
        standings_by_league = {}
        for lid, members in league_teams.items():
            season_id += 1
            for t in members:  # évolution des effectifs entre saisons
                att[t] += rng.normal(0, 0.07)
                dfn[t] += rng.normal(0, 0.07)
            rounds = round_robin(members, rng)
            start = pd.Timestamp(f"{season}-08-08 15:00")
            step = 7 if len(rounds) <= 38 else 5
            base = np.log(1.32 + 0.05 * (lid % 3))
            pts = {t: 0 for t in members}
            for r, pairs in enumerate(rounds, start=1):
                for i, (h, a) in enumerate(pairs):
                    match_id += 1
                    kickoff = start + pd.Timedelta(days=step * (r - 1) + (i % 3)) + pd.Timedelta(hours=2 * (i % 4))
                    lh = float(np.exp(base + 0.13 + att[h] - dfn[a]))
                    la = float(np.exp(base - 0.12 + att[a] - dfn[h]))
                    finished = kickoff < today_ts
                    row = {"MatchId": match_id, "FlashscoreMatchKey": f"k{match_id}", "LeagueId": lid,
                           "LeagueName": league_info[lid]["LeagueName"], "CountryName": league_info[lid]["CountryName"],
                           "TierLevel": league_info[lid]["TierLevel"], "SeasonId": season_id,
                           "SeasonName": season_name,
                           "MatchDate": kickoff, "RoundNumber": r, "HomeTeamId": h, "HomeTeamName": f"T{h}",
                           "AwayTeamId": a, "AwayTeamName": f"T{a}",
                           "MatchStatus": "Finished" if finished else "Scheduled"}
                    if finished:
                        hg, ag = int(rng.poisson(lh)), int(rng.poisson(la))
                        hxg = lh * rng.gamma(8, 1 / 8)
                        axg = la * rng.gamma(8, 1 / 8)
                        hsh = int(rng.poisson(4 + 6 * hxg)); ash = int(rng.poisson(4 + 6 * axg))
                        hpos = float(np.clip(50 + 18 * (att[h] - att[a]) + rng.normal(0, 6), 25, 75))
                        row.update(HomeScoreFT=hg, AwayScoreFT=ag,
                                   HomeScoreHT=int(rng.binomial(hg, 0.45)), AwayScoreHT=int(rng.binomial(ag, 0.45)),
                                   Home_xG=round(hxg, 2), Away_xG=round(axg, 2),
                                   Home_Possession=round(hpos), Away_Possession=round(100 - hpos),
                                   Home_Shots=hsh, Away_Shots=ash,
                                   Home_SoT=int(rng.binomial(hsh, 0.35)), Away_SoT=int(rng.binomial(ash, 0.35)),
                                   Home_Corners=int(rng.poisson(4 + 1.5 * hxg)), Away_Corners=int(rng.poisson(4 + 1.5 * axg)))
                        if rng.random() < 0.03:   # statistiques manquantes, comme dans la vraie base
                            for c in ("Home_xG", "Away_xG", "Home_Shots", "Away_Shots"):
                                row[c] = None
                        pts[h] += 3 if hg > ag else 1 if hg == ag else 0
                        pts[a] += 3 if ag > hg else 1 if hg == ag else 0
                    matches.append(row)

                    # Cotes : le marché estime les lambdas avec un bruit qui diminue jusqu'à la clôture
                    hours_to_ko = (kickoff - today_ts).total_seconds() / 3600
                    noise = rng.normal(0, 1, size=2)
                    for lab, off in LABEL_OFFSETS:
                        if not finished and off < hours_to_ko:
                            continue
                        e = noise * LABEL_NOISE[lab] * odds_noise_scale + rng.normal(0, 0.02, size=2)
                        quotes.append((match_id, lab, lh * np.exp(e[0]), la * np.exp(e[1])))
            standings_by_league[lid] = sorted(members, key=lambda t: -pts[t])

        # Promotions / relégations (3 montées, 3 descentes)
        for l1, l2 in country_leagues:
            down = standings_by_league[l1][-3:]
            up = standings_by_league[l2][:3]
            league_teams[l1] = [t for t in league_teams[l1] if t not in down] + up
            league_teams[l2] = [t for t in league_teams[l2] if t not in up] + down

    matches_df = pd.DataFrame(matches)
    q = pd.DataFrame(quotes, columns=["MatchId", "SnapshotLabel", "lh", "la"])
    probs = market_probs(q["lh"].to_numpy(), q["la"].to_numpy())
    frames = []
    for key, p in probs.items():
        market, sel = key.rsplit("_", 1)
        margin = 0.065 if market == "1X2" else 0.07
        f = q[["MatchId", "SnapshotLabel"]].copy()
        f["BookmakerId"] = 141
        f["MarketCode"] = {"OU15": "OVER_UNDER", "OU25": "OVER_UNDER", "OU35": "OVER_UNDER",
                           "DC": "DOUBLE_CHANCE"}.get(market, market)
        f["SelectionCode"] = sel
        f["LineValue"] = {"OU15": 1.5, "OU25": 2.5, "OU35": 3.5}.get(market, np.nan)
        f["OddValue"] = np.maximum(1.01, np.round(1.0 / (p * (1 + margin)), 2))
        frames.append(f)
    odds_df = pd.concat(frames, ignore_index=True)
    second = odds_df[odds_df["MatchId"] % 20 == 0].copy()   # second bookmaker sur 5 % des matchs
    second["BookmakerId"] = 16
    second["OddValue"] = np.round(second["OddValue"] * 1.02, 2)
    odds_df = pd.concat([odds_df, second], ignore_index=True)
    odds_df = odds_df[["MatchId", "BookmakerId", "MarketCode", "SelectionCode", "LineValue",
                       "SnapshotLabel", "OddValue"]]
    return matches_df, odds_df
