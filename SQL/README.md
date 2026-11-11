# Scripts SQL — Juniro_Evo

> Explication complète du scraping à la décision : `DOCUMENTATION.md` à la racine du projet.

Tous les scripts commencent par `USE [Juniro_Evo];` : la base sélectionnée dans SSMS n'a pas d'importance.

| Dossier | Contenu | Modifie la base ? |
|---|---|---|
| `1_Diagnostics` | Comprendre l'état des données | Non |
| `2_Corrections` | Réparer des données, **une seule fois**, dans l'ordre | Oui, avec garde-fous |
| `3_Surveillance` | Contrôle hebdomadaire de la qualité de collecte | Non |
| `4_Controles` | Vérifications ponctuelles, après un test ou un rattrapage | Non |

---

## 1_Diagnostics — lecture seule

| Script | À quoi il sert |
|---|---|
| `D1_VerifierSchema.sql` | Inventaire des tables, contraintes, taux de remplissage des statistiques et des cotes, complétude des matchs. Le point de départ en cas de doute. |
| `D2_LiguesEtEtiquettesCotes.sql` | Ligues et saisons existantes (repère la ligue parasite), étiquettes de relevés de cotes, nombre de relevés par match. |
| `D3_VerifierInversionCoteNul.sql` | Vérifie si l'ancienne application a inversé ouverture et clôture sur la cote du **nul** (indices `[1]`/`[0]` intervertis). Résultat à m'envoyer avant toute correction. |
| `D4_PrecisionCotesParEtiquette.sql` | Mesure quel relevé de cotes (ouverture, J-2, J-1, jour J, H-1, clôture) prédit le mieux le résultat. À lancer après D3. |
| `D5_CotesSaisonEnCours.sql` | Cotes de la saison en cours relevées après le coup d'envoi ? |
| `D6_CouvertureCotesDecision.sql` | Part des matchs avec une cote de décision (DAY / J-1 / J-2 / J-3), par saison et semaine par semaine ; dernières exécutions du scraper. |

## 2_Corrections — à exécuter une fois, dans cet ordre

Chaque script fonctionne en deux temps :

1. **Lancer tel quel** → aperçu de ce qui sera modifié, rien n'est écrit.
2. **Passer `@Appliquer` à `1`** et relancer → correction dans une transaction.

Chacun s'enregistre dans `dbo.SchemaMigrations` et **refuse de s'exécuter deux fois**.

| Ordre | Script | Ce qu'il corrige | Statut |
|---|---|---|---|
| 1 | `C1_HeuresMatchsUtcVersParis.sql` | Heures de coup d'envoi écrites en UTC par le service (décalage de 1 h en hiver, 2 h en été). | ☐ |
| 2 | `C2_NettoyageCotesMalAttribuees.sql` | Cotes BTTS, double chance et DNB qui contenaient du 1X2, Plus/Moins vide. **Exige C1.** | ☐ |
| 3 | `C3_SuppressionLigueParasite.sql` | Ligue « championnatdumonde » (4 matchs hors périmètre). Le paramètre est `@Supprimer`. | ☐ |
| 4 | `C4_FormatNomSaisons.sql` | Saisons nommées `2026/2027` au lieu de `2026-2027` comme l'historique. | ☐ |
| 5 | `C5_JournalExecutions.sql` | Journal d'exécution : colonnes de bilan dans `AppExecutionRuns` (fin, durée, compteurs, résumé), reconstitution des anciennes exécutions restées « Running », vue `dbo.vw_ScraperRuns`. **À exécuter avant de republier le service.** | ☐ |

Pour savoir ce qui a déjà été appliqué :

```sql
SELECT * FROM Juniro_Evo.dbo.SchemaMigrations ORDER BY AppliedAtUtc;
```

**Ensuite** : lancer le rattrapage **sans** `--force`. Les matchs nettoyés sont considérés comme incomplets et seront repris automatiquement.

> Avant C1 et C2, le service doit être **arrêté** ou **mis à jour** sur le PC fixe. Sinon l'ancienne version réécrit des données fausses pendant la nuit.

## 3_Surveillance — chaque semaine

| Script | À quoi il sert |
|---|---|
| `S1_SurveillanceHebdomadaire.sql` | Alertes du scraper sur 30 jours, taux de remplissage **semaine par semaine** (une colonne qui chute brutalement = Flashscore a changé ses pages), matchs terminés encore incomplets, dernières exécutions du service, étiquettes de cotes récentes. |

Indépendant de l'application : il reste valable même si le contrôle qualité intégré au scraper tombe en panne.

## 4_Controles — à la demande

