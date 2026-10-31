# Builds the Velopack installer locally (the same steps as .github/workflows/release.yml, without uploading).
#
#   .\build-installer.ps1 0.1.0
#
# Output in artifacts\releases:
#   FriesDemoDeck-win-Setup.exe     the installer to hand out
#   FriesDemoDeck-win-Portable.zip  runs without installing
#   *.nupkg, releases.win.json      update packages (what the app downloads when it updates)

param(
    [Parameter(Mandatory)][string]$Version
)

$ErrorActionPreference = 'Stop'
Set-Location $PSScriptRoot

$publish = 'artifacts\publish'
$releases = 'artifacts\releases'
if (Test-Path $publish) { Remove-Item $publish -Recurse -Force }

dotnet tool restore
if ($LASTEXITCODE) { throw 'dotnet tool restore failed' }

dotnet publish src\Ex.App -c Release -r win-x64 --self-contained true -p:Version=$Version -o $publish
if ($LASTEXITCODE) { throw 'dotnet publish failed' }

dotnet vpk pack `
    --packId FriesDemoDeck `
    --packVersion $Version `
    --packDir $publish `
    --mainExe Ex.App.exe `
    --packTitle "Fries' Demo Deck" `
    --packAuthors FriesBoury `
    --icon src\Ex.App\Assets\app.ico `
    --outputDir $releases
if ($LASTEXITCODE) { throw 'vpk pack failed' }

Write-Host "`nInstaller: $(Resolve-Path "$releases\FriesDemoDeck-win-Setup.exe")"
