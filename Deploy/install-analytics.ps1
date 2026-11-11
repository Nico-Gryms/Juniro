#Requires -Version 5.1
<#
    install-analytics.ps1 - A LANCER SUR LE PC FIXE (et a relancer a chaque mise a jour du moteur)

    Copie le moteur Python (dossier Analytics du projet) dans C:\Juniro\analytics,
    cree son environnement Python et installe les bibliotheques.
    Ne touche ni a l'environnement deja cree, ni aux modeles entraines (dossier models).

    Prerequis, une seule fois sur le PC :
      - Python 3.13 (ou 3.11 / 3.12) : https://www.python.org/downloads/  (cocher "Add python.exe to PATH")
        Les versions plus recentes ne sont pas encore prises en charge par toutes les bibliotheques.
      - Pilote "ODBC Driver 18 for SQL Server" (souvent deja present avec SSMS)

    Exemple :
      .\install-analytics.ps1 -SourceDir "C:\Users\grymy\Juniro.ScraperService\Analytics"
    ETAPES : [1] choix d'un Python 3.11 a 3.13  [2] pilote ODBC  [3] copie du moteur
             (config.json mis a jour, ancien sauvegarde ; local.json et tuned.json conserves)
             [4] environnement .venv + bibliotheques  [5] note l'emplacement de la chaine de
             connexion dans local.json puis lance "check"
    Voir DOCUMENTATION.md, chapitre 5.1.
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)][string]$SourceDir,
    [string]$InstallRoot = 'C:\Juniro'
)

$ErrorActionPreference = 'Stop'
$target = Join-Path $InstallRoot 'analytics'

if (-not (Test-Path (Join-Path $SourceDir 'juniro_analytics\__main__.py'))) {
    throw "Dossier source invalide : $SourceDir (juniro_analytics\__main__.py introuvable)."
}

Write-Host '[1/5] Verification de Python ...' -ForegroundColor Cyan
# Versions pour lesquelles toutes les bibliotheques (pyodbc, scikit-learn, scipy) existent deja
# compilees pour Windows. Une version plus recente (ex. 3.15) oblige pip a les compiler et
# echoue sans les outils C++ de Visual Studio.
$supported = @('3.13', '3.12', '3.11')

# Lance un executable et renvoie sa sortie, ou $null s'il echoue. Sous Windows PowerShell 5.1,
# un message sur la sortie d'erreur ferait sinon planter le script (ErrorActionPreference = Stop).
function Get-PyVersion([string]$exe, [string[]]$exeArgs = @()) {
    $old = $ErrorActionPreference
    $ErrorActionPreference = 'Continue'
    try {
        $out = & $exe @exeArgs -c "import sys; print('%d.%d' % sys.version_info[:2])" 2>$null
        if ($LASTEXITCODE -eq 0) { return "$out".Trim() }
        return $null
    } catch { return $null } finally { $ErrorActionPreference = $old }
}
$pyCmd = $null; $pyArgs = @(); $version = $null
if (Get-Command py -ErrorAction SilentlyContinue) {
    foreach ($v in $supported) {
        $out = Get-PyVersion 'py' @("-$v")
        if ($out -eq $v) { $pyCmd = 'py'; $pyArgs = @("-$v"); $version = $v; break }
    }
}
if (-not $pyCmd -and (Get-Command python -ErrorAction SilentlyContinue)) {
    $out = Get-PyVersion 'python'
    if ($supported -contains $out) { $pyCmd = 'python'; $version = $out }
}
if (-not $pyCmd) {
    $installed = try { (& py -0 2>$null) -join ', ' } catch { 'inconnu' }
    throw ("Aucun Python 3.11 a 3.13 trouve (installes : $installed). Installez Python 3.13 depuis " +
           "python.org (il cohabite avec les autres versions), puis relancez ce script.")
}
Write-Host "  Python $version retenu"

Write-Host '[2/5] Verification du pilote ODBC ...' -ForegroundColor Cyan
$odbc = Get-OdbcDriver -ErrorAction SilentlyContinue | Where-Object { $_.Name -like 'ODBC Driver 1[78] for SQL Server' }
if (-not $odbc) {
    Write-Warning "Aucun 'ODBC Driver 17/18 for SQL Server'. Installez-le (recherche : 'Download ODBC Driver for SQL Server')."
} else {
    $odbc | ForEach-Object { Write-Host "  $($_.Name) ($($_.Platform))" }
    if (-not ($odbc | Where-Object Name -eq 'ODBC Driver 18 for SQL Server')) {
        Write-Host "  Pilote 18 absent : le moteur utilisera automatiquement le pilote 17."
    }
}

