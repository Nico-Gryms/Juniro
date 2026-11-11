# Juniro Analytics — variables, modèles, simulation

> Explication complète, étape par étape, du scraping à la décision : `DOCUMENTATION.md` à la
> racine du projet (chapitres 5 à 8 pour ce moteur).

Le moteur de calcul. Il lit les données brutes **via les vues `core.*`**, calcule tout en Python
et écrit ses résultats dans les schémas `feat`, `ml` et `sim` de `Juniro_Evo`. Il ne modifie
jamais les tables brutes (`dbo`).

```
dbo (brut, écrit par le scraper)
  └─ core.vw_Matches / core.vw_OddsByLabel        vues canoniques (A2)
        └─ Python : variables au coup d'envoi ──► feat.MatchFeatures
              └─ modèles (réentraînés chaque mois) ──► ml.ModelRuns + ml.MatchPredictions
                    └─ stratégies de paris ──► sim.Runs + sim.Bets (historique) / sim.LiveSignals (à venir)
                          └─ vues app.* et sim.vw_* (A3) : ce que vous consultez dans SSMS
```

## Installation (PC fixe, une fois)

1. SSMS : exécuter `SQL/5_Analytique/A1_StructureAnalytique.sql` puis `A2_VuesSource.sql`.
2. Installer Python 3.13 (python.org, cocher « Add python.exe to PATH »).
3. PowerShell **administrateur** :
   ```powershell
   cd C:\Users\grymy\Juniro.ScraperService\Deploy
   .\install-analytics.ps1 -SourceDir "C:\Users\grymy\Juniro.ScraperService\Analytics"
   ```
   Le script copie le moteur dans `C:\Juniro\analytics`, crée l'environnement Python, installe les
   bibliothèques et vérifie la connexion (`check`). La chaîne de connexion est relue dans
   `C:\Juniro\app\appsettings.Production.json` : rien à recopier.
4. Premier calcul complet (voir ci-dessous), puis SSMS : `A3_VuesConsultation.sql`.

Sur le PC de dev, on peut aussi lancer directement depuis le dossier du projet (la connexion est
lue dans `appsettings.Development.json`) :
```powershell
cd C:\Users\grymy\Juniro.ScraperService\Analytics
py -m venv .venv ; .\.venv\Scripts\pip install -r requirements.txt
.\.venv\Scripts\python -X utf8 -m juniro_analytics check
```

## Commandes

Depuis `C:\Juniro\analytics`, préfixer par `.\.venv\Scripts\python.exe -X utf8 -m juniro_analytics` :