| Script | Quand le lancer |
|---|---|
| `Q1_SaisonsCourantes.sql` | Après une intersaison ou un rattrapage : une seule saison courante par ligue, aucun match dans la mauvaise saison. |
| `Q2_JourneesIncompletes.sql` | Après un rattrapage : journées où il manque des matchs. |
| `Q3_DerniersRelevesCotes.sql` | Juste après un test : cotes de la dernière heure, ouverture et clôture par sélection, marchés récupérés par match. |
| `Q4_DernieresExecutions.sql` | **Chaque matin** : l'import de la nuit a-t-il réussi ? Statut (OK / alertes / ÉCHEC / interrompue), durée, matchs trouvés et enregistrés par exécution, jours sans passage, erreurs et matchs enregistrés de la dernière exécution. Exige C5. |
| `Q5_TrousDeDonnees.sql` | Trous de données de la saison : matchs passés restés « à venir », matchs terminés sans score / mi-temps / statistiques / xG / cotes (et s'ils seront repris automatiquement), équipes en retard de matchs, derniers rattrapages automatiques (`repair-recent-matches`). |

## 5_Analytique — calculs, modèles et simulation

Rejouables sans risque : ils ne créent que ce qui manque et ne touchent jamais aux tables `dbo`.

| Ordre | Script | Contenu |
|---|---|---|
| 1 | `A1_StructureAnalytique.sql` | Schémas `core`, `feat`, `ml`, `sim`, `app` et tables de résultats (modèles, simulations, paris proposés). |
| 2 | `A2_VuesSource.sql` | Vues canoniques lues par le moteur Python : `core.vw_Matches`, `core.vw_TeamMatch`, `core.vw_OddsByLabel`. |
| — | *moteur Python* | `backtest` (voir `Analytics/README.md`) : crée `feat.MatchFeatures` et remplit `ml` et `sim`. |
| 3 | `A3_VuesConsultation.sql` | Ce que vous consultez : prédictions à venir, paris proposés, forme des équipes, bilan des stratégies, calibration. |
| 4 | `A4_ResultatsModelesEtSeuils.sql` | Résultats des modèles seuls (par match, par tranche de buts attendus) et balayage des seuils `sim.RuleScan` (à exécuter avant la commande `scan`). |
| 5 | `A5_SimulationParLigue.sql` | Ajoute la ligue aux résultats de simulation (`sim.Runs`, `sim.RuleScan`). **Puis réexécuter A3 et A4.** |
| 6 | `A6_BilanParSaison.sql` | Vue `sim.vw_SeasonReport` : bilan de chaque stratégie par ligue et par saison (+ totaux Test et Total). |
| 7 | `A7_SuiviParisFictifs.sql` | Suivi des paris fictifs : `sim.vw_PaperBets` (un pari par match et stratégie, avec son résultat) et `sim.vw_PaperSummary` (bilan par stratégie, total et par mois). |
| 8 | `A8_ExportSupabase.sql` | Vues `app.vx_Supa*` copiées chaque matin vers Supabase par le service (application mobile). Exige A7 et A9. **À rejouer** après A9 (lignes ±1,5 / ±3,5 : probabilité finale et du marché). |
| 9 | `A9_LignesButs.sql` | Colonnes `Clf_POver15/35`, `POver15/35`, `Mkt_POver15/35` de `ml.MatchPredictions` (classifieurs dédiés aux lignes 1,5 et 3,5, depuis le 10/11/2026). Une fois, avant A8. |

## 6_Supabase — base en ligne de l'application

| Script | Contenu |
|---|---|
| `001_schema.sql` | Tables, vues, fonctions et règles de sécurité du projet Supabase « juniro ». **Déjà appliqué** ; à rejouer seulement sur un nouveau projet, dans l'éditeur SQL de Supabase (pas dans SSMS). |
| `002_app_v2.sql` | Version 2 de l'application : toutes les cotes dans `predictions`, vues `v_matches`, `v_leagues`, `v_league_stats`, `v_elo`, fonction `league_table`. **Déjà appliqué** ; à rejouer après 001 sur un nouveau projet. |
| `003_execution_logs.sql` | Journal du service (tables `execution_runs`, `execution_logs`) affiché dans l'application. À rejouer après 002 sur un nouveau projet. |
| `004_lignes_buts.sql` | Lignes ±1,5 / ±3,5 : `mkt_p_over15`, `mkt_p_over35` dans `predictions`, « bien prévu » dans `v_prediction_results`, indicateurs dans `prediction_kpis`. À appliquer dans l'éditeur SQL de Supabase. |

Requêtes du quotidien :

```sql
SELECT * FROM Juniro_Evo.app.vw_UpcomingPredictions ORDER BY MatchDate;   -- matchs à venir
SELECT * FROM Juniro_Evo.app.vw_LiveSignals ORDER BY MatchDate;           -- paris proposés
SELECT * FROM Juniro_Evo.sim.vw_PaperSummary WHERE Mois = 'Total';      -- bilan des paris fictifs
SELECT * FROM Juniro_Evo.sim.vw_StrategySummary ORDER BY ModelRunId DESC; -- bilan des stratégies
SELECT * FROM Juniro_Evo.app.vw_TeamForm ORDER BY LeagueName, Classement;
```
