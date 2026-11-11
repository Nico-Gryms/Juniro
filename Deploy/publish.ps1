#Requires -Version 5.1
<#
    publish.ps1 - A LANCER SUR LE PC DE DEV
    Compile l'application en mode autonome (self-contained) et produit une archive .zip
    prete a etre copiee sur le PC fixe.
    ETAPES : 1) dotnet publish en win-x64 autonome (aucun .NET a installer sur le PC fixe)
             2) compression du dossier publie en JuniroScraper-AAAAMMJJ-HHMM.zip
                dans %USERPROFILE%\Juniro.Releases
    ENSUITE : copier le .zip sur le PC fixe et lancer update.ps1 (ou install-service.ps1 la 1re fois).
    Voir DOCUMENTATION.md, chapitre 9.
#>
[CmdletBinding()]
param(
    [string]$Configuration = 'Release',
    [string]$OutputRoot    = (Join-Path $env:USERPROFILE 'Juniro.Releases')
)

$ErrorActionPreference = 'Stop'

$projectPath = (Resolve-Path (Join-Path $PSScriptRoot '..\Juniro.ScraperService.csproj')).Path
$stamp       = Get-Date -Format 'yyyyMMdd-HHmm'
$publishDir  = Join-Path $OutputRoot "publish-$stamp"
$zipPath     = Join-Path $OutputRoot "JuniroScraper-$stamp.zip"

New-Item -ItemType Directory -Force -Path $OutputRoot | Out-Null

Write-Host "Publication de $projectPath ..." -ForegroundColor Cyan
dotnet publish $projectPath `
    -c $Configuration `
    -r win-x64 `
    --self-contained true `
    -o $publishDir

if ($LASTEXITCODE -ne 0) { throw "dotnet publish a echoue (code $LASTEXITCODE)." }

# La config de dev contient des identifiants : elle ne doit jamais partir en production.
Remove-Item (Join-Path $publishDir 'appsettings.Development.json') -Force -ErrorAction SilentlyContinue

# Les logs du poste de dev n'ont rien a faire dans le paquet.
Remove-Item (Join-Path $publishDir 'logs') -Recurse -Force -ErrorAction SilentlyContinue

Write-Host "Creation de l'archive ..." -ForegroundColor Cyan
if (Test-Path $zipPath) { Remove-Item $zipPath -Force }
Compress-Archive -Path (Join-Path $publishDir '*') -DestinationPath $zipPath

# On ne garde que les 5 dernieres publications locales.
Get-ChildItem $OutputRoot -Directory -Filter 'publish-*' |
    Sort-Object Name -Descending | Select-Object -Skip 5 |
    Remove-Item -Recurse -Force -ErrorAction SilentlyContinue

$sizeMb = [math]::Round((Get-Item $zipPath).Length / 1MB, 1)

Write-Host ''
Write-Host "Archive prete : $zipPath ($sizeMb Mo)" -ForegroundColor Green
Write-Host 'Copiez-la sur le PC fixe dans C:\Juniro\releases, puis lancez Deploy\update.ps1 la-bas.'
