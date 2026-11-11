#Requires -Version 5.1
#Requires -RunAsAdministrator
<#
    install-service.ps1 - A LANCER UNE SEULE FOIS SUR LE PC FIXE, EN ADMINISTRATEUR
    Installe l'application comme service Windows, installe Chromium pour Playwright
    et ouvre le port du dashboard Hangfire sur le reseau prive.
    ETAPES : [1] extraction dans C:\Juniro\app  [2] dossier des navigateurs Playwright
             [3] installation de Chromium  [4] creation du service Windows (demarrage auto)
             [5] pare-feu : port 5000 du tableau de bord Hangfire, reseau prive uniquement
             [6] controle de appsettings.Production.json (chaine de connexion)
    Voir DOCUMENTATION.md, chapitre 9.
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string]$ZipPath,

    [string]$InstallRoot = 'C:\Juniro',
    [string]$ServiceName = 'JuniroScraperService',
    [string]$DisplayName = 'Juniro Scraper Service',
    [int]   $Port        = 5000
)

$ErrorActionPreference = 'Stop'

if (-not (Test-Path $ZipPath)) { throw "Archive introuvable : $ZipPath" }

$appDir        = Join-Path $InstallRoot 'app'
$browsersDir   = Join-Path $InstallRoot 'ms-playwright'
$releasesDir   = Join-Path $InstallRoot 'releases'
$backupDir     = Join-Path $InstallRoot 'backup'
$exePath       = Join-Path $appDir 'Juniro.ScraperService.exe'
$prodSettings  = Join-Path $appDir 'appsettings.Production.json'

if (Get-Service -Name $ServiceName -ErrorAction SilentlyContinue) {
    throw "Le service $ServiceName existe deja. Utilisez update.ps1 pour mettre a jour."
}

foreach ($d in @($appDir, $browsersDir, $releasesDir, $backupDir)) {
    New-Item -ItemType Directory -Force -Path $d | Out-Null
}

Write-Host '[1/6] Extraction de l''application ...' -ForegroundColor Cyan
Expand-Archive -Path $ZipPath -DestinationPath $appDir -Force
if (-not (Test-Path $exePath)) { throw "Executable introuvable apres extraction : $exePath" }

Write-Host '[2/6] Variable PLAYWRIGHT_BROWSERS_PATH ...' -ForegroundColor Cyan
# Indispensable : le service tourne sous un compte systeme qui n'a pas le dossier
# ms-playwright de votre profil utilisateur.
[Environment]::SetEnvironmentVariable('PLAYWRIGHT_BROWSERS_PATH', $browsersDir, 'Machine')
$env:PLAYWRIGHT_BROWSERS_PATH = $browsersDir

Write-Host '[3/6] Installation de Chromium pour Playwright ...' -ForegroundColor Cyan
$playwrightScript = Join-Path $appDir 'playwright.ps1'
if (Test-Path $playwrightScript) {
    & $playwrightScript install chromium
    if ($LASTEXITCODE -ne 0) { throw "L'installation de Chromium a echoue (code $LASTEXITCODE)." }
} else {
    Write-Warning "playwright.ps1 absent du paquet. Installez Chromium manuellement :"
    Write-Warning "  `$env:PLAYWRIGHT_BROWSERS_PATH='$browsersDir'; pwsh $appDir\playwright.ps1 install chromium"
}

Write-Host '[4/6] Creation du service Windows ...' -ForegroundColor Cyan
New-Service -Name $ServiceName `
            -BinaryPathName "`"$exePath`"" `
            -DisplayName $DisplayName `
            -Description 'Scraping Flashscore vers la base Juniro_Evo (Hangfire).' `
            -StartupType Automatic | Out-Null

# Environnement propre au service : evite de polluer toute la machine.
$regPath = "HKLM:\SYSTEM\CurrentControlSet\Services\$ServiceName"
Set-ItemProperty -Path $regPath -Name 'Environment' -Type MultiString -Value @(
    'ASPNETCORE_ENVIRONMENT=Production',
    "PLAYWRIGHT_BROWSERS_PATH=$browsersDir"
)

# Redemarrage automatique en cas de crash (60 s, trois tentatives, compteur remis a zero chaque jour).
& sc.exe failure $ServiceName reset= 86400 actions= restart/60000/restart/60000/restart/60000 | Out-Null

Write-Host '[5/6] Regle de pare-feu pour le dashboard Hangfire ...' -ForegroundColor Cyan
$ruleName = "Juniro Hangfire Dashboard ($Port)"
if (-not (Get-NetFirewallRule -DisplayName $ruleName -ErrorAction SilentlyContinue)) {
    New-NetFirewallRule -DisplayName $ruleName `
                        -Direction Inbound -Action Allow -Protocol TCP `
                        -LocalPort $Port -Profile Private | Out-Null
}

Write-Host '[6/6] Verification de la configuration ...' -ForegroundColor Cyan
if (-not (Test-Path $prodSettings)) {
    Write-Host ''
    Write-Warning 'appsettings.Production.json est absent : le service NE PEUT PAS demarrer.'
    Write-Host  "Creez $prodSettings a partir de appsettings.Production.template.json," -ForegroundColor Yellow
    Write-Host  'renseignez la chaine de connexion, puis lancez :' -ForegroundColor Yellow
    Write-Host  "  Start-Service $ServiceName" -ForegroundColor Yellow
} else {
    Start-Service -Name $ServiceName
    Start-Sleep -Seconds 5
    $svc = Get-Service -Name $ServiceName
    Write-Host ''
    Write-Host "Service $ServiceName : $($svc.Status)" -ForegroundColor Green
    Write-Host "Dashboard : http://localhost:$Port/hangfire"
}
