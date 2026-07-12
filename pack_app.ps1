param(
    [Parameter(Mandatory=$true)]
    [string]$Version
)

$ErrorActionPreference = "Stop"

# 1. Ensure Velopack CLI tool is installed
Write-Host "Checking for Velopack CLI..." -ForegroundColor Cyan
try {
    dotnet tool install -g vpk
} catch {
    Write-Host "Velopack CLI is already installed or up-to-date." -ForegroundColor Green
}

# 2. Clean previous build artifacts
Write-Host "Cleaning old build directories..." -ForegroundColor Cyan
if (Test-Path "publish") { Remove-Item -Recurse -Force "publish" }
if (Test-Path "Releases") { 
    Write-Host "Found existing Releases folder. NOTE: Velopack will append new versions. If you are re-packaging the same version, please delete or clear the 'Releases' directory first." -ForegroundColor Yellow 
}

# 3. Publish UI, Host & Uninstaller projects into the same 'publish' directory
Write-Host "Publishing PatreonArchiverBridge.UI (App)..." -ForegroundColor Cyan
dotnet publish PatreonArchiverBridge.UI -c Release -r win-x64 -o publish -p:PublishSingleFile=true --self-contained false --nologo

Write-Host "Publishing PatreonArchiverBridge.Host..." -ForegroundColor Cyan
dotnet publish PatreonArchiverBridge.Host -c Release -r win-x64 -o publish -p:PublishSingleFile=true --self-contained false --nologo

Write-Host "Publishing PatreonArchiverBridge.Uninstaller..." -ForegroundColor Cyan
dotnet publish PatreonArchiverBridge.Uninstaller -c Release -r win-x64 -o publish -p:PublishSingleFile=true --self-contained false --nologo

# 4. Run Velopack Pack on the combined directory
Write-Host "Packaging version $Version with Velopack..." -ForegroundColor Cyan
vpk pack --packId "PatreonArchiverBridge" --packVersion $Version --packDir "publish" --mainExe "PatreonArchiverBridge.exe"

# 5. Copy the Velopack Setup into the Custom Setup project as an embedded resource
Write-Host "Embedding Velopack installer into Custom Setup Wizard..." -ForegroundColor Cyan
$embeddedSetupDest = "PatreonArchiverBridge.Setup\velopack_setup.exe"
Copy-Item -Path "Releases\PatreonArchiverBridge-win-Setup.exe" -Destination $embeddedSetupDest -Force

# 6. Compile the Custom Setup Wizard (which embeds the Velopack setup)
Write-Host "Compiling Custom Setup Wizard..." -ForegroundColor Cyan
if (Test-Path "publish_setup") { Remove-Item -Recurse -Force "publish_setup" }
dotnet publish PatreonArchiverBridge.Setup -c Release -r win-x64 -o publish_setup -p:PublishSingleFile=true --self-contained false --nologo

# 7. Copy the final Custom Setup Wizard to 'publish' folder
Copy-Item -Path "publish_setup\PatreonArchiverBridge_setup.exe" -Destination "publish\PatreonArchiverBridge_setup.exe" -Force

# 8. Cleanup temp files
Remove-Item -Recurse -Force "publish_setup"
Remove-Item -Path $embeddedSetupDest -Force

Write-Host "Successfully packaged PatreonArchiverBridge version $Version." -ForegroundColor Green
Write-Host "The final custom setup wizard is available at: publish\PatreonArchiverBridge_setup.exe" -ForegroundColor Green
Write-Host "To upload the Velopack update assets to GitHub, run:" -ForegroundColor Yellow
Write-Host "vpk upload github --repoUrl YOUR_GITHUB_URL --publish --releaseName 'v$Version' --tag v$Version" -ForegroundColor Yellow