| Commande | Rôle | Durée indicative |
|---|---|---|
| `check` | Connexion, présence des vues/tables, volume de données | secondes |
| `features` | Recalcule toutes les variables et réécrit `feat.MatchFeatures` | 1 min |
| `backtest --label "texte"` | Walk-forward mois par mois + simulation de toutes les stratégies | 15–40 min |
| `simulate --run-id N` | Rejoue seulement les stratégies sur un backtest existant (après modification de `config.json`) | 1–3 min |
| `scan [--run-id N]` | Balayage des seuils : « buts attendus ≥ 2,7 ⇒ +1,5 », etc. (aussi fait à la fin de chaque backtest) | 1 min |
| `tune [--trials 16]` | Réglage fin des modèles sur la saison de réglage, écrit `tuned.json` | 1–3 h |
| `daily` | Matchs à venir : prédictions + paris proposés (lancé chaque jour par le service) | 1–2 min (10 min le 1er du mois) |
| `tune-elo` | Teste 45 réglages Elo et affiche les meilleurs | 2 min |
| `importance` | Quelles familles de variables comptent vraiment (Elo, xG, cotes, confrontations directes…) : retrait de chaque famille et mélange variable par variable | 10–20 min |
| `placebo [--reps 10]` | Rejoue le balayage sur des résultats tirés au hasard (cotes justes) : combien de « stratégies » le hasard produit-il ? | 5–10 min |
| `biais` | Carte des biais de prix : ligue × pari × tranche de cote, saison par saison, sans modèle (CSV dans `rapports\`) | 1 min |
| `demo` | Tout le circuit sur données synthétiques, sans base (test d'installation) | 10–20 min |

Options utiles : `--window all|1y|2y|decay`, `--no-market` (modèles sans les cotes),
`--include-holdout` (ouvre la réserve 2026-27 : **une seule fois**, à la toute fin).
`--no-h2h` : sans les confrontations directes (désactivées par défaut).
`--skip-first N` : matchs de début de saison sans pari (5 par défaut ; `--skip-first 0` pour comparer).

## Comment c'est calculé

**Variables (≈280 par match), toutes figées à minuit le jour du match.** Le principe : on calcule
l'état de chaque équipe *après* chacun de ses matchs, puis on rattache à chaque match le dernier
état *strictement antérieur au jour du match*. Aucun résultat du jour ne peut entrer dans les
variables, ce qui reproduit la situation réelle (décision le matin). Un test automatique modifie
un score et vérifie qu'aucune variable antérieure ne bouge.

- Formes 3/5/10 matchs, générales et domicile/extérieur : buts, xG, tirs, tirs cadrés, corners,
  possession, points, xPts, BTTS, +2,5, clean sheets, matchs sans marquer.
- Moyennes pondérées exponentielles (demi-vie 5 matchs).
- Saison en cours : points/match, xPts/match, chance (points − xPts), différence de buts.
- Forces attaque/défense (buts et xG) rapportées à la moyenne de la ligue, rétrécies vers la
  moyenne en début de saison (k = 5 matchs) ; buts attendus « naïfs » qui en découlent.
- Classements avant le match : réel, aux xPts, de forme (10 matchs) ; écart au leader.
- Elo continu entre divisions (D2 démarre 150 points plus bas, régression de 20 % à chaque
  saison), évolution Elo sur 3/5/10 matchs.
- Contexte : jours de repos, promu/relégué, journée, avancement de saison, moyennes de la ligue.
- Marché : probabilités sans marge à la cote de décision (jour J, sinon J-1, J-2, J-3) et
  mouvement depuis l'ouverture. Jamais la clôture.

**Modèles.** Un modèle de score (deux régressions de Poisson → buts attendus de chaque équipe →
matrice de tous les scores, correction Dixon-Coles) d'où sortent *toutes* les probabilités de
façon cohérente ; plus un classifieur calibré par marché (1X2, BTTS, +2,5). Probabilité finale =
moyenne des deux. Réentraînement le 1er de chaque mois. Fenêtre d'entraînement au choix
(`--window`) : `all` (tout l'historique, par défaut), `1y`, `2y`, ou `decay` (tout l'historique,
les matchs anciens pesant moins ; environ 10 fois plus lent). Lancez un backtest par fenêtre et
comparez-les dans `sim.vw_StrategySummary` : c'est la simulation qui tranche, pas l'intuition.

**Simulation.** Phases : 2022-23 historique seul, 2023-24 réglage (TUNING), 2024-25 et 2025-26
évaluation (TEST), 2026-27 réserve (HOLDOUT). Chaque variante de chaque stratégie est enregistrée
dans `sim.Runs`, la meilleure est choisie sur TUNING, et **seul son résultat TEST fait foi**.

## Simulation ligue par ligue

Chaque stratégie est testée, avec toutes ses variantes de seuils, **dans chaque ligue séparément** :
la meilleure variante est choisie sur la période de réglage de cette ligue. Chaque couple stratégie ×
ligue est un portefeuille à part, avec un **bilan saison par saison** et deux totaux : « Test »
(saisons de test seulement) et « Total » (toutes saisons). Le balayage des seuils est fait de la même
façon, ligue par ligue.

- `config.json > simulation` : `by_league` (true), `include_all_leagues` (ajoute aussi le calcul
  toutes ligues confondues), `min_bets_per_league` (30 paris minimum en réglage pour retenir une variante).
- Vues : `sim.vw_SeasonReport` (bilan par saison, script A6), `sim.vw_StrategySummary` (une ligne par stratégie et par ligue),
  `sim.vw_VariantsByLeague` (toutes les variantes essayées), `sim.vw_RuleScan` (colonne `Ligue`).
- Pour jouer une stratégie dans certaines ligues seulement : ajouter `"leagues": ["Ligue 2", "Serie B"]`
  à sa définition dans `live_strategies`.

Prudence : 144 variantes x 10 ligues = 1 440 essais pour une seule famille, avec 30 à 60 paris par
ligue et par saison. Quelques ligues sortiront « PROMETTEUSE » par pur hasard ; une ligue n'est
crédible que si plusieurs variantes voisines y gagnent aussi (`sim.vw_VariantsByLeague`) et si
elle continue de gagner sur les matchs joués après le test.

## Cotes de décision estimées

Quand un match n'a pas de relevé du jour ou de la veille (matchs d'août à octobre 2026, entrés par
le rattrapage), la cote de décision est estimée entre la cote d'ouverture et la cote finale
(étiquette `EST`). `data.decision_mode` / `--decision-mode` : `estimated_fallback` (défaut), `real`,
`estimated_all`. Comparer un backtest `real` et un backtest `estimated_all` mesure le biais de
l'estimation (DOCUMENTATION.md, chapitre 6.6).

## Recalage des buts attendus

À chaque entraînement, les buts prédits sont comparés aux buts réels sur les 20 % de matchs les plus
récents (non vus par un modèle provisoire), puis corrigés par une droite a + b × prédit. Réglage :
`model.goal_calibration` (`"linear"` / `"none"`), `model.calibration_fraction`. Effet à contrôler
dans `ml.vw_GoalsCalibration` (buts attendus vs réels par tranche).

## Résultats des modèles et balayage des seuils

Deux couches séparées (script SQL `A4_ResultatsModelesEtSeuils.sql`) :

- **Ce que disent les modèles, sans cote ni pari** : `ml.vw_PredictionResults` (chaque match
  prédit et son issue) et `ml.vw_GoalsCalibration` (par tranche de 0,1 but attendu : buts réels,
  % de +1,5 / +2,5 / +3,5 / BTTS et **cote d'équilibre**, la cote minimale pour ne pas perdre).
- **Ce que donneraient des paris** : `sim.vw_RuleScan`. Pour chaque sortie des modèles
  (buts attendus total/domicile/extérieur, probabilités 1X2, BTTS, +1,5, +2,5, +3,5), chaque seuil
  (≥ et ≤) et chaque pari (+/-1,5, +/-2,5, +/-3,5, BTTS, 1X2, double chance, remboursé si nul) :
  réussite, cote d'équilibre, et quand la cote existe en base, ROI, intervalle de confiance et CLV,
  pour la saison de réglage ET la période de test côte à côte.

Lire la colonne `Verdict` : seule « PROMETTEUSE » (positive en réglage, et borne basse de
l'intervalle positive en test) mérite attention. Le balayage teste plusieurs milliers de règles,
qui se recoupent largement : quelques-unes paraîtront bonnes par pur hasard.

Les cotes Plus/Moins 1,5 et 3,5 sont relevées par le scraper (comme 2,5) : leur ROI est calculé
quand le relevé existe. La double chance « 12 » n'est pas relevée : pour ce pari, seuls le taux de
réussite et la cote d'équilibre sont disponibles.

## Réglage des modèles (« entraîner plus longtemps »)

`tune` essaie des réglages plus fins (apprentissage plus lent, arbres plus ou moins profonds,
régularisation, variables tirées au hasard) en laissant chaque modèle s'entraîner tant qu'il
progresse (arrêt après 50 arbres sans gain, jusqu'à 3 000 arbres). Il est jugé **uniquement sur la
saison de réglage** ; le meilleur réglage est écrit dans `tuned.json` et repris automatiquement
par `backtest` et `daily`. Supprimer `tuned.json` = revenir au réglage de `config.json`.

Ensuite : `backtest --label "réglé"`, puis comparer les deux backtests dans `sim.vw_StrategySummary`
et `sim.vw_RuleScan`. C'est la période de test qui dit si le gain est réel.

## Vos stratégies combinées

Elles se décrivent dans `config.json > simulation > strategies`. Exemple (votre idée) :

```json
{
  "name": "combo_plus25",
  "bets": ["OU25_OVER"],
  "conditions": [
    ["exp_total_goals", ">=", 2.8],
    ["p_btts_yes", ">=", 0.55],
    ["p_over25", ">=", 0.55]
  ],
  "min_edge": null,
  "grid": { "conditions.0": [2.6, 2.8, 3.0, 3.2], "min_edge": [null, 0.0, 0.03] }
}
```

- Champs utilisables dans `conditions` : `exp_total_goals`, `exp_home_goals`, `exp_away_goals`,
  `p_home`, `p_draw`, `p_away`, `p_btts_yes`, `p_over25`, les versions `score_p_*` (modèle de score
  seul) et `clf_p_*` (classifieur seul), ainsi que `odd`, `prob`, `edge`.
- Paris possibles : `1X2_HOME/DRAW/AWAY`, `BTTS_YES/NO`, `OU25_OVER/UNDER`, `DC_1X/X2/12`,
  `DNB_HOME/AWAY`.
- `min_edge` : exige en plus que la cote soit au-dessus du prix juste du modèle
  (0.03 = au moins 3 % d'espérance de gain). `null` = pas d'exigence.
- `grid` : les seuils à essayer. Attention : 4 × 3 × 4 × 3 = 144 variantes ; plus il y en a,
  plus la meilleure en TUNING est flatteuse par hasard.
- Mises : `{"method": "flat", "units": 1}` ou `{"method": "kelly", "fraction": 0.25, "max_pct": 0.03}`.

Après un changement de stratégie, pas besoin de réentraîner : `simulate --run-id N` suffit.

## Passer en production

Quand une stratégie est **PROMETTEUSE** dans `sim.vw_StrategySummary` (ROI TEST positif, borne
basse de l'intervalle de confiance positive, CLV positif), copiez sa définition **avec les seuils
retenus et sans `grid`** dans `config.json > simulation > live_strategies`. Le passage quotidien
écrira alors ses paris dans `sim.LiveSignals` (vue `app.vw_LiveSignals`).

Activation dans le service (`C:\Juniro\app\appsettings.Production.json`) :

```json
"Analytics": { "Enabled": true, "Directory": "C:\\Juniro\\analytics", "Cron": "30 5 * * *" }
```

puis redémarrer le service. Le job `daily-analytics` apparaît dans le tableau de bord Hangfire et
peut y être lancé à la main (« Trigger now »).

## Nettoyage

Chaque backtest garde ses résultats. Pour supprimer un essai (prédictions, simulations et paris
suivent par cascade) :

```sql
DELETE FROM Juniro_Evo.ml.ModelRuns WHERE RunId = 12;
```