Write-Host "[3/5] Copie du moteur vers $target ..." -ForegroundColor Cyan
New-Item -ItemType Directory -Force -Path $target | Out-Null
$configTarget = Join-Path $target 'config.json'
$configSource = Join-Path $SourceDir 'config.json'
if ((Test-Path $configTarget) -and
    ((Get-FileHash $configTarget).Hash -ne (Get-FileHash $configSource).Hash)) {
    $backup = Join-Path $target "config.backup-$(Get-Date -Format 'yyyyMMdd-HHmm').json"
    Copy-Item $configTarget $backup
    Write-Host "  Ancien config.json sauvegarde : $backup (reportez vos eventuelles modifications dans le nouveau)."
}
robocopy $SourceDir $target /E /XD .venv models __pycache__ .pytest_cache /XF local.json tuned.json /NFL /NDL /NJH /NJS | Out-Null
if ($LASTEXITCODE -ge 8) { throw "Echec de la copie (robocopy code $LASTEXITCODE)." }
# Les reglages propres a ce PC ne sont pas dans config.json : local.json (connexion) et tuned.json (modeles).

Write-Host '[4/5] Environnement Python et bibliotheques ...' -ForegroundColor Cyan
$venvDir = Join-Path $target '.venv'
$venvPy = Join-Path $venvDir 'Scripts\python.exe'
if (Test-Path $venvPy) {
    $venvVersion = Get-PyVersion $venvPy
    if ($venvVersion -ne $version) {
        Write-Host "  Environnement existant ($(if ($venvVersion) { "Python $venvVersion" } else { 'inutilisable' })) : recreation en Python $version"
        Remove-Item $venvDir -Recurse -Force
    }
}
if (-not (Test-Path $venvPy)) {
    & $pyCmd @pyArgs -m venv (Join-Path $target '.venv')
    if ($LASTEXITCODE -ne 0) { throw 'Creation de l''environnement virtuel impossible.' }
}
& $venvPy -m pip install --upgrade pip --quiet
& $venvPy -m pip install -r (Join-Path $target 'requirements.txt') --only-binary=:all: --quiet
if ($LASTEXITCODE -ne 0) { throw 'Installation des bibliotheques en echec.' }

# Ou trouver la chaine de connexion sur CE PC : le service installe (production) en priorite,
# sinon le projet de dev. Seul le chemin est note dans local.json, jamais le mot de passe.
# @(...) autour du filtre : avec un seul fichier trouve, PowerShell renverrait une simple chaine
# et $appsettings[0] serait sa premiere lettre ("C").
$appsettings = @(@(
    (Join-Path $InstallRoot 'app\appsettings.Production.json'),
    (Join-Path (Split-Path $SourceDir -Parent) 'appsettings.Production.json'),
    (Join-Path (Split-Path $SourceDir -Parent) 'appsettings.Development.json')
) | Where-Object { Test-Path $_ })
if ($appsettings.Count -gt 0) {
    $local = @{ appsettings_files = @($appsettings) } | ConvertTo-Json
    [IO.File]::WriteAllText((Join-Path $target 'local.json'), $local, (New-Object Text.UTF8Encoding $false))
    Write-Host "  Chaine de connexion lue dans : $($appsettings[0])"
} else {
    Write-Warning "Aucun appsettings avec la chaine de connexion trouve ($InstallRoot\app ou le projet)."
}

Write-Host '[5/5] Controle de la connexion a la base ...' -ForegroundColor Cyan
Push-Location $target
try {
    & $venvPy -X utf8 -m juniro_analytics check
    if ($LASTEXITCODE -ne 0) {
        Write-Warning 'Le controle a echoue : voir le message ci-dessus (scripts SQL A1/A2 executes ? chaine de connexion ?).'
    }
} finally { Pop-Location }

Write-Host ''
Write-Host "Moteur installe dans $target" -ForegroundColor Green
Write-Host 'Premier calcul complet (une fois, puis a chaque changement de modele) :'
Write-Host "  cd $target"
Write-Host '  .\.venv\Scripts\python.exe -X utf8 -m juniro_analytics backtest --label "premier essai"'
Write-Host 'Puis, pour l''automatiser chaque jour : "Analytics": { "Enabled": true } dans appsettings.Production.json et redemarrer le service.'
