<#
.SYNOPSIS
  Builds a NepPOS register release, makes the installer, and uploads it so every installed register updates itself.

.EXAMPLE
  .\deploy\register\publish-release.ps1 -Version 1.0.1 -Server root@203.0.113.10

  Run from the NepPOS folder in PowerShell. Use a higher -Version every time (1.0.0, 1.0.1, 1.1.0 ...).
#>
param(
    [Parameter(Mandatory = $true)][string] $Version,
    [Parameter(Mandatory = $true)][string] $Server,
    [string] $UpdateUrl = "https://updates.neppos.com.au/neppos"
)

$ErrorActionPreference = "Stop"
$VelopackVersion = "1.2.161"   # keep the same as the Velopack package in NepPOS.Presentation.Register.csproj

Write-Host "== vpk tool $VelopackVersion"
dotnet tool update -g vpk --version $VelopackVersion
if ($LASTEXITCODE -ne 0) { throw "Couldn't install the vpk tool." }

Write-Host "== Build $Version (self-contained, so shop PCs need nothing else installed)"
Remove-Item -Recurse -Force publish\register -ErrorAction SilentlyContinue
dotnet publish src\NepPOS.Presentation.Register\NepPOS.Presentation.Register.csproj `
    -c Release -f net9.0-windows10.0.19041.0 -r win-x64 --self-contained true `
    -p:WindowsAppSDKSelfContained=true `
    -p:NepPosUpdateUrl=$UpdateUrl `
    -p:ApplicationDisplayVersion=$Version `
    -o publish\register
if ($LASTEXITCODE -ne 0) { throw "Build failed." }

Write-Host "== Installer and update package"
vpk pack --packId NepPOS.Register --packVersion $Version --packDir publish\register --mainExe NepPOS.Presentation.Register.exe --outputDir releases
if ($LASTEXITCODE -ne 0) { throw "vpk pack failed." }

Write-Host "== Upload to $UpdateUrl"
ssh $Server "mkdir -p /var/www/neppos-updates/neppos"
Get-ChildItem releases -File | ForEach-Object {
    scp $_.FullName "${Server}:/var/www/neppos-updates/neppos/"
    if ($LASTEXITCODE -ne 0) { throw "Upload of $($_.Name) failed." }
}

Write-Host ""
Write-Host "Done. Registers download $Version within 4 hours and install it the next time NepPOS starts."
Write-Host "New shop PC: download and run $UpdateUrl/NepPOS.Register-win-Setup.exe"
