#Requires -Version 5.1
#Requires -RunAsAdministrator
<#
    update.ps1 - A LANCER SUR LE PC FIXE, EN ADMINISTRATEUR, A CHAQUE MISE A JOUR
    Arrete le service, sauvegarde la version en cours, deploie la nouvelle,
    redemarre et verifie. appsettings.Production.json et les logs sont preserves.

    Sans -ZipPath, la derniere archive de C:\Juniro\releases est utilisee.
    ETAPES : [1] arret du service  [2] sauvegarde de C:\Juniro\app dans C:\Juniro\backup
             [3] nettoyage (sauf appsettings.Production.json et logs)  [4] copie de la nouvelle version
             [5] redemarrage  [6] verification que le service repond sur le port 5000
    En cas de probleme : la version precedente est dans C:\Juniro\backup\<date>.
    Voir DOCUMENTATION.md, chapitre 9.
#>
[CmdletBinding()]
param(
    [string]$ZipPath,
    [string]$InstallRoot = 'C:\Juniro',
    [string]$ServiceName = 'JuniroScraperService',
    [int]   $Port        = 5000
)

$ErrorActionPreference = 'Stop'

$appDir      = Join-Path $InstallRoot 'app'
$releasesDir = Join-Path $InstallRoot 'releases'
$backupDir   = Join-Path $InstallRoot 'backup'
$browsersDir = Join-Path $InstallRoot 'ms-playwright'
$stamp       = Get-Date -Format 'yyyyMMdd-HHmm'

if (-not (Get-Service -Name $ServiceName -ErrorAction SilentlyContinue)) {
    throw "Le service $ServiceName n'existe pas. Lancez d'abord install-service.ps1."
}

if (-not $ZipPath) {
    $latest = Get-ChildItem $releasesDir -Filter '*.zip' -ErrorAction SilentlyContinue |
              Sort-Object LastWriteTime -Descending | Select-Object -First 1
    if (-not $latest) { throw "Aucune archive .zip trouvee dans $releasesDir." }
    $ZipPath = $latest.FullName
}
if (-not (Test-Path $ZipPath)) { throw "Archive introuvable : $ZipPath" }

Write-Host "Archive a deployer : $ZipPath" -ForegroundColor Cyan

Write-Host '[1/6] Arret du service ...' -ForegroundColor Cyan
Stop-Service -Name $ServiceName -Force -ErrorAction SilentlyContinue
$deadline = (Get-Date).AddSeconds(90)
while ((Get-Service -Name $ServiceName).Status -ne 'Stopped' -and (Get-Date) -lt $deadline) {
    Start-Sleep -Seconds 2
}
if ((Get-Service -Name $ServiceName).Status -ne 'Stopped') {
    throw "Le service ne s'est pas arrete en 90 s. Un scraping est peut-etre en cours : reessayez plus tard."
}

# Chromium lance par Playwright peut survivre a l'arret du service et verrouiller des fichiers.
# On ne cible QUE les processus issus du dossier ms-playwright, jamais le navigateur de l'utilisateur.
Get-Process -ErrorAction SilentlyContinue |
    Where-Object { $_.Path -and $_.Path.StartsWith($browsersDir, [StringComparison]::OrdinalIgnoreCase) } |
    Stop-Process -Force -ErrorAction SilentlyContinue

Write-Host '[2/6] Sauvegarde de la version en cours ...' -ForegroundColor Cyan
New-Item -ItemType Directory -Force -Path $backupDir | Out-Null
$thisBackup = Join-Path $backupDir "app-$stamp"
Copy-Item -Path $appDir -Destination $thisBackup -Recurse -Force
# On ne conserve que les 3 dernieres sauvegardes.
Get-ChildItem $backupDir -Directory -Filter 'app-*' |
    Sort-Object Name -Descending | Select-Object -Skip 3 |
    Remove-Item -Recurse -Force -ErrorAction SilentlyContinue

Write-Host '[3/6] Nettoyage (config et logs preserves) ...' -ForegroundColor Cyan
Get-ChildItem $appDir -Force |
    Where-Object { $_.Name -notin @('appsettings.Production.json', 'logs') } |
    Remove-Item -Recurse -Force

Write-Host '[4/6] Deploiement de la nouvelle version ...' -ForegroundColor Cyan
Expand-Archive -Path $ZipPath -DestinationPath $appDir -Force

$exePath = Join-Path $appDir 'Juniro.ScraperService.exe'
if (-not (Test-Path $exePath)) { throw "Executable absent apres extraction : $exePath" }
if (-not (Test-Path (Join-Path $appDir 'appsettings.Production.json'))) {
    throw 'appsettings.Production.json a disparu. Restaurez-le depuis la sauvegarde avant de demarrer.'
}

Write-Host '[5/6] Redemarrage du service ...' -ForegroundColor Cyan
Start-Service -Name $ServiceName
Start-Sleep -Seconds 8

Write-Host '[6/6] Verification ...' -ForegroundColor Cyan
$svc = Get-Service -Name $ServiceName
if ($svc.Status -ne 'Running') {
    Write-Host ''
    Write-Warning "Le service est $($svc.Status). Consultez les logs :"
    Write-Warning "  Get-Content $appDir\logs\scraper-$(Get-Date -Format 'yyyyMMdd').log -Tail 40"
    Write-Warning "Pour revenir en arriere :"
    Write-Warning "  Stop-Service $ServiceName; Remove-Item $appDir\* -Recurse -Force; Copy-Item $thisBackup\* $appDir -Recurse; Start-Service $ServiceName"
    exit 1
}

try {
    $resp = Invoke-WebRequest -Uri "http://localhost:$Port/hangfire" -UseBasicParsing -TimeoutSec 15
    Write-Host "Dashboard Hangfire : HTTP $($resp.StatusCode)" -ForegroundColor Green
} catch {
    Write-Warning "Service demarre mais le dashboard ne repond pas encore : $($_.Exception.Message)"
}

Write-Host ''
Write-Host "Mise a jour terminee. Service : $($svc.Status)" -ForegroundColor Green
Write-Host "Sauvegarde conservee ici : $thisBackup"
