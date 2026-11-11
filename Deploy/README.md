# Déploiement de Juniro.ScraperService

> Explication complète du scraping à la décision : `DOCUMENTATION.md` à la racine du projet.

Deux machines :

- **PC de dev** (portable) : on y développe et on y compile.
- **PC fixe** : il héberge SQL Server et fait tourner le scraper en permanence, comme service Windows.

Arborescence cible sur le PC fixe :

```
C:\Juniro\
├── app\            version en cours d'exécution
│   ├── appsettings.Production.json   <- la seule config à ne jamais écraser
│   └── logs\
├── ms-playwright\  navigateur Chromium utilisé par le scraper
├── releases\       archives .zip déposées depuis le PC de dev
└── backup\         3 dernières versions, pour revenir en arrière
```

---

## Installation initiale (une seule fois)

### 1. Sur le PC de dev — produire le paquet

```powershell
cd C:\Users\grymy\Juniro.ScraperService\Deploy
.\publish.ps1
```

La compilation est **autonome** (`--self-contained`) : le runtime .NET 10 est embarqué, il n'y a donc
rien à installer sur le PC fixe. Le script affiche le chemin de l'archive produite.

### 2. Copier l'archive sur le PC fixe

Dans `C:\Juniro\releases\`, via Tailscale, un partage réseau ou une clé USB.

### 3. Sur le PC fixe — installer le service

PowerShell **en administrateur** :

```powershell
.\install-service.ps1 -ZipPath C:\Juniro\releases\JuniroScraper-20261028-2200.zip
```

Le script crée l'arborescence, extrait l'application, installe Chromium, enregistre le service
`JuniroScraperService` en démarrage automatique, le configure pour redémarrer seul en cas de crash,
et ouvre le port 5000 sur le profil réseau privé.

### 4. Créer la configuration de production

Le service ne démarre pas sans elle. Copiez `appsettings.Production.template.json` en
`C:\Juniro\app\appsettings.Production.json` et renseignez la chaîne de connexion :

```json
"JuniroDb": "Server=localhost,49999;Database=Juniro_Evo;UID=scraper;Password=...;TrustServerCertificate=True;Encrypt=False;"
```

`localhost` plutôt que l'adresse Tailscale : le scraper et SQL Server sont sur la même machine,
le service ne dépend donc plus de l'état du réseau.

```powershell
Start-Service JuniroScraperService
```

### 5. Vérifier

```powershell
Get-Service JuniroScraperService
Get-Content C:\Juniro\app\logs\scraper-*.log -Tail 30
```

Dashboard Hangfire : `http://localhost:5000/hangfire` depuis le PC fixe, ou
`http://100.80.8.107:5000/hangfire` depuis le portable via Tailscale.

Depuis ce dashboard, le bouton **Trigger now** sur `daily-flashscore-scrape` lance un scraping
immédiat, sans attendre 3 h du matin. C'est le moyen le plus simple de tester.

---

## Mise à jour (à chaque nouvelle version)

Trois commandes au total.

**Sur le PC de dev :**

```powershell
cd Deploy
.\publish.ps1
```

**Copier l'archive** dans `C:\Juniro\releases\` sur le PC fixe.

**Sur le PC fixe**, en administrateur :

```powershell
.\update.ps1
```

