# Juniro — du scraping à la décision de pari

Documentation technique de bout en bout : comment une page Flashscore devient, quelques heures
plus tard, un pari proposé (ou refusé) pour un match. Chaque étape cite les fichiers qui la
réalisent, pour savoir quoi ouvrir avant de modifier quelque chose.

> **Règle de maintenance.** Toute modification du code (C#, Python, SQL, scripts de déploiement,
> `config.json`) s'accompagne de la mise à jour du chapitre concerné **et** d'une ligne dans le
> [journal des modifications](#12-journal-des-modifications). Un document faux est pire qu'un
> document absent.

Dernière mise à jour : 11 novembre 2026.

---

## Sommaire

1. [Vue d'ensemble](#1-vue-densemble)
2. [Le service Windows et la planification](#2-le-service-windows-et-la-planification)
3. [Collecte des données sur Flashscore](#3-collecte-des-données-sur-flashscore)
4. [Stockage dans SQL Server](#4-stockage-dans-sql-server)
5. [Le moteur de calcul : installation et organisation](#5-le-moteur-de-calcul--installation-et-organisation)
6. [Les variables : ce que l'on sait avant le match](#6-les-variables--ce-que-lon-sait-avant-le-match)
7. [Les modèles et leur évaluation](#7-les-modèles-et-leur-évaluation)
8. [Les paris : de la probabilité à la décision](#8-les-paris--de-la-probabilité-à-la-décision)
9. [Déploiement et mises à jour](#9-déploiement-et-mises-à-jour)
10. [Où modifier quoi](#10-où-modifier-quoi)
11. [Résultats actuels et limites connues](#11-résultats-actuels-et-limites-connues)
12. [Journal des modifications](#12-journal-des-modifications)
13. [Application mobile et Supabase](#13-application-mobile-et-supabase)
14. [Application Windows de stratégies (en préparation)](#14-application-windows-de-stratégies-en-préparation)

---

## 1. Vue d'ensemble

### 1.1 La chaîne complète

```
 03h00  SERVICE WINDOWS (C#)                         SQL SERVER  Juniro_Evo
 ┌───────────────────────────────┐                  ┌──────────────────────────────┐
 │ FlashscoreScraperJob          │  matchs, stats,  │ dbo.*   données brutes        │
 │  accueil J-1 … J+3            │ ───────────────► │ Matches, MatchTeamStats,      │
 │  page de chaque match         │  cotes           │ Odds, OddsQuotes …            │
 │  JuniroRepository (écriture)  │                  │                              │
 └───────────────────────────────┘                  │ core.*  vues canoniques       │
                                                    └──────────────┬───────────────┘
 05h30  AnalyticsJob lance Python                                  │ lecture
 ┌─────────────────────────────────────────────────────────────────▼───────────────┐
 │ MOTEUR PYTHON  (C:\Juniro\analytics, commande « daily »)                         │
 │  1. données      data.py, odds.py                                                │
 │  2. variables    features.py, elo.py      ─► feat.MatchFeatures                  │
 │  3. modèles      models.py (réentraînés le 1er du mois)                          │
 │  4. prédictions  live.py                  ─► ml.ModelRuns, ml.MatchPredictions   │
 │  5. décision     simulate.py (stratégies actives) ─► sim.LiveSignals             │
 └──────────────────────────────────────────────────────────────────────────────────┘
                                                    │
 VOUS (SSMS)  app.vw_UpcomingPredictions, app.vw_LiveSignals, sim.vw_StrategySummary …

 À LA DEMANDE : backtest / simulate / scan / tune  ─► ml.*, sim.Runs, sim.Bets, sim.RuleScan
 (rejouer l'histoire pour savoir quelles stratégies méritent d'être activées)
```

Deux circuits distincts :

- **Le circuit quotidien** (automatique) : collecter, calculer, prédire, proposer les paris du jour.
- **Le circuit d'étude** (manuel) : rejouer les saisons passées pour mesurer les modèles et les
  stratégies. C'est lui qui décide quelles stratégies ont le droit d'entrer dans le circuit quotidien.

### 1.2 Les dossiers du projet

| Dossier / fichier | Langage | Rôle |
|---|---|---|
| `Program.cs` | C# | Démarrage du service, planification Hangfire |
| `Configuration\` | C# | Ligne de commande du rattrapage, heure de Paris, accès au tableau de bord |
| `Services\FlashscoreScraperJob.cs` | C# | Le scraper (navigateur Playwright) |
| `Services\AnalyticsJob.cs` | C# | Lancement du moteur Python |
| `Data\JuniroRepository.cs` | C# | Écriture en base des données scrapées |
| `Models\` | C# | Objets en mémoire (match lu, cotes, options, rapport qualité) |
| `Deploy\` | PowerShell | Publication, installation, mise à jour (service et moteur) |
| `Analytics\` | Python | Moteur de calcul : variables, modèles, simulation |
| `SQL\` | T-SQL | Scripts à exécuter dans SSMS (diagnostics, corrections, structure analytique) |
| `appsettings*.json` | JSON | Configuration du service (connexion, réglages du scraper) |

### 1.3 Les machines

| Machine | Ce qui y tourne | Dossiers |
|---|---|---|
| PC de développement | Visual Studio, le projet, tests | `C:\Users\grymy\Juniro.ScraperService` |
| PC fixe | Service Windows, SQL Server, moteur Python | `C:\Juniro\app`, `C:\Juniro\analytics` |

La base `Juniro_Evo` est sur le PC fixe ; le PC de développement y accède par Tailscale.

---

## 2. Le service Windows et la planification

**Fichiers :** `Program.cs`, `Configuration\HangFireAuthFilter.cs`, `appsettings.json`,
`appsettings.Production.json` (sur le PC fixe, non versionné), `Services\AnalyticsJob.cs`.

### 2.1 Démarrage

1. Les journaux Serilog sont ouverts en premier (`logs\scraper-AAAAMMJJ.log` à côté de l'exécutable).
2. Si l'argument `--backfill` est présent, le programme lance le rattrapage et s'arrête (chapitre 3.3).
3. Sinon il démarre en service Windows `JuniroScraperService`. La configuration est lue dans le
   dossier de l'exécutable (et non `C:\Windows\system32`, dossier courant d'un service).
4. Il vérifie la chaîne de connexion `ConnectionStrings:JuniroDb` ; absente, il s'arrête avec un
   message explicite.

### 2.2 Hangfire : les tâches planifiées

Hangfire est un planificateur qui stocke ses tâches dans la base SQL. Un **seul worker** : deux
tâches ne tournent jamais en même temps (pas deux navigateurs, et le calcul attend la fin du scraping).

| Tâche | Heure | Méthode | Condition |
|---|---|---|---|
| `daily-flashscore-scrape` | 03h00 | `FlashscoreScraperJob.ExecuteDailyScrapeAsync` | toujours |
| `repair-recent-matches` | 04h30 (`Repair:Cron`) | `FlashscoreScraperJob.ExecuteRecentRepairAsync` (rattrapage automatique, 3.3) | sauf `Repair:Enabled = false` |
| `daily-analytics` | 05h30 (`Analytics:Cron`) | `AnalyticsJob.RunDailyAsync` | `Analytics:Enabled = true` |
| `supabase-sync` | 06h15 (`Supabase:Cron`) | `SupabaseSyncJob.RunAsync` (chapitre 13) | `Supabase:Enabled = true` |

Tableau de bord : `http://<IP du PC fixe>:5000/hangfire`, réservé au réseau local et à Tailscale
(`HangFireAuthFilter.cs`). On peut y relancer une tâche à la main (« Trigger now »).

### 2.3 Lancement du moteur Python

`AnalyticsJob.RunAsync("daily")` exécute
`C:\Juniro\analytics\.venv\Scripts\python.exe -X utf8 -m juniro_analytics daily`, recopie chaque
ligne de sa sortie dans les journaux du service, et échoue si Python est absent, dépasse
`Analytics:TimeoutMinutes` (90 par défaut) ou renvoie une erreur.

---

## 3. Collecte des données sur Flashscore

**Fichiers :** `Services\FlashscoreScraperJob.cs` (sections numérotées 1 à 11 dans le fichier),
`Models\ScrapedMatchData.cs`, `Models\ScrapeQualityReport.cs`, `Configuration\BackfillCommand.cs`,
`Models\SeasonBackfillOptions.cs`.

### 3.1 Les ligues suivies

Liste `AllowedLeagues` (section 1 du fichier) : Ligue 1, Ligue 2, Premier League, Championship,
Bundesliga, Bundesliga 2, LaLiga, LaLiga2, Serie A, Serie B. Chaque ligue est reconnue par le début
de son adresse Flashscore (`/football/france/ligue-1/`) ou, à défaut, par son pays et son nom affiché.

### 3.2 Le job journalier

`ExecuteDailyScrapeAsync` (section 2), réglé par `ScraperSettings` dans appsettings :

| Réglage | Défaut | Effet |
|---|---|---|
| `BaseUrl` | `https://www.flashscore.fr` | site visité |
| `DaysBehind` / `DaysAhead` | 1 / 3 | fenêtre J-1 … J+3 |
| `Headless` | true | navigateur invisible |
| `ContextRecycleEveryNMatches` | 20 | navigateur « neuf » tous les N matchs (mémoire) |
| `PreferredBookmakerIds` | 141 (Betclic) | bookmaker dont on garde les cotes |

**Étape 1 — collecte des matchs (section 4).** Pour chaque jour de J-1 à J+3 : ouverture de
l'accueil Flashscore à cette date, défilement et clic sur « Afficher plus » pour tout charger,
puis extraction des matchs dont la compétition est l'une des 10 ligues. Un même match n'est
gardé qu'une fois.

**Étape 2 — chaque match (section 5).** `ScrapeMatchDetailTabsAsync` ouvre la page du match et
lit, dans l'ordre : équipes et clés Flashscore, date et heure (stockées en heure de Paris),
statut, journée, score final et mi-temps, statistiques, puis les cotes. Un match « en cours » ou
au statut illisible n'est pas enregistré : il sera repris au passage suivant.

**Étape 3 — enregistrement** par `JuniroRepository.SaveFullMatchDataAsync` (chapitre 4), puis
pause aléatoire de 0,7 à 1,4 s.

**Étape 4 — contrôle qualité** en fin d'exécution (chapitre 3.7).

Le journalier ne relève que la **cote actuelle** : l'évolution des cotes naît des passages
successifs (J-3, J-2, J-1, jour J), chacun enregistré avec son étiquette (chapitre 4.3).

### 3.3 Le rattrapage historique (backfill)

`ExecuteSeasonBackfillAsync` (section 3), lancé en ligne de commande :

```
Juniro.ScraperService.exe --backfill [--from 2026-08-01] [--to 2026-10-31] [--leagues "Ligue 1,Serie A"]
                          [--dry-run] [--force] [--include-unfinished] [--limit N] [--headless false]
                          [--delay-ms 1000] [--max-more-clicks 60] [--dump-stats] [--environment Development]
```

1. Pour chaque ligue, ouverture de la page « résultats » et clics sur « Afficher plus de matchs »
   jusqu'à charger toute la période (`CollectLeagueResultKeysAsync`). Un avertissement « INCOMPLÈTE »
   signale une liste tronquée.
2. Les matchs déjà **complets** en base sont sautés (`GetCompletedMatchKeysAsync` : terminé, scores
   final et mi-temps, journée, statistiques des deux équipes avec tirs cadrés et corners, et cotes
   1X2, Plus 2,5 et BTTS), sauf avec `--force`.
3. Chaque match est lu comme au journalier, mais avec la **cote d'ouverture et la cote finale**.
4. `--dry-run` lit sans rien écrire ; `--limit N` s'arrête après N matchs (essais).

Les options sont lues par `BackfillCommand.TryBuildOptions` et rangées dans `SeasonBackfillOptions`
(une saison commence le 1er juillet).

**Rattrapage automatique (chaque matin, depuis le 10 novembre 2026).** Le journalier ne repasse
qu'une fois sur un match joué (à J+1, 03h00). Si ce passage échoue (PC éteint, Flashscore lent,
statistiques pas encore publiées, plantage), le match restait incomplet pour toujours : c'est ce qui
est arrivé aux matchs du samedi 31 octobre 2026 (35 matchs avec score mais sans statistiques, 14
restés « Scheduled » alors qu'ils étaient joués). Le job Hangfire `repair-recent-matches`
(`ExecuteRecentRepairAsync`, 04h30, avant les calculs) est le filet de sécurité :

1. il lit la page « résultats » des 10 ligues en entier (`Repair:MaxMoreClicks` clics au plus, 30 par
   défaut ; la lecture s'arrête quand toute la saison est affichée) et retient les matchs **jamais
   enregistrés** de la saison, quelle que soit leur date (premières journées comprises : le
   11/11/2026, il manquait les journées d'août en Championship et en LaLiga2). Contrôle : dans le
   journal du service, chaque ligue affiche « N matchs après X clic(s) » ; « 0 clic » avec une
   liste courte signifie que Flashscore a encore changé le bouton « Montrer plus de matchs »
   (recherche : `moreButtonFinder` dans `CollectLeagueResultKeysAsync`) ;
2. il ajoute les matchs en base des `Repair:DaysBack` derniers jours (10 par défaut) qui sont
   **incomplets** (`GetRepairCandidatesAsync` : statut non terminé alors que le coup d'envoi est passé
   de plus de 3 h, score, mi-temps, statistiques ou cotes manquants), même s'ils n'apparaissent pas
   sur la page « résultats » ;
3. il les repasse avec le moteur du backfill (cote d'ouverture + cote finale ; un report ou une
   annulation découverts sont enregistrés). Au plus `Repair:MaxMatches` matchs (250).

Un match incomplet de plus de 10 jours n'est plus retenté (Flashscore n'aura rien de plus) : le
script `SQL\4_Controles\Q5_TrousDeDonnees.sql` le signale (« --backfill ») avec la commande à lancer.
Journal : `TriggerSource = Repair` dans `dbo.AppExecutionRuns` (Q4, bloc 1 ; Q5, bloc 4), une ligne
`Repair.Selection` qui liste les matchs repris et ce qui leur manquait. Réglages : section `Repair`
de `appsettings.Production.json` (`Enabled`, `Cron`, `DaysBack`, `MaxMoreClicks`, `MaxMatches`).

### 3.4 Lecture d'un match : résumé, statut, journée, mi-temps

- **Statut** (`MapMatchStatus`, section 11) : Terminé, Reporté, Annulé, Interrompu, À venir ;
  « Live » et « Unknown » ne sont jamais écrits en base (contrainte `CK_Matches_Status`).
- **Journée** : lue dans le fil d'Ariane (« … - Journée 12 »).
- **Score mi-temps** (`ExtractHalfTimeScoreAsync`, section 7) : lu dans l'en-tête « 1re mi-temps ».
- **Adresses des sous-pages** (`ResolveMatchRoutesAsync`, section 6) : lues dans la table de
  routage de la page, pour suivre automatiquement un renommage par Flashscore.

### 3.5 Statistiques

`ExtractMatchStatsAsync` (section 10) lit le tableau de statistiques selon plusieurs méthodes
(nouvelle et ancienne présentation de Flashscore) ; `ResolveStatTarget` / `MapStatRow` font
correspondre chaque libellé (« Tirs cadrés », « Expected goals (xG) »…) à sa colonne :
xG, possession, tirs, tirs cadrés, corners, fautes, cartons. Un libellé inconnu est signalé dans
les journaux (`LogUnmappedStatCategories`).

### 3.6 Cotes

`ScrapeAllOddsMarketsAsync` (section 6) ouvre une sous-page par marché : 1X2, Plus/Moins
(lignes 1,5 / 2,5 / 3,5), Les deux marquent (BTTS), Double chance (1X, X2), Remboursé si nul (DNB).

1. **Lecture du tableau** (`ReadOddsRowsAsync`) : une ligne par bookmaker ; chaque cellule porte la
   cote actuelle et, dans son infobulle, la cote d'ouverture (`ParseOddCell`).
2. **Choix du bookmaker** (`PickRow`) : le premier de `PreferredBookmakerIds` présent.
3. **Contrôles de cohérence** — une cote douteuse est laissée vide plutôt qu'enregistrée fausse :
   - marge du marché entre −2 % et +25 % (`KeepIfCoherent`) ;
   - Plus/Moins dans le bon sens (`ValidateOverUnder`) ;
   - double chance plausible (`IsCoherentDoubleChance`).
4. Une page qui retombe sur un autre marché est abandonnée (`OpenOddsMarketAsync`).

### 3.7 Contrôle qualité et diagnostics

- **Rapport de qualité** (`ScrapeQualityReport.cs`) : pour chaque donnée, part des matchs où elle a
  été trouvée, comparée à un seuil (tableau `Expectations`, ex. xG ≥ 85 %, cote 1X2 ≥ 90 %).
  « ALERTE » = Flashscore a probablement changé une page. Le rapport est écrit dans les journaux et
  dans `dbo.AppExecutionLogs`, et une alerte fait passer l'exécution en `Warning` (chapitre 4.5).
- **Captures de diagnostic** (`SaveDiagnosticAsync`) : HTML + image de la page fautive dans
  `diagnostics\` (5 au maximum par exécution).
- **Surveillance hebdomadaire** : `SQL\3_Surveillance\S1_SurveillanceHebdomadaire.sql`.
- **Contrôle de l'import de la nuit** : `SQL\4_Controles\Q4_DernieresExecutions.sql` (chapitre 4.5).

---

## 4. Stockage dans SQL Server

**Fichiers :** `Data\JuniroRepository.cs`, `Configuration\ParisTime.cs`, scripts `SQL\1_` à `SQL\4_`.

### 4.1 Les tables brutes (schéma dbo)

| Table | Contenu |
|---|---|
| `Leagues`, `Seasons` | ligues (division `TierLevel`) et saisons « 2026-2027 » (`IsCurrent`) |
| `Teams` | équipes, avec leur clé Flashscore |
| `Matches` | un match : date (heure de Paris), statut, scores final et mi-temps, journée |
| `MatchTeamStats` | statistiques par équipe et par match (`IsHome`) |
| `Odds` | un relevé complet de 15 cotes par ligne (`OddIndex` 1 = ouverture … N = dernier) |
| `OddsQuotes` | une cote par ligne : marché, sélection, ligne, valeur, date UTC, étiquette du relevé |
| `Bookmakers` | bookmakers |
| `AppExecutionRuns`, `AppExecutionLogs` | journal de chaque exécution du scraper (une ligne par exécution / une ligne par événement, chapitre 4.5) |

### 4.2 Règles d'écriture

`SaveFullMatchDataAsync` écrit ligue → saison → équipes → match → statistiques → cotes dans une
**seule transaction** : au moindre problème, rien n'est écrit. Règles :

- la date n'est écrite que si l'heure a bien été lue ;
- un match terminé ne redevient jamais « à venir » ;
- une valeur vide n'écrase jamais une valeur connue (scores, journée) ;
- un relevé de cotes identique au précédent n'est pas réécrit (`Odds`) ;
- la cote d'ouverture n'est insérée qu'une fois, datée avant tous les autres relevés.

### 4.3 Les étiquettes des relevés de cotes

`ResolveSnapshotLabel` donne à chaque relevé une étiquette selon l'écart avec le coup d'envoi :

| Étiquette | Signification |
|---|---|
| `OPENING` | vraie cote d'ouverture (lue par le rattrapage) |
| `J-3`, `J-2`, `J-1` | relevé 3, 2 ou 1 jour avant |
| `DAY` | relevé le jour du match (le service passe à 03h00) |
| `H-1` | moins d'une heure avant |
| `CLOSING` | match commencé ou terminé : cote finale |

Ces étiquettes sont essentielles pour la suite : la décision de pari utilise la cote **DAY**
(sinon J-1, J-2, J-3), jamais la cote finale (chapitre 6.6).

### 4.4 Scripts SQL de diagnostic, correction et contrôle

Tous commencent par `USE [Juniro_Evo]` ; les corrections s'exécutent en deux temps (aperçu, puis
`@Appliquer = 1`) et ne peuvent pas être rejouées (`dbo.SchemaMigrations`). Détail : `SQL\README.md`.

| Dossier | Scripts | Usage |
|---|---|---|
| `1_Diagnostics` | D1 schéma, D2 ligues et étiquettes, D3 inversion du nul, D4 précision par étiquette, D5 cotes de la saison en cours, D6 couverture des cotes de décision par saison et par semaine | comprendre l'état des données (lecture seule) |
| `2_Corrections` | C1 heures UTC → Paris, C2 cotes mal attribuées, C3 ligue parasite, C4 noms de saison, C5 journal d'exécution | une fois, dans l'ordre |
| `3_Surveillance` | S1 | chaque semaine |
| `4_Controles` | Q1 saisons courantes, Q2 journées incomplètes, Q3 derniers relevés, Q4 dernières exécutions, Q5 trous de données (matchs à compléter, équipes en retard, rattrapages automatiques) | après un test ou un rattrapage ; Q4 chaque matin ; Q5 quand un match manque dans l'application |

### 4.5 Journal d'exécution : savoir si l'import a réussi

**Fichiers :** `Data\JuniroRepository.cs` (dernière section), `Services\FlashscoreScraperJob.cs`
(`ExecuteDailyScrapeAsync`, `ExecuteSeasonBackfillAsync`, `RunStats`, `FinishRunAsync`),
`SQL\2_Corrections\C5_JournalExecutions.sql`, `SQL\4_Controles\Q4_DernieresExecutions.sql`.

Chaque exécution (job journalier `ScheduledTask` ou rattrapage `Backfill`) laisse **une ligne**
dans `dbo.AppExecutionRuns` et **une ligne par événement** dans `dbo.AppExecutionLogs`.

**Cycle de vie d'une exécution**

1. `StartExecutionRunAsync` crée la ligne avec `Status = 'Running'`. Au passage, une exécution de
   la même source restée `Running` depuis plus de 12 h (48 h pour le rattrapage) passe à
   `Interrupted` : le processus s'est arrêté sans pouvoir écrire sa fin.
2. Pendant le travail, `LogExecutionAsync` écrit les événements :

   | Secteur | Niveau | Sens |
   |---|---|---|
   | `.Startup` | INFO | début, fenêtre traitée |
   | `.Collected` | INFO | matchs trouvés, par ligue |
   | `.MatchProcessed` | INFO | **un match enregistré** (score, cotes) : la preuve de l'import |
   | `.MatchSkipped` | INFO | match en cours, non enregistré, repris au passage suivant |
   | `.MatchEmpty`, `.UnparsedKickoff` | WARN | page illisible, match ignoré |
   | `.MatchError` | ERROR | échec sur un match (exception jointe) |
   | `.Quality` | INFO / ERROR | rapport de qualité (ERROR = alerte, chapitre 3.7) |
   | `.Crash` | FATAL | plantage du job |
   | `.Finished` | INFO / WARN / ERROR | bilan : `[Statut] Terminé en … : X enregistrés sur Y trouvés … Par ligue …` |

3. `FinishRunAsync` (toujours appelé, même après un plantage) écrit le bilan et appelle
   `CompleteExecutionRunAsync`, qui remplit `Status`, `FinishedAtUtc`, `DurationSeconds`,
   `MatchesFound`, `MatchesSaved`, `MatchesSkipped`, `MatchesFailed`, `QualityDegraded`,
   `LogWriteFailures`, `Summary`, `ErrorMessage`.

**Statut final**

| Status | Quand |
|---|---|
| `Success` | tout est enregistré, contrôle qualité au vert |
| `Warning` | au moins un match en échec, alerte qualité, ou aucun match trouvé |
| `Failed` | plantage, ou des échecs sans aucun match enregistré |
| `Interrupted` | jamais terminé (PC éteint, service arrêté en plein travail) |

**Robustesse**

- `LogExecutionAsync` ne lève **jamais** d'exception : une panne du journal ne fait plus échouer
  l'enregistrement d'un match. Un échec est retenté une fois, puis écrit dans le fichier Serilog
  (`logs\`) et compté dans `LogWriteFailures`.
- Les textes trop longs sont tronqués à la taille de leur colonne au lieu de faire échouer l'INSERT.
- La structure des tables est lue au premier appel (`GetJournalSchemaAsync`) : le nom de la colonne
  identifiant (`RunId` ou `Id`) et les colonnes présentes. Sans le script C5, seul `Status` est mis
  à jour ; avec lui, tout le bilan.

**Mise en place (une fois)** : exécuter `C5_JournalExecutions.sql` (aperçu, puis `@Appliquer = 1`).
Il ajoute les colonnes, élargit `Status` et une éventuelle contrainte CHECK, reconstitue le bilan
des anciennes exécutions à partir de `AppExecutionLogs` et crée la vue `dbo.vw_ScraperRuns`
(heures de Paris, colonne `Etat` en français, « BLOQUÉE ? » au-delà de 6 h en cours).

**Chaque matin** : `Q4_DernieresExecutions.sql` — dernières exécutions, présence d'un passage pour
chacun des 7 derniers jours, problèmes et matchs enregistrés de la dernière exécution.

---

## 5. Le moteur de calcul : installation et organisation

**Fichiers :** dossier `Analytics\` (sources) installé dans `C:\Juniro\analytics`,
`Deploy\install-analytics.ps1`, `SQL\5_Analytique\A1` à `A5`.

### 5.1 Installation et fichiers de réglage

`install-analytics.ps1` copie le moteur, crée l'environnement Python (3.11 à 3.13), installe les
bibliothèques (`requirements.txt`) et vérifie la connexion. Sur le PC fixe, copier d'abord le dossier
`Analytics` du projet (par exemple dans `C:\Juniro\releases\Analytics`, l'archive de `publish.ps1` ne
le contient pas), puis, en PowerShell administrateur :
`Set-ExecutionPolicy -Scope Process Bypass -Force ; Get-ChildItem C:\Juniro\releases -Recurse | Unblock-File ;
.\install-analytics.ps1 -SourceDir "C:\Juniro\releases\Analytics"`.
Le moteur lit le appsettings avec la même tolérance que .NET (commentaires, virgule en trop avant
`}`) et prend le pilote ODBC 17 si le 18 n'est pas installé (`db.choose_odbc_driver`).
Trois fichiers de réglage :

| Fichier | Contenu | Écrit par |
|---|---|---|
| `config.json` | tous les réglages (données, variables, Elo, modèles, phases, stratégies, balayage) | vous |
| `local.json` | chemin du appsettings contenant la chaîne de connexion de CE PC (aucun mot de passe) | l'installeur |
| `tuned.json` | meilleur réglage des modèles | la commande `tune` |

L'installeur remplace `config.json` (l'ancien est sauvegardé en `config.backup-….json`) mais ne
touche ni à `local.json`, ni à `tuned.json`, ni aux modèles enregistrés (`models\`).

### 5.2 Les schémas analytiques

Les données brutes (`dbo`) ne sont jamais modifiées par le moteur. Ses objets sont rangés à part :

| Schéma | Contenu | Créé par |
|---|---|---|
| `core` | vues canoniques sur les données brutes | A2 |
| `feat` | `MatchFeatures` : toutes les variables, une ligne par match | le moteur (reconstruit à chaque passage) |
| `ml` | `ModelRuns` (une exécution de modèles), `MatchPredictions` (sorties par match) | A1 |
| `sim` | `Runs` (résultat d'une stratégie/variante/ligue/phase), `Bets` (paris simulés), `LiveSignals` (paris proposés), `RuleScan` (balayage des seuils) | A1, A4, A5 |
| `app` | vues de consultation | A3 |
| `lab` | tables de l'application Windows de stratégies (vide pour l'instant, chapitre 14) | L1 |

Ordre d'installation : A1 → A2 → premier `backtest` → A3 → A4 → A5 → réexécuter A3 et A4 → A6.

### 5.3 Lecture des données

`data.py` lit `core.vw_Matches` (un match par ligne, statistiques à plat) et `core.vw_OddsByLabel`
(dernière cote de chaque relevé), renomme les colonnes, déduit la division (TierLevel ou nom de la
ligue) et donne à chaque sélection un nom unique : `1X2_HOME`, `BTTS_YES`, `OU25_OVER`, `DC_1X`,
`DNB_AWAY`… La vue `core.vw_TeamMatch` (deux lignes par match) sert à vos propres analyses.

### 5.4 Les commandes

`.\.venv\Scripts\python.exe -X utf8 -m juniro_analytics <commande>` depuis `C:\Juniro\analytics`
(`__main__.py`) :

| Commande | Rôle | Durée |
|---|---|---|
| `check` | connexion, vues/tables, volume de données | secondes |
| `features` | recalcule les variables (`feat.MatchFeatures`) | 1 min |
| `backtest [--include-holdout] [--label …]` | walk-forward + simulation + balayage | 10–30 min |
| `simulate [--run-id N]` | rejoue les stratégies sur un backtest existant | < 1 min |
| `scan [--run-id N]` | rejoue le balayage des seuils | < 1 min |
| `tune [--trials 16]` | réglage fin des modèles → `tuned.json` | 10–20 min |
| `daily` | prédictions et paris proposés du jour (lancée par le service) | 1–10 min |
| `tune-elo` | réglage de l'Elo | 2 min |
| `demo` | tout le circuit sur données synthétiques | 5 min |

Tests : `python -m pytest -q` dans le dossier du moteur (`tests\test_pipeline.py`, ~2 min), à
lancer après toute modification.

---

## 6. Les variables : ce que l'on sait avant le match

**Fichiers :** `features.py`, `elo.py`, `odds.py`. Réglages : `config.json > features`, `elo`, `data`.

### 6.1 Le principe anti-fuite

Une variable ne peut contenir que ce qui était connu **le matin du match**. Méthode unique,
appliquée partout (`features.asof`) :

1. on calcule l'état de chaque équipe **après** chacun de ses matchs (moyennes, cumuls…) ;
2. on rattache à chaque match le dernier état **strictement antérieur au jour du match** (minuit).

Aucun résultat du jour même n'entre donc dans les variables. Le test
`test_aucune_fuite_du_futur` modifie un score et vérifie qu'aucune variable antérieure ne change.

### 6.2 Formes récentes

Pour chaque équipe (`team_long` met chaque match sous l'angle des deux équipes) :

- **générales** (`all_*`) sur 3, 5 et 10 matchs : buts pour/contre, xG pour/contre, tirs, tirs
  cadrés, corners, possession, points, xPts, % BTTS, % +2,5, clean sheets, matchs sans marquer ;
- **domicile/extérieur** (`ven_*`) : mêmes statistiques principales, seulement sur les matchs
  joués au même endroit (à domicile pour l'équipe qui reçoit, à l'extérieur pour l'autre) ;
- **pondérées** (`ewm_*`) : moyennes où les matchs récents comptent plus (demi-vie 5 matchs).

### 6.3 Saison, forces, classements

- **Saison en cours** (`ssn_*`) : matchs joués, points, buts, xG, xPts par match ; **chance** =
  points − xPts (une équipe très au-dessus de ses xPts a tendance à redescendre).
- **xPts** (`xpts_from_xg`) : points qu'une équipe « aurait dû » prendre d'après les xG du match.
- **Forces attaque/défense** (`*_att_strength`, `*_def_strength`, versions xG `x*`) : rapportées à
  la moyenne de la ligue, rapprochées de 1 en début de saison (`shrink_k` = 5 matchs) ; buts
  attendus « naïfs » qui en découlent (`naive_exp_*`).
- **Classements avant le match** (`standings`) : réel, aux xPts, de forme sur 10 matchs, rang en %
  et écart au leader, recalculés pour chaque date.
- **Moyennes de la ligue** (`lg_*`) sur les 200 derniers matchs : buts et xG domicile/extérieur,
  % nuls, % BTTS, % +2,5.

### 6.4 Elo

`elo.py` : classement Elo mis à jour match après match.

- Espérance : E = 1 / (1 + 10^(−(Rdom + avantage − Rext)/400)) ; mise à jour R' = R + K·G·(S − E),
  G grandit avec l'écart de buts.
- Une équipe de 2e division démarre 150 points plus bas ; l'Elo est continu entre divisions
  (promotions et relégations comprises) ; ramené de 20 % vers la base à chaque nouvelle saison.
- Variables : Elo des deux équipes, différence, probabilité Elo, évolution sur 3/5/10 matchs.
- Réglage : `config.json > elo` ; la commande `tune-elo` teste 45 combinaisons.

### 6.5 Contexte

Jours de repos, promu/relégué/nouveau, journée, mois, avancement de la saison, différences
domicile − extérieur des principales variables (`diff_*`).

### 6.6 Le marché (les cotes comme variables)

`odds.py` :

1. **Un bookmaker par match** : le premier de `data.preferred_bookmaker_ids`.
2. **Cote de décision** : premier relevé disponible parmi `data.decision_labels`
   (`DAY`, puis `J-1`, `J-2`, `J-3`), marché par marché, toutes les sélections d'un marché venant du
   même relevé. **Jamais la cote finale** : elle n'existe pas encore au moment de décider.
3. **Retrait de la marge** (méthode proportionnelle) : p = (1/cote) / somme(1/cotes).
4. Variables `mkt_*` : probabilités du marché (1X2, BTTS, +2,5), mouvement depuis l'ouverture,
   marge. Désactivables : `--no-market` ou `model.use_market_features = false`.
5. La cote d'ouverture (`open_*`) et de clôture (`close_*`) sont gardées pour mesurer le CLV.
6. **Cote de décision estimée** (`odds.estimate_decision`). Les matchs entrés en base par le
   rattrapage (août à octobre 2026) n'ont que la cote d'ouverture et la cote finale. Pour eux, la
   cote de décision est estimée entre les deux, en probabilité implicite :
   `estimée = ouverture + w × (clôture − ouverture)`. Le poids `w` (entre 0 et 1) est mesuré marché
   par marché sur tous les matchs qui ont les trois relevés : c'est la part du mouvement de cote
   déjà faite au moment de décider. Sans cote finale, l'estimation est la cote d'ouverture. Ces
   cotes portent l'étiquette `EST` (visible dans `sim.Bets.OddLabel`).
   - Réglage : `data.decision_mode` = `estimated_fallback` (défaut : estimation seulement quand le
     relevé manque), `real` (aucune estimation) ou `estimated_all` (tout estimer, pour mesurer l'effet) ;
     option `--decision-mode` en ligne de commande.
   - Limite : la cote finale contient des informations arrivées après la décision (compositions,
     blessures). Une partie entre dans l'estimation : c'est une légère fuite, qui rend les
     résultats sur ces matchs un peu optimistes. On la mesure en comparant deux backtests,
     `--decision-mode real` et `--decision-mode estimated_all`, sur les saisons où les vrais relevés existent.
   - Le backtest et `daily` affichent, pour chaque marché, le nombre de cotes estimées, `w` et
     l'écart moyen entre cote estimée et cote réelle.
   - Mesure du 8 novembre 2026 : w ≈ 0,99 sur tous les marchés, écart moyen 0,003 à 0,009. Le relevé
     de 03h00 le jour du match est déjà presque la cote finale, l'estimation est donc fidèle.

### 6.7 Confrontations directes

`features.h2h_features` : les 5 derniers matchs **terminés** entre les deux mêmes équipes, joués un
jour **strictement antérieur** (lieu et saison indifférents), vus du côté de l'équipe qui reçoit :

| Variable | Contenu |
|---|---|
| `h2h_n` | nombre de confrontations trouvées (0 à 5) |
| `h2h_pts`, `h2h_gd`, `h2h_xgd` | points moyens, différence de buts et de xG moyennes |
| `h2h_goals`, `h2h_btts`, `h2h_over25` | buts par match, fréquences BTTS et +2,5 |
| `h2h_venue_n`, `h2h_venue_pts` | idem, seulement quand elle recevait déjà |
| `h2h_days_since` | jours depuis la dernière confrontation |

Sans confrontation : `h2h_n = 0`, autres variables vides (le boosting sait traiter l'absence).
Réglage : `config.json > features > h2h` (`enabled`, `last_n`) ; `backtest --no-h2h` pour comparer.
**Désactivées par défaut depuis le 8 novembre 2026** : la commande `importance` montre qu'elles
n'apportent rien (retrait : −0,0002 sur le 1X2). Le code reste en place (`enabled: true` pour réessayer).
Ajoutée aussi : `diff_xgd_10`, écart entre les différences de xG des deux équipes sur 10 matchs.

### 6.8 Contrôle des calculs (10 novembre 2026)

Question : les statistiques (Elo, momentum, forme) sont-elles mal calculées, puisqu'elles ne changent
rien aux prédictions ? Réponse : **elles sont justes, mais les cotes contiennent déjà tout ce qu'elles
disent.**

1. **Recalcul indépendant** (script écrit sans le code du moteur, sur les données copiées dans
   Supabase, 7 457 matchs des deux dernières saisons) : Elo d'avant-match, évolution de l'Elo sur 5
   matchs (momentum), matchs joués, points par match, classement (600 matchs tirés au hasard), points,
   buts et xG pour / contre sur 5 matchs, % BTTS et % +2,5 sur 10 matchs, jours de repos. **Aucun
   écart** au-delà de l'arrondi d'affichage, sur environ 75 000 valeurs.
2. **Ce que valent les stats seules** (régression logistique, apprise sur la première moitié, testée
   sur la seconde, 2 891 matchs, hors 5 premiers matchs de saison) — perte logarithmique 1X2 :
   fréquences seules 1,0731 ; Elo seul 1,0225 ; toutes les stats 1,0185 ; **cotes 1,0039** ; cotes +
   toutes les stats 1,0039. Les stats retrouvent environ 80 % de ce que savent les cotes, et ne leur
   ajoutent rien. Même constat sur ±2,5 (cotes 0,6787, cotes + stats 0,6780 : écart dans le bruit).
3. **Pourquoi** : l'écart d'Elo est corrélé à 0,89 avec l'avis du marché, les points par match à 0,82,
   la forme xG à 0,70. Le bookmaker publie un prix qui intègre déjà ces chiffres, plus les
   compositions, blessures et l'argent des parieurs. Le momentum (évolution de l'Elo) est peu corrélé
   au marché (0,28), mais il ne prédit rien de ce que le marché ignore (corrélation avec l'erreur du
   marché : −0,014, dans le bruit).
4. **Ce qui faussait vraiment les stats** : les trous de données (31 octobre 2026). Un match sans
   score n'entre pas dans l'Elo ni dans les formes ; un match sans statistiques n'entre pas dans les
   moyennes de xG. D'où le rattrapage automatique (3.3).

---

## 7. Les modèles et leur évaluation

**Fichiers :** `models.py`, `backtest.py`, `tune.py`. Réglages : `config.json > model`, `phases`,
surchargés par `tuned.json`.

### 7.1 Le modèle de score

Deux régressions de Poisson (gradient boosting) prédisent les **buts attendus** de chaque équipe.
On en déduit la probabilité de chaque score de 0-0 à 10-10 (`score_matrix`), corrigée pour les
petits scores (Dixon-Coles, paramètre rho), puis toutes les probabilités par addition des cases
(`probs_from_matrix`) : 1X2, BTTS, Plus 1,5 / 2,5 / 3,5, score le plus probable. Ces probabilités
sont **cohérentes entre elles** par construction.

**Recalage des buts attendus** (`JuniroModels._fit_goal_calibration`). Sur les données réelles, le
modèle sous-estimait légèrement les buts, surtout pour les matchs annoncés très fermés
(1,9 but attendu pour 2,26 réels, `ml.vw_GoalsCalibration`). À chaque entraînement :

1. les modèles de buts sont entraînés provisoirement sur les 80 % de matchs les plus anciens ;
2. ils prédisent les 20 % les plus récents, qu'ils n'ont pas vus ;
3. on mesure, pour le domicile et l'extérieur, la droite « buts réels = a + b × buts prédits »
   (b borné entre 0,5 et 1,5, a fixé pour que la moyenne recalée égale la moyenne réelle) ;
4. le modèle final est entraîné sur 100 % des matchs et chaque prédiction est corrigée par cette
   droite, avant le calcul de la grille des scores.

Le backtest affiche la correction en janvier et juillet (« Recalage des buts : domicile +0.26 +
0.85 x prédit… »). Réglage : `model.goal_calibration` (`"linear"` ou `"none"`),
`model.calibration_fraction` (0,2). Coût : deux modèles de buts de plus par entraînement.

### 7.2 Les classifieurs et le mélange

Cinq classifieurs dédiés (1X2, BTTS, Plus 1,5, Plus 2,5, Plus 3,5) optimisent directement leur
marché. La **probabilité finale** (`p_home`, `p_btts_yes`, `p_over15`, `p_over25`, `p_over35`…) est la
moyenne pondérée des deux approches (`blend_weight_score_model` = 0,5), puis les trois lignes sont
rendues cohérentes (P(+1,5) ≥ P(+2,5) ≥ P(+3,5)). Avant le 10 novembre 2026, les lignes 1,5 et 3,5 ne
venaient que du modèle de score : les anciens backtests n'ont que `Score_POver15` / `Score_POver35`, et
la simulation les utilise alors à la place (`simulate._probabilities`).
Les colonnes `Clf_POver15/35`, `POver15/35` et `Mkt_POver15/35` exigent
`SQL\5_Analytique\A9_LignesButs.sql` ; tant qu'il n'est pas exécuté, le moteur tourne mais ne les
enregistre pas (`store.save_model_run` n'écrit que les colonnes existantes). La précision des trois
lignes face au marché s'affiche dans le bloc « Précision » du backtest.

Sorties enregistrées par match (`ml.MatchPredictions`) : buts attendus (`ExpTotalGoals`…), score
probable, probabilités du modèle de score (`Score_*`), des classifieurs (`Clf_*`), finales (`P*`)
et du marché (`Mkt_*`) pour comparaison.

### 7.3 Le walk-forward et les phases

`backtest.run_backtest` rejoue l'histoire **mois par mois** : pour chaque mois, les modèles sont
entraînés sur les seuls matchs terminés avant le 1er du mois, puis prédisent ce mois. Toutes les
prédictions sont donc « hors échantillon », comme en réel.

| Phase | Période (actuelle) | Rôle |
|---|---|---|
| TUNING (réglage) | 2022-07 → 2024-06 | choisir les seuils des stratégies |
| TEST | 2024-07 → 2026-06 | juger, sans rien toucher |
| HOLDOUT (en cours) | depuis 2026-07 | réserve, avec `--include-holdout` uniquement |

Fenêtre d'entraînement (`model.train_window`) : `all` (défaut), `1y`, `2y`, ou `decay` (poids
décroissants, ~10 fois plus lent).

### 7.4 Le réglage des modèles (tune)

`tune.py` essaie plusieurs réglages du gradient boosting (vitesse d'apprentissage, taille des
arbres, régularisation, part des variables), chacun avec arrêt anticipé, et les juge **uniquement
sur la période de réglage**. Le meilleur réglage des modèles de buts et celui des modèles de
marché sont écrits séparément dans `tuned.json`. Supprimer ce fichier = revenir à `config.json`.

### 7.5 Les mesures

- **Perte logarithmique** (bloc « Précision » du backtest) : plus elle est basse, meilleure est la
  prédiction ; comparée à celle du **marché** sur les mêmes matchs. Battre le marché est la condition
  nécessaire pour gagner de l'argent.
- **Calibration** (`ml.vw_Calibration`, `ml.vw_GoalsCalibration`) : quand le modèle annonce 60 %,
  l'événement arrive-t-il 60 % du temps ? Par tranche de buts attendus : % de +1,5 / +2,5 / +3,5 /
  BTTS et cote d'équilibre.

### 7.6 Quelles données comptent ? (commande importance)

**Fichier :** `importance.py`. Les modèles sont entraînés une fois sur tout ce qui précède la période
de TEST, puis mesurés sur les matchs de TEST (perte logarithmique 1X2, BTTS, +2,5) :

1. **Retrait d'une famille** : réentraînement sans la famille (Elo, xG, cotes, confrontations
   directes, tirs, corners, classements...). Écart positif = la famille apporte une information que
   les autres n'ont pas. C'est la mesure à croire.
2. **Mélange** (permutation) : les valeurs d'une famille, puis de chaque variable, sont mélangées
   entre les matchs, sans réentraîner. Rapide, mais une variable très corrélée à d'autres paraît inutile.
3. Référence : perte du marché sur les mêmes matchs.

Résultat du 8 novembre 2026 : seules les cotes comptent (+0,013 sur le 1X2 quand on les retire) ;
Elo, xG, forme, classements et confrontations directes restent dans le bruit (±0,0005).

Les familles sont reconnues par le nom des variables (`importance.FAMILIES`). Ordre de grandeur :
0,001 sur le 1X2 correspond à l'écart actuel entre le modèle et le marché. Durée : 10 à 20 minutes
(`--no-ablation` pour le mélange seul, `--repeats N` pour le nombre de mélanges).

---

## 8. Les paris : de la probabilité à la décision

**Fichiers :** `simulate.py`, `scan.py`, `live.py`, `store.py`. Réglages : `config.json > simulation`
et `scan`. Vues : `SQL\5_Analytique\A3` et `A4`.

### 8.1 Paris possibles, cote et avantage (edge)

Pour chaque match et chaque pari disponible (`candidate_bets`) — 1X2, BTTS oui/non,
Plus/Moins 1,5 / 2,5 / 3,5, double chance 1X / X2, remboursé si nul — on calcule :

- la **probabilité** de gagner (et d'être remboursé pour le DNB) selon le modèle ;
- la **cote de décision** (chapitre 6.6) ;
- l'**edge** = espérance de gain pour 1 € misé = p × (cote − 1) − (1 − p − p_remboursé).
  Un edge de +0,05 signifie +5 % d'espérance ; un edge négatif = pari perdant en moyenne.
- le résultat réel (gagné, perdu, remboursé) pour les matchs joués.

**Pas de pari en début de saison** (`config.json > simulation > skip_first_season_matches`, 5 par
défaut). Tant qu'une des deux équipes a joué moins de 5 matchs dans la saison, le match est exclu de la
simulation, du balayage des seuils et des paris proposés. Raison : effectifs remaniés par les
transferts, et moyennes sur 3, 5 et 10 matchs encore calculées sur la saison précédente.
Fonctions : `simulate.season_matches_played` et `early_season_mask`. Le backtest affiche la précision
1X2 du modèle et du marché séparément sur ces matchs et sur le reste. Pour mesurer l'effet du filtre :
`backtest --skip-first 0` (aucun match exclu) contre le réglage par défaut. Les modèles, eux, sont
entraînés et prédisent sur tous les matchs : seul le pari est retenu.

### 8.2 Les stratégies

Une stratégie (bloc de `config.json > simulation > strategies`) combine :

| Champ | Rôle | Exemple |
|---|---|---|
| `bets` | paris autorisés | `["OU25_OVER"]` |
| `conditions` | conditions sur les sorties des modèles | `["exp_total_goals", ">=", 2.8]` |
| `min_edge` | edge minimal (null = aucun) | `0.03` |
| `min_odd` / `max_odd` | fourchette de cotes | `1.30` / `4.00` |
| `one_bet_per_match` | un seul pari par match (le meilleur edge) | `true` |
| `leagues` | limiter à certaines ligues | `["Serie B"]` |
| `staking` | mise fixe ou Kelly fractionné plafonné | `{"method": "kelly", "fraction": 0.25, "max_pct": 0.03}` |
| `grid` | seuils à essayer (une variante par combinaison) | `{"conditions.0": [2.6, 2.8, 3.0]}` |

Deux familles : **combinées** (accord entre modèles, ex. buts attendus ≥ 2,8 et P(BTTS) ≥ 55 % et
P(+2,5) ≥ 55 % ⇒ +2,5) et **valeur** (on joue quand l'edge dépasse un minimum). Les deux se
combinent avec `min_edge`.

Champs utilisables dans `conditions` : `exp_total_goals`, `exp_home_goals`, `exp_away_goals`,
`p_home`, `p_draw`, `p_away`, `p_btts_yes`, `p_over15`, `p_over25`, `p_over35`, les versions
`score_p_*` et `clf_p_*`, `mkt_p_*` (dont `mkt_p_over15` et `mkt_p_over35`), ainsi que `odd`, `prob`, `edge`.

Familles pour les lignes 1,5 et 3,5 (ajoutées le 10 novembre 2026, à évaluer au prochain backtest) :
`value_lignes_15_35` (valeur sur ±1,5 et ±3,5, edge 2 à 8 %), `combo_plus35` (buts attendus ≥ 3,0 et
P(+3,5) ≥ 40 % ⇒ +3,5), `combo_moins35` (buts attendus ≤ 2,4 et P(+3,5) ≤ 25 % ⇒ −3,5),
`combo_plus15` (buts attendus ≥ 3,0 et P(+1,5) ≥ 82 % ⇒ +1,5). Le balayage teste aussi `p_over15`,
`p_over35`, `mkt_p_over15` et `mkt_p_over35`.

### 8.3 Le balayage des seuils

`scan.py` cartographie, sans rien choisir, toutes les règles « signal ≥ ou ≤ seuil ⇒ pari »
(signaux et seuils dans `config.json > scan > signals`), **ligue par ligue** et phase par phase :
taux de réussite, cote d'équilibre (cote minimale pour ne pas perdre), et quand la cote existe :
cote moyenne, ROI, intervalle de confiance à 90 %, CLV. Résultats : `sim.RuleScan`, vue
`sim.vw_RuleScan` (réglage et test côte à côte, colonne `Verdict`).

**Variables brutes** (`config.json > scan > raw_signals`) : les mêmes règles sont balayées sur des
données d'avant tout modèle (écart d'Elo, probabilité Elo, écart de xG, forme sur 10 matchs, points
attendus de la saison, classement, repos, confrontations directes) et sur **la probabilité du marché
seule** (`mkt_p_home`, `mkt_p_away`, `mkt_p_over25`, `mkt_p_btts`). C'est un contrôle : si une
variable brute seule gagnait de façon stable, le marché la négligerait. Le rapport compte séparément
les règles « brutes » et les règles sur les sorties des modèles, et liste les 10 meilleures règles brutes.
Si « P(dom.) du marché ≥ 0,7 ⇒ victoire dom. » gagne autant que la même règle sur la probabilité du
modèle, l'effet vient du prix du bookmaker (favoris sous-cotés), pas du modèle.

**Filtres durs** (`scan.candidates`, réglages `config.json > scan > candidates`). Le rapport affiche
un entonnoir : chaque étape garde les règles qui passent aussi la précédente.

1. ROI positif en réglage et en test ;
2. borne basse de l'IC90 du test > 0 ;
3. positive sur **chaque** saison de réglage et de test (au moins 15 paris par saison). Le ROI saison
   par saison est calculé par `run_scan` (`by_season`) mais n'est pas enregistré en base ;
4. régulière : les seuils voisins (juste au-dessus, juste en dessous) sont aussi positifs en test ;
5. correction pour tests multiples (Benjamini-Hochberg sur toutes les règles testées, 10 % de fausses
   découvertes acceptées) ;
6. facultatif (`require_clv`) : CLV du test ≥ 0. Désactivé par défaut : avec la cote de 03h00, presque
   égale à la cote finale, le CLV vaut à peu près zéro et ne départage rien.

Les règles qui passent tout sauf l'étape 5 sont listées quand aucune ne passe tout.

### 8.6 Le placebo : ce que le hasard produit

**Fichier :** `placebo.py`, commande `placebo [--reps 10] [--run-id N]`. Le balayage est rejoué sur des
résultats **tirés au hasard dans un monde où les cotes sont exactement justes** :

1. pour chaque match, les buts attendus d'une loi de Poisson qui reproduisent les probabilités du
   marché (1X2 sans marge, et Plus de 2,5 si la cote existe ; ajustement par plus proche voisin sur
   une grille, `market_lambdas`) ;
2. un faux score est tiré selon ces buts attendus (`fake_scores`) ;
3. tous les paris sont réglés avec les **vraies** cotes et ce faux score.

Dans ce monde, aucune règle n'a d'avantage. Ce qui passe les filtres est donc du hasard pur. Le rapport
compare, étape par étape de l'entonnoir, le vrai balayage aux tirages : moyenne, 90e centile, maximum, et
**part des tirages au moins aussi bons que le réel**. De 0 à 10 %, le vrai résultat sort du hasard ;
au-delà de 20 %, il n'apporte rien de plus. Même comparaison ligue par ligue (survivantes de l'étape 2).
Durée : environ 30 secondes par tirage.

### 8.7 La carte des biais de prix

**Fichier :** `bias.py`, commande `biais`, réglages `config.json > bias`. Sans aucun modèle : pour
chaque **ligue × pari × tranche de cote** (de 1,01-1,25 à 6,00 et plus), le résultat de paris
systématiques à la cote de décision. Pour chaque case : nombre de paris, réussite comparée à la
probabilité implicite, ROI, ROI saison par saison.

- **Rétrécissement** (DerSimonian-Laird) : le ROI de chaque ligue est ramené vers la moyenne des autres
  ligues pour le même pari et la même tranche, d'autant plus qu'il repose sur peu de paris et que les
  ligues se ressemblent. L'incertitude sur la moyenne elle-même est comptée dans l'IC90.
- **Solide** = borne basse après rétrécissement > 0, toutes les saisons positives (au moins 10 paris
  chacune) et tranches de cote voisines positives.
- Le haut du rapport donne le biais favori / outsider « toutes ligues » (ROI par tranche pour chaque
  pari), le bas les 20 cases à la meilleure borne basse. La carte complète est écrite dans
  `rapports\biais_AAAAMMJJ_HHMM.csv` (séparateur `;`, à ouvrir dans Excel).
- Les matchs de début de saison sont exclus (`skip_first_season_matches`) ; la saison en cours aussi,
  sauf avec `--include-holdout`.

### 8.4 La simulation : bilan par stratégie, par ligue et par saison

`simulate.run_simulation` :

1. chaque stratégie est déclinée en toutes ses variantes (`expand_grid`) ;
2. chaque variante est jouée **dans chaque ligue** séparément, phase par phase (`select_bets`,
   `stake_and_settle`) ; toutes les variantes sont enregistrées dans `sim.Runs` ;
3. dans chaque ligue, la variante au meilleur ROI en **réglage** (avec au moins
   `min_bets_per_league` paris) est retenue : chaque couple **stratégie × ligue est un portefeuille
   à part** ; il n'y a aucun cumul entre ligues ;
4. `season_report` établit le bilan de ce portefeuille **saison par saison** (2022-23 … 2026-27),
   puis deux totaux : **Test** (saisons de test seulement, la seule mesure honnête) et **Total**
   (toutes les saisons, réglage compris, donc flatteur) ;
5. intervalles de confiance (bootstrap) sur la période de test et détail des paris (`sim.Bets`)
   pour les variantes retenues.

À l'écran, un tableau par stratégie : une ligne par ligue, une colonne par saison (ROI et nombre de
paris), puis Test, Total, profit en test et verdict. Dans SSMS : `sim.vw_SeasonReport`
(script A6 ; une ligne par stratégie, ligue et saison, plus les lignes « Test » et « Total »),
`sim.vw_StrategySummary` (variante retenue par ligue, `Verdict`), `sim.vw_VariantsByLeague`
(toutes les variantes), `sim.vw_BetsBreakdown`.

Une stratégie n'est crédible dans une ligue que si elle gagne sur **chaque** saison de test, pas
seulement en moyenne.

**Lecture des verdicts :**

| Verdict | Condition |
|---|---|
| PROMETTEUSE | ROI positif en réglage ET borne basse de l'IC90 positive en test |
| POSITIVE MAIS NON PROUVÉE | ROI de test positif, mais compatible avec le hasard |
| NON RENTABLE | ROI de test négatif |

Plus on essaie de variantes et de ligues, plus certaines paraissent bonnes par hasard : un résultat
isolé ne vaut rien sans confirmation sur des matchs joués après le test.

### 8.5 La décision du jour : un match est-il jouable ?

Chaque matin, `daily` (`__main__.cmd_daily` puis `live.py`) :

1. recalcule les variables avec les résultats de la veille ;
2. charge les modèles du mois (entraînés le 1er du mois sur les données antérieures, conservés
   dans `models\`), ou les entraîne ;
3. prédit les matchs à venir des `live.days_ahead` prochains jours (sans ceux du jour déjà commencés) → `ml.ModelRuns` (type LIVE)
   et `ml.MatchPredictions` ;
4. applique les stratégies de `config.json > simulation > live_strategies` à ces matchs, avec la
   cote de décision (relevé DAY de 03h00, sinon J-1, J-2, J-3 — chapitre 6.6).

**Un match est jouable si et seulement si** une stratégie active le sélectionne : toutes ses
conditions sont remplies, la cote est dans la fourchette et l'edge atteint le minimum. Le pari
proposé (marché, sélection, probabilité, cote vue, edge, mise) est écrit dans `sim.LiveSignals`.
Sans stratégie active, aucun pari n'est proposé : seules les prédictions sont produites.

Pour activer une stratégie : copier sa variante retenue (seuils figés, sans `grid`, avec
`leagues` si elle n'est retenue que dans certaines ligues) dans `live_strategies`, **seulement**
si elle est « PROMETTEUSE ».

Les conditions peuvent porter sur les sorties des modèles (`p_*`, `exp_*`, `score_*`) **et sur la
probabilité du marché** (`mkt_p_home`, `mkt_p_draw`, `mkt_p_away`, `mkt_p_btts`, `mkt_p_over25` :
cote de décision sans marge, `simulate.MARKET_PROBS`).

**Paris fictifs actifs depuis le 8 novembre 2026** (seuils figés, bilan en fin de saison 2026-27,
ne pas les retoucher en cours de route) :

| Stratégie | Règle | Mise |
|---|---|---|
| `papier_seriea_ext_favori` | Serie A, P(ext.) du marché ≥ 0,40 ⇒ victoire extérieure | 1 unité |
| `papier_laliga_dom_favori` | LaLiga, P(dom.) du marché ≥ 0,60 ⇒ victoire domicile | 1 unité |
| `papier_ligue1_value_kelly` | Ligue 1, valeur 1X2 / DNB, edge ≥ 5 %, cote 1,40–6,00, un pari par match | quart de Kelly, ≤ 3 % |

**Suivi** (`SQL\5_Analytique\A7_SuiviParisFictifs.sql`) : un même match est proposé à plusieurs
passages (J-3 à jour J). Le pari retenu est la **dernière proposition faite avant le coup
d'envoi**. Si la stratégie ne le propose plus au dernier passage, il n'est pas joué.
`sim.vw_PaperBets` liste un pari par match et stratégie, avec son résultat (`PENDING` tant que le
match n'est pas joué). `sim.vw_PaperSummary` donne le bilan par stratégie, au total et mois par mois.

**Ce que vous consultez dans SSMS :**

| Vue | Contenu |
|---|---|
| `app.vw_UpcomingPredictions` | matchs à venir : buts attendus, probabilités, cotes justes (1/p), cotes du marché, edge par pari |
| `app.vw_LiveSignals` | paris proposés du jour |
| `sim.vw_PaperBets`, `sim.vw_PaperSummary` | suivi des paris fictifs : résultat de chaque pari, bilan par stratégie et par mois |
| `app.vw_TeamForm` | forme actuelle de chaque équipe (Elo, classements, xG, chance) |
| `sim.vw_StrategySummary` | bilan des stratégies par ligue |
| `sim.vw_SeasonReport` | bilan de chaque stratégie, ligue par ligue et saison par saison |
| `ml.vw_PredictionResults` | prédictions passées et ce qui s'est passé |

---

## 9. Déploiement et mises à jour

**Fichiers :** `Deploy\publish.ps1`, `install-service.ps1`, `update.ps1`, `install-analytics.ps1`,
`Deploy\README.md`, `Juniro.ScraperService.csproj`.

| Quoi | Où | Commande |
|---|---|---|
| Publier le service | PC de dev | `Deploy\publish.ps1` → `%USERPROFILE%\Juniro.Releases\JuniroScraper-….zip` |
| Première installation | PC fixe, admin | `install-service.ps1 -ZipPath …` |
| Mise à jour du service | PC fixe, admin | `update.ps1` (dernière archive) ; sauvegarde dans `C:\Juniro\backup` |
| Installer / mettre à jour le moteur | PC fixe ou dev | `install-analytics.ps1 -SourceDir …\Analytics` |
| Structure SQL | SSMS | scripts `SQL\5_Analytique` (chapitre 5.2) |

Le dossier `Analytics\` est exclu de la compilation C# ; `appsettings.Production.json` et les
journaux sont préservés par `update.ps1`.

---

## 10. Où modifier quoi

| Je veux… | Fichier | Endroit |
|---|---|---|
| changer l'heure du scraping | `Program.cs` | `cronExpression: "0 3 * * *"` |
| régler ou couper le rattrapage automatique | `appsettings.Production.json` | section `Repair` (`Enabled`, `Cron`, `DaysBack`, `MaxMoreClicks`, `MaxMatches`) |
| voir les matchs incomplets | `SQL\4_Controles\Q5_TrousDeDonnees.sql` | blocs 1-2 |
| suivre une autre ligue | `FlashscoreScraperJob.cs` | `AllowedLeagues` (section 1) |
| changer la fenêtre J-1 … J+3 | appsettings | `ScraperSettings:DaysBehind/DaysAhead` |
| changer de bookmaker | appsettings + `config.json` | `ScraperSettings:PreferredBookmakerIds`, `data.preferred_bookmaker_ids` |
| réparer un sélecteur après un changement de Flashscore | `FlashscoreScraperJob.cs` | sections 4 à 7 et 10 (le rapport qualité indique la donnée touchée) |
| changer un seuil d'alerte qualité | `ScrapeQualityReport.cs` | `Expectations` |
| changer les règles Success / Warning / Failed d'une exécution | `FlashscoreScraperJob.cs` | `FinishRunAsync` |
| afficher une nouvelle donnée dans l'application | A8 (colonne de la vue) + `SQL\6_Supabase\001_schema.sql` (colonne de la table, même nom) + `App\Models` + la page XAML | dans cet ordre ; aucun changement dans `SupabaseSyncJob.cs` |
| changer les seuils Gagnante / Perdante | vue `v_strategy_overview` (Supabase) et `App\AppConfig.cs` | les deux |
| ajouter un compte utilisateur | tableau de bord Supabase > Authentication > Users > Add user | chapitre 13.4 |
| changer les droits SQL de l'application de stratégies | `SQL\7_Atelier\L1_CompteApplication.sql` | bloc 3 (un `GRANT` par schéma), puis rejouer le script |
| ajouter une information au bilan d'exécution | `JuniroRepository.cs` (`CompleteExecutionRunAsync`, `ExecutionRunResult`), C5 (colonne), vue `dbo.vw_ScraperRuns` | dans cet ordre |
| ajouter une statistique scrapée | `ScrapedMatchData.cs`, `FlashscoreScraperJob.cs` (`ResolveStatTarget`), `JuniroRepository.cs`, vue `core.vw_Matches` (A2), `data.py`, `features.py` | dans cet ordre |
| ajouter une variable calculée | `features.py` | `BASE_METRICS` ou nouveau bloc dans `build_match_features` |
| régler les confrontations directes | `config.json` | `features.h2h` (`enabled`, `last_n`) |
| changer le nombre de matchs de début de saison sans pari | `config.json` | `simulation.skip_first_season_matches` |
| balayer d'autres variables brutes | `config.json` | `scan.raw_signals` |
| durcir ou assouplir les filtres du balayage | `config.json` | `scan.candidates` |
| changer les tranches de cote de la carte des biais | `config.json` | `bias.bands`, `min_bets` |
| classer une nouvelle variable dans une famille | `importance.py` | `FAMILIES` |
| régler l'Elo | `config.json` | `elo` (aidé par `tune-elo`) |
| changer la cote de décision | `config.json` | `data.decision_labels` |
| régler les modèles | `config.json` / `tuned.json` | `model.hgb*` (aidé par `tune`) |
| changer les périodes réglage/test | `config.json` | `phases` |
| créer ou modifier une stratégie | `config.json` | `simulation.strategies` |
| activer des paris quotidiens | `config.json` | `simulation.live_strategies` |
| changer les signaux du balayage | `config.json` | `scan.signals` |
| activer les calculs dans le service | `appsettings.Production.json` | `"Analytics": { "Enabled": true }` |

Après une modification du moteur : `python -m pytest -q`, puis `install-analytics.ps1` pour la
recopier dans `C:\Juniro\analytics`. Après une modification du service : `publish.ps1` puis `update.ps1`.

---

## 11. Résultats actuels et limites connues

Résumé détaillé : document « Résultats des backtests » du projet Juniro.

- Le modèle (avec les cotes en variables, réglé) est **au niveau du marché**, pas au-dessus
  (1X2 : 1,0075 contre 1,0050 en test).
- Aucune stratégie n'est rentable de façon prouvée avec les seules cotes Betclic (marge ≈ 10 %),
  ni globalement, ni ligue par ligue.
- Trois pistes à suivre en paris fictifs : favoris à domicile en LaLiga, favoris à l'extérieur en
  Serie A, nuls en Serie B.
- **Cotes de la saison 2026-27 suspectes** (précision du marché irréaliste) : à vérifier avec
  `D5_CotesSaisonEnCours.sql` avant d'interpréter la colonne « EN COURS ».
- Une seule cote par match (Betclic) : relever plusieurs bookmakers est la principale piste
  d'amélioration.

---

## 12. Journal des modifications

| Date | Modification | Fichiers | Chapitre |
|---|---|---|---|
| 2026-11-11 | Compte SQL dédié à la future application Windows de stratégies : connexion `juniro_lab`, rôle `lab_app` (lecture seule sur `core`, `ml`, `sim`, `app` ; écriture sur `lab`), schéma `lab` | `SQL\7_Atelier\L1_CompteApplication.sql`, `SQL\README.md` | 5.2, 14 |
| 2026-11-11 | Flashscore a renommé le bouton de la page « résultats » en « Montrer plus de matchs » : la collecte (rattrapage et backfill) s'arrêtait à 0 clic et ne voyait que les ~110 derniers matchs de chaque ligue. Bouton cherché d'abord dans le pied de section (`data-testid=wcl-section-footer`), libellés afficher / voir / montrer | `FlashscoreScraperJob.cs` (`CollectLeagueResultKeysAsync`) | 3.3 |
| 2026-11-11 | Rattrapage : page « résultats » lue en entier (30 clics au plus au lieu de 3) pour retrouver les premières journées manquantes ; Q5 : fenêtre réglable, équipes en retard seules, bilan de saison par ligue | `FlashscoreScraperJob.cs`, `Deploy\appsettings.Production.template.json`, Q5 | 3.3 |
| 2026-11-11 | Moteur : lecture tolérante du appsettings (virgule en trop, commentaires, comme .NET), pilote ODBC 17 pris automatiquement si le 18 manque ; `install-analytics.ps1` affichait « C » au lieu du chemin du appsettings | `settings.py`, `db.py`, tests, `Deploy\install-analytics.ps1` | 5.1 |
| 2026-11-10 | Rattrapage automatique des trous de données (job `repair-recent-matches`, 04h30) : matchs jamais enregistrés de la saison + matchs incomplets des 10 derniers jours ; contrôle Q5 | `FlashscoreScraperJob.cs`, `JuniroRepository.cs`, `SeasonBackfillOptions.cs`, `Program.cs`, `Deploy\appsettings.Production.template.json`, Q5 | 2.2, 3.3 |
| 2026-11-10 | Contrôle indépendant des stats (Elo, momentum, forme, classement) : aucune erreur ; elles n'ajoutent rien aux cotes | — | 6.8 |
| 2026-11-10 | Lignes ±1,5 et ±3,5 : classifieurs dédiés et probabilité finale (comme 2,5), probabilité du marché, 4 familles de stratégies, précision dans le backtest et dans l'application ; script A9 ; Supabase 004 ; la copie vers Supabase ignore (avec avertissement) une colonne absente de Supabase au lieu d'échouer | `models.py`, `features.py`, `odds.py`, `simulate.py`, `backtest.py`, `store.py`, `db.py`, `synthetic.py`, `config.json`, tests, A1, A8, A9, `SQL\6_Supabase\004_lignes_buts.sql`, `SupabaseSyncJob.cs`, `App\Models\Analysis.cs`, `App\Views\ResultsPage.xaml` | 6.6, 7.2, 8.2, 13 |
| 2026-11-08 | Application MAUI (Android + Windows) et base Supabase : projet « juniro », tables, vues et sécurité ; vues d'export A8 ; job Hangfire `supabase-sync` (Npgsql) ; saisie des paris et courbe du ROI dans l'application | `App\**`, `Services\SupabaseSyncJob.cs`, `Program.cs`, `Juniro.ScraperService.csproj`, A8, `SQL\6_Supabase\001_schema.sql`, `Deploy\appsettings.Production.template.json` | 13 |
| 2026-11-08 | Recherche sur toutes les ligues : commande `placebo` (balayage sur faux résultats, cotes justes), commande `biais` (carte ligue × pari × tranche de cote, rétrécissement), filtres durs du balayage (chaque saison, seuils voisins, Benjamini-Hochberg) ; calcul du balayage vectorisé | `placebo.py`, `bias.py`, `scan.py`, `__main__.py`, `config.json`, tests | 8.3, 8.6, 8.7 |
| 2026-11-08 | `daily` écarte les matchs du jour déjà commencés (heure connue et passée) | `live.py` | 8.5 |
| 2026-11-08 | Paris fictifs : 3 stratégies figées dans `live_strategies` (Serie A extérieur favori, LaLiga domicile favori, Kelly Ligue 1) ; conditions possibles sur la probabilité du marché ; suivi `sim.vw_PaperBets` / `vw_PaperSummary` | `simulate.py`, `config.json`, A7, tests | 8.5 |
| 2026-11-08 | Pas de pari sur les 5 premiers matchs de la saison de chaque équipe (simulation, balayage, paris du jour) ; précision 1X2 séparée début / reste de saison ; option `--skip-first` | `simulate.py`, `scan.py`, `live.py`, `backtest.py`, `__main__.py`, `config.json`, tests | 8.1 |
| 2026-11-08 | Correction : les variables presque vides (< 50 valeurs) sont écartées à l'entraînement (erreur « window shape » qui faisait sauter des mois du walk-forward) ; confrontations directes désactivées par défaut ; probabilités du marché ajoutées au balayage brut | `models.py`, `backtest.py`, `config.json`, tests | 6.7, 7.6, 8.3 |
| 2026-11-08 | Confrontations directes (`h2h_*`) et `diff_xgd_10` ; balayage des variables brutes (`scan.raw_signals`) ; commande `importance` (retrait et mélange par famille, variable par variable) ; option `--no-h2h` | `features.py`, `scan.py`, `importance.py`, `__main__.py`, `config.json`, tests | 6.7, 7.6, 8.3 |
| 2026-11-08 | Journal d'exécution fiable : chaque exécution est clôturée (Success / Warning / Failed / Interrupted, fin, durée, compteurs, bilan par ligue) ; journal qui ne fait plus jamais échouer un match ; ligne `MatchProcessed` rétablie dans le journalier ; script C5, vue `dbo.vw_ScraperRuns`, contrôle Q4 | `JuniroRepository.cs`, `FlashscoreScraperJob.cs`, `BackfillCommand.cs`, C5, Q4, S1 | 4.5 |
| 2026-11-08 | Estimation de la cote de décision (ouverture + w × (clôture − ouverture)) quand le relevé manque ; modes `real` / `estimated_fallback` / `estimated_all` | `odds.py`, `__main__.py`, `config.json`, tests | 6.6 |
| 2026-11-07 | Diagnostic D6 : couverture des cotes de décision (DAY / J-1 / J-2 / J-3) par saison et par semaine | `SQL\1_Diagnostics\D6_CouvertureCotesDecision.sql` | 4.4 |
| 2026-11-07 | Recalage des buts attendus (droite a + b × prédit mesurée sur les 20 % de matchs les plus récents) | `models.py`, `backtest.py`, `live.py`, `config.json` | 7.1 |
| 2026-11-07 | Simulation : plus de portefeuille toutes ligues ; bilan par stratégie, par ligue et par saison (+ totaux Test et Total), vue `sim.vw_SeasonReport` | `simulate.py`, A6, tests | 8.4 |
| 2026-11-05 | Commentaires d'en-tête et de fonctions dans tout le code ; création de ce document | tous | — |
| 2026-11-05 | Double chance : le marché n'exige plus la cote « 12 » (non relevée), les paris 1X / X2 sont désormais simulés | `Analytics\juniro_analytics\odds.py` | 6.6, 8.1 |
| 2026-11-04 | Simulation et balayage ligue par ligue, ligne Portefeuille, `sim.vw_VariantsByLeague` | `simulate.py`, `scan.py`, `store.py`, A1, A3, A4, A5 | 8.3, 8.4 |
| 2026-11-02 | Réglage sur deux saisons, colonne « saison en cours » ; diagnostic D5 | `config.json`, A3, D5 | 7.3, 4.4 |
| 2026-11-02 | Balayage des seuils, résultats des modèles séparés, lignes 1,5 / 3,5, commande `tune` | `scan.py`, `tune.py`, `models.py`, A4 | 7.4, 7.5, 8.3 |
| 2026-11-01 | Moteur de calcul Python, structure analytique SQL, tâche `daily-analytics` | `Analytics\`, A1–A3, `AnalyticsJob.cs` | 5 à 8 |

---

## 13. Application mobile et Supabase

### 13.1 Vue d'ensemble

```
 PC FIXE                                      EN LIGNE (Supabase, Paris)            TÉLÉPHONE / PC
 SQL Server Juniro_Evo                        base PostgreSQL « juniro »            Application Juniro (MAUI)
 ┌───────────────────────┐  06h15 chaque jour ┌────────────────────────┐   HTTPS   ┌────────────────────────┐
 │ vues app.vx_Supa* (A8) │ ─────────────────▶ │ matches, predictions,  │ ◀───────▶ │ Matchs, Résultats,     │
 │                        │ SupabaseSyncJob.cs │ match_stats, signals,  │  lecture  │ Stratégies, Mes paris, │
 │                        │ (Hangfire, Npgsql) │ strategy_reports       │           │ Réglages               │
 └───────────────────────┘                    │ user_bets, profiles    │ ◀──────── │ « J'ai parié »         │
                                              └────────────────────────┘ écriture  └────────────────────────┘
```

- La base locale reste la référence : Supabase n'en reçoit qu'une copie (sens unique, chaque matin).
- Les paris saisis dans l'application vivent **uniquement** dans Supabase (`user_bets`) : la copie
  n'y touche jamais.
- Projet Supabase : `juniro` (organisation Juni_Org, région Paris `eu-west-3`),
  adresse `https://ysqdklhsjfzbgwazgfkf.supabase.co`.

### 13.2 Le schéma Supabase

**Fichier :** `SQL\6_Supabase\001_schema.sql` (déjà appliqué au projet ; à rejouer seulement sur un
nouveau projet, dans l'éditeur SQL de Supabase).

| Objet | Contenu | Écrit par |
|---|---|---|
| `matches` | tous les matchs : ligue, saison, heure (UTC), équipes, score, xG, tirs, corners, possession | service (copie) |
| `predictions` | une prédiction par match : la dernière quotidienne faite avant le coup d'envoi, sinon celle du dernier backtest ; probabilités du modèle et du marché, dernière cote | service |
| `match_stats` | forme des deux équipes avant le match (Elo, classement, points, buts, xG, BTTS, +2,5, repos), deux dernières années | service |
| `strategy_reports` | bilan des stratégies du dernier backtest, par famille, ligue et saison (+ Test, Total) | service |
| `signals` | paris fictifs des stratégies actives et leur résultat | service |
| `sync_log` | une ligne par copie réussie (date affichée dans l'application) | service |
| `profiles` | nom affiché de chaque compte (créé automatiquement à la création du compte) | application |
| `user_bets` | paris réellement joués : match, pari, cote, mise, stratégie, bookmaker, note, annulé | application |
| `v_upcoming` | matchs à venir + prédiction + stratégies qui proposent un pari | — |
| `v_prediction_results` | matchs terminés : prédiction, résultat, bon / mauvais (1X2, BTTS, ±1,5, ±2,5, ±3,5, score exact) | — |
| `prediction_kpis(début, fin)` | précision par ligue et toutes ligues : % de bons 1X2, BTTS, ±1,5, ±2,5, ±3,5, score exact ; perte logarithmique modèle / marché (1X2 et chaque ligne de buts) | — |
| `v_strategy_overview` | une ligne par stratégie et ligue, verdict sur le test : **Gagnante** (ROI ≥ +3 %), **Équilibrée**, **Perdante** (ROI ≤ −3 %) | — |
| `v_paper_summary`, `v_signals` | bilan et détail des paris fictifs (verdict « Trop tôt » sous 20 paris réglés) | — |
| `v_user_bets` | paris saisis avec le match, le résultat (calculé par `settle_bet`) et le gain | — |
| `v_matches` | tous les matchs avec prédiction, **toutes les cotes**, Elo / rang avant le match et état (`FINISHED`, `PLAYED` = joué mais score pas encore copié, `LIVE`, `UPCOMING`, `POSTPONED`) | — |
| `v_leagues`, `v_league_stats` | ligues suivies et saison en cours ; moyennes de la saison (buts, % +2,5, % BTTS, % victoires à domicile) | — |
| `league_table(ligue, portée)` | classement calculé sur les matchs terminés : `all`, `home`, `away`, `last5` ; forme sur 5 matchs | — |
| `v_elo` | dernier Elo connu de chaque équipe de la saison en cours + ses 10 dernières valeurs | — |

**Version 2 (novembre 2026) :** `SQL\6_Supabase\002_app_v2.sql` (déjà appliqué au projet) ajoute à
`predictions` les cotes double chance (`odd_dc_1x`, `odd_dc_x2`), remboursé si nul (`odd_dnb_home`,
`odd_dnb_away`) et plus/moins 1,5 et 3,5 (`odd_over15`, `odd_under15`, `odd_over35`, `odd_under35`), et
crée les vues et la fonction ci-dessus. Côté PC fixe, la vue `app.vx_SupaPredictions` de
`A8_ExportSupabase.sql` envoie ces cotes : **rejouer A8 dans SSMS** une fois (le job n'a pas changé).

**Lignes ±1,5 / ±3,5 (10 novembre 2026) :** `SQL\6_Supabase\004_lignes_buts.sql` ajoute à
`predictions` la probabilité du marché (`mkt_p_over15`, `mkt_p_over35`), les colonnes « bien prévu »
de ces lignes à `v_prediction_results` et leurs indicateurs à `prediction_kpis` (la fonction est
supprimée puis recréée : son type de retour change). Côté PC fixe : A9 puis A8 dans SSMS.

**Sécurité (RLS)** : sans connexion, rien n'est lisible. Un compte connecté lit tout, mais ne peut
créer, modifier ou supprimer **que ses propres paris** et son propre profil. Les tables copiées ne
sont modifiables que par le service (utilisateur `postgres`, mot de passe dans
`appsettings.Production.json` du PC fixe, jamais dans l'application).

### 13.3 La copie quotidienne (service Windows)

**Fichiers :** `SQL\5_Analytique\A8_ExportSupabase.sql` (vues `app.vx_Supa*`, une par table, mêmes
noms de colonnes que dans Supabase), `Services\SupabaseSyncJob.cs`, `Program.cs` (job `supabase-sync`).

1. Le job lit chaque vue en entier, puis écrit dans Supabase par paquets de 500 lignes, une
   transaction par table :
   - `matches`, `predictions`, `match_stats` : mise à jour par clé (`INSERT ... ON CONFLICT DO UPDATE`) ;
   - `strategy_reports`, `signals` : remplacement complet (petites tables).
2. Les types sont déduits des colonnes SQL Server ; les heures des vues sont en UTC.
3. Journal : une exécution `SupabaseSync` dans `dbo.AppExecutionRuns` (visible avec
   `Q4_DernieresExecutions.sql`), une ligne par table dans `dbo.AppExecutionLogs`, une ligne dans la
   table Supabase `sync_log`.
4. Réglages (`appsettings.Production.json`, section `Supabase`) : `Enabled`, `ConnectionString`
   (« Session pooler » de Supabase), `Cron` (défaut `15 6 * * *`), `BatchRows`.
5. Une colonne présente dans la vue locale mais absente de la table Supabase (script
   `SQL\6_Supabase` pas encore rejoué) n'est pas envoyée : le reste part quand même, et le journal
   affiche un avertissement `Supabase.MissingColumns` qui la nomme.

### 13.4 Mise en service (une fois)

1. **Mot de passe de la base Supabase** : tableau de bord > Project Settings > Database > *Reset
   database password*. Le garder dans un gestionnaire de mots de passe.
2. **Chaîne de connexion** : bouton *Connect* en haut du tableau de bord > *Session pooler*. Recopier
   l'hôte dans `appsettings.Production.json` du PC fixe, section `Supabase` (modèle :
   `Deploy\appsettings.Production.template.json`), avec le mot de passe, et passer `Enabled` à `true`.
   Format : `Host=...pooler.supabase.com;Port=5432;Database=postgres;Username=postgres.ysqdklhsjfzbgwazgfkf;Password=...;SSL Mode=Require`.
3. **SQL local** : exécuter `SQL\5_Analytique\A8_ExportSupabase.sql` (exige A7) ; le dernier bloc
   affiche le nombre de lignes que recevra chaque table.
4. **Service** : `publish.ps1` puis `update.ps1`. Dans le tableau de bord Hangfire
   (`http://<pc>:5000/hangfire` > Recurring jobs), lancer `supabase-sync` avec *Trigger now* pour la
   première copie.
5. **Comptes** : tableau de bord Supabase > Authentication > Users > *Add user* > *Create new user*,
   un par personne, *Auto Confirm User* coché. Puis Authentication > Sign In / Providers : désactiver
   *Allow new users to sign up* (personne d'autre ne pourra créer de compte).
6. **Application** : voir 13.5.

### 13.5 L'application (dossier `App`)

**Projet :** `App\Juniro.App.csproj` (.NET 10 MAUI, Android + Windows), à ouvrir dans Visual Studio
2026 avec la charge de travail *Développement .NET MAUI*. Le projet du service ignore ce dossier.

| Écran | Contenu |
|---|---|
| Matchs | bandeau de dates (3 jours avant, 4 après) ; filtre Tous / Joués / À venir ; ligues et « paris proposés » ; sections Terminés, Joués (score à la copie du lendemain), À venir ; carte : cotes 1 N 2 (la plus intéressante surlignée), probabilités, valeur, meilleur indice, pronostic ✓ / ✕ |
| Fiche d'un match | en-tête (score ou score probable, probabilités) et 4 onglets : **Cotes** (16 paris par marché : modèle, cote juste, cote, valeur ; stratégies ; nos paris), **Indices** (critères ✓ / ✕ de chaque pari et score), **Stats** (stats du match, comparaison avant le match), **Forme** (5 derniers matchs de chaque équipe) ; bouton **J'ai parié** |
| J'ai parié | pari (liste avec la cote relevée), cote, mise, stratégie suivie, bookmaker, date, note ; gain possible et avis du modèle ; modification, annulation, suppression de ses propres paris |
| Classements | **Ligues** : classement général, domicile, extérieur, 5 derniers, zones (Europe, montée, barrages, relégation), forme ; **Elo** : recherche, filtres pays et division, tri par Elo, tendance ou nom, mini-courbe des 10 derniers matchs |
| Stratégies | paris fictifs en cours avec verdict ; stratégies simulées par ligue (Gagnantes / Équilibrées / Perdantes) ; détail saison par saison, ou courbe des paris fictifs |
| Mes paris | nos paris **ou** les paris fictifs ; récapitulatif **hier / 3 derniers jours / total** (bénéfice, ROI, gagnés / perdus) ; filtres moi / partenaire / les deux, période, ligue, stratégie ; bilan, **courbe du ROI** ou du bénéfice cumulé, répartitions ; historique jour par jour |
| Plus | résultats du modèle (précision 1X2, BTTS, ±2,5, score exact, perte log face au marché), réglages (compte, nom affiché, déconnexion), date des données |

**Journal du service** (`SQL\6_Supabase\003_execution_logs.sql`, vues `app.vx_SupaExecutionRuns` /
`app.vx_SupaExecutionLogs` de A8) : la copie du matin envoie aussi les exécutions du service des 30
derniers jours (`execution_runs`) et leurs événements des 14 derniers jours (`execution_logs`). Ces deux
tables sont **facultatives** dans `SupabaseSyncJob.cs` : si la vue ou la table manque, un avertissement
est écrit et la copie continue. Dans l'application, une pastille en haut de l'onglet Matchs donne l'état :
vert « Scrap OK · 03:02 », orange (alertes, scrap en cours, copie d'hier), rouge (échec, interruption,
pas de scrap depuis plus de 30 h, données de plus de 54 h). La toucher ouvre le **Journal du service**
(aussi dans « Plus ») : dernier scrap, dernière copie, exécutions filtrables (Tout / Scrap / Copie /
Problèmes) et, pour chacune, ses événements (alertes et erreurs, tout, ou matchs traités).
Mise en service : rejouer A8 dans SSMS (exige C5), puis `publish.ps1` et `update.ps1`.

**Indices** (`Services\IndexEngine.cs`) : pour chaque pari, liste de critères favorables (✓) ou non (✕),
évalués seulement quand la donnée existe. Pour tous : modèle contre marché, valeur à la cote, probabilité
≥ 50 %. Buts : buts attendus du modèle, buts et xG d'après la forme, % de +2,5 des deux équipes, BTTS,
moyenne de la ligue. BTTS : BTTS récents, attaques, défenses, buts attendus, BTTS de la ligue. Résultat :
modèle, Elo, classement, points par match, forme, bilan xG, dynamique Elo, repos, avantage du terrain.
Nul : écarts faibles, probabilité du nul, match fermé. Score = favorables / évalués ; les seuils sont en
tête du fichier.

**Thème** : sombre, un accent vert citron (actions, valeur), bleu = domicile, orange = extérieur. Polices
Inter et Inter Display (licence SIL OFL, `Resources\Fonts`, texte de licence dans
`Resources\Fonts-Inter-OFL.txt`). Couleurs : `Resources\Styles\Colors.xaml` **et** `Services\Palette.cs`.

Organisation du code :

| Dossier / fichier | Rôle |
|---|---|
| `AppConfig.cs` | adresse Supabase, clé publique (anon), seuils des verdicts |
| `Services\SupabaseClient.cs` | connexion (e-mail + mot de passe, renouvellement du jeton) et requêtes HTTP vers Supabase |
| `Services\SessionStore.cs` | mémorisation de la session sur l'appareil |
| `Services\DataService.cs` | toutes les requêtes de l'application (une méthode par table ou vue) |
| `Services\BetKeys.cs`, `Fmt.cs`, `Palette.cs` | libellés des paris et stratégies, mise en forme à la française, couleurs |
| `Services\IndexEngine.cs`, `Leagues.cs` | calcul des indices ; ordre, couleur et zones de classement des ligues |
| `Models\` | une classe par table ou vue Supabase, avec les textes et couleurs prêts à afficher |
| `ViewModels\` | logique de chaque écran (filtres, calcul du bilan et de la courbe) |
| `Views\` | écrans XAML |
| `Controls\` | barre des probabilités 1 N 2, courbe, jauge d'indice, mini-courbe Elo (dessinées à la main) |
| `Resources\Styles\` | couleurs, styles et modèles (thème sombre) |

Lancer et installer :

- **Windows** : cible *Windows Machine*, F5. Exécutable à copier :
  `dotnet publish App\Juniro.App.csproj -f net10.0-windows10.0.19041.0 -c Release`.
- **Android** : téléphone en *débogage USB*, choisir le téléphone comme cible, F5. Fichier à installer :
  `dotnet publish App\Juniro.App.csproj -f net10.0-android -c Release`, puis copier le `.apk` de
  `App\bin\Release\net10.0-android\publish` sur le téléphone (autoriser l'installation
  d'applications de sources inconnues).

Limites : sans réseau, l'application n'affiche rien (pas de cache hors ligne). Les prédictions ont au
plus un jour (copie du matin) ; un match joué n'a son score qu'après la copie du lendemain. L'Elo affiché
est celui d'avant le dernier match connu de l'équipe. La version 2 n'a pas pu être compilée lors de son
écriture : toute erreur de Visual Studio est à signaler pour correction.

---

## 14. Application Windows de stratégies (en préparation)

**Fichiers :** `SQL\7_Atelier\L1_CompteApplication.sql`. Le code de l'application n'existe pas encore.

But : une application Windows (WPF, C#) pour consulter les stratégies et leurs résultats, en créer de
nouvelles et lancer les calculs du moteur (simulation, backtest, balayage, biais, placebo, activation
en paris fictifs). Contrairement à l'application du chapitre 13, elle lit **directement SQL Server**
sur le PC fixe, depuis n'importe quel poste relié par Tailscale (`100.80.8.107,49999`).

### 14.1 Le compte SQL de l'application

L'application n'utilise pas le compte du scraper. `L1_CompteApplication.sql` crée :

| Objet | Rôle |
|---|---|
| connexion et utilisateur `juniro_lab` | ce que l'application met dans sa chaîne de connexion |
| rôle `lab_app` | porte les droits ; `juniro_lab` en est membre |
| schéma `lab` | futures tables de l'application (stratégies créées, demandes de calcul, stratégies actives) |

Droits du rôle : `SELECT` sur `core`, `ml`, `sim`, `app` ; `SELECT`, `INSERT`, `UPDATE`, `DELETE`,
`EXECUTE` sur `lab`. Aucun droit sur `dbo` ni sur `feat` : l'application ne peut modifier ni les
matchs, ni les cotes, ni les résultats de simulation. Les vues de `core` et de `app` restent lisibles,
car elles appartiennent au même propriétaire que les tables qu'elles lisent.

Exécution : dans SSMS avec un compte administrateur du serveur, après avoir saisi un mot de passe
(12 caractères au moins) dans `@MotDePasse`. **Ne pas enregistrer le fichier avec le mot de passe** :
il est versionné. Lancé avec un mot de passe vide, le script ne crée rien. Il est rejouable et ne
modifie jamais le mot de passe d'une connexion existante (`ALTER LOGIN` pour le changer).

Le mot de passe ira dans un fichier de réglages local à l'application, exclu de GitHub comme les
`appsettings*.json` (à ajouter au `.gitignore` quand le projet sera créé).

### 14.2 Ce qui reste à construire

Cadrage complet : document `app-windows-strategies.md` du projet Claude « Juniro ». Architecture prévue,
pas encore réalisée : les calculs restent exécutés par le service du PC fixe, à partir de demandes que
l'application écrit dans une table de `lab` ; les stratégies créées dans l'application et les
stratégies actives en paris fictifs sont rangées dans des tables de `lab`, `config.json` restant la
liste de base.