Sans argument, `update.ps1` prend la dernière archive du dossier `releases`. Il arrête le service,
sauvegarde la version en cours dans `backup\`, remplace le contenu de `app\` en **préservant
`appsettings.Production.json` et `logs\`**, redémarre, puis vérifie que le dashboard répond.

Si le service ne redémarre pas, le script affiche la commande de retour arrière vers la sauvegarde.

### Revenir à la version précédente

```powershell
Stop-Service JuniroScraperService
Remove-Item C:\Juniro\app\* -Recurse -Force -Exclude appsettings.Production.json,logs
Copy-Item C:\Juniro\backup\app-20261028-2200\* C:\Juniro\app -Recurse -Force
Start-Service JuniroScraperService
```

---

## Points de vigilance

**Mode headless.** Un service Windows tourne sans bureau : `Headless` doit valoir `true` en
production, sinon Chromium ne démarre pas. C'est le cas dans `appsettings.json` et dans le modèle
de production. En dev, `appsettings.Development.json` le remet à `false` pour voir le navigateur.

**Chemin des navigateurs Playwright.** Le service tourne sous `LocalSystem`, qui n'a pas accès au
dossier `ms-playwright` de votre profil. D'où la variable `PLAYWRIGHT_BROWSERS_PATH` qui pointe sur
`C:\Juniro\ms-playwright`, posée par le script d'installation. Après une montée de version de
Microsoft.Playwright, réinstallez le navigateur :

```powershell
$env:PLAYWRIGHT_BROWSERS_PATH='C:\Juniro\ms-playwright'
C:\Juniro\app\playwright.ps1 install chromium
```

**Droits SQL du compte `scraper`.** Hangfire crée son propre schéma dans `Juniro_Evo` au premier
démarrage. Le compte doit donc pouvoir créer des tables :

```sql
ALTER ROLE db_ddladmin ADD MEMBER [scraper];
ALTER ROLE db_datawriter ADD MEMBER [scraper];
ALTER ROLE db_datareader ADD MEMBER [scraper];
```

**Veille du PC fixe.** Un PC endormi ne scrape pas. Désactivez la mise en veille :

```powershell
powercfg /change standby-timeout-ac 0
powercfg /change hibernate-timeout-ac 0
```

**Secrets.** La chaîne de connexion a été retirée de `appsettings.json`. En dev, elle est dans
`appsettings.Development.json` ; en production, dans `appsettings.Production.json`. Ces deux
fichiers sont dans le `.gitignore`. Pour aller plus loin côté dev :

```powershell
dotnet user-secrets init
dotnet user-secrets set "ConnectionStrings:JuniroDb" "Server=..."
```

**Travail à deux.** Le dossier n'est pas encore sous Git. Avec un `.gitignore` en place :

```powershell
git init
git add .
git commit -m "Initial commit"
```

Un dépôt privé (GitHub ou Azure Repos) évitera les divergences entre vos deux postes.

## Moteur de calcul (Analytics)

Le moteur Python (variables, modèles, simulation) s'installe **à côté** du service, dans
`C:\Juniro\analytics`, et n'est pas concerné par `update.ps1` : une mise à jour du service ne
touche ni à son environnement Python ni aux modèles entraînés.

1. SSMS : `SQL\5_Analytique\A1_StructureAnalytique.sql` puis `A2_VuesSource.sql`.
2. Installer Python 3.13 depuis python.org (cocher « Add python.exe to PATH »).
3. Copier le dossier `Analytics` du projet sur le PC fixe, dans `C:\Juniro\releases\Analytics`
   (l'archive de `publish.ps1` ne contient pas le moteur), avec `Deploy\install-analytics.ps1`. Puis,
   PowerShell administrateur :
   ```powershell
   cd C:\Juniro\releases
   Set-ExecutionPolicy -Scope Process -ExecutionPolicy Bypass -Force
   Get-ChildItem C:\Juniro\releases -Recurse | Unblock-File
   .\install-analytics.ps1 -SourceDir "C:\Juniro\releases\Analytics"
   ```
   (Sur le portable : `-SourceDir "C:\Users\grymy\Juniro.ScraperService\Analytics"`.)
   À relancer de la même façon pour mettre à jour le moteur (`config.json` existant conservé).
4. Premier calcul : `cd C:\Juniro\analytics` puis
   `.\.venv\Scripts\python.exe -X utf8 -m juniro_analytics backtest --label "premier essai"`.
5. SSMS : `A3_VuesConsultation.sql`.
6. Automatisation quotidienne : ajouter dans `C:\Juniro\app\appsettings.Production.json`
   ```json
   "Analytics": { "Enabled": true, "Directory": "C:\\Juniro\\analytics", "Cron": "30 5 * * *" }
   ```
   puis redémarrer le service. Le job `daily-analytics` apparaît dans Hangfire.

Détails et commandes : `Analytics\README.md`.
