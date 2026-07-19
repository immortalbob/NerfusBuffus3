#requires -Version 5.1
<#
  Nerfus Buffus III (revival) - Windows build helper.
  Builds the net48 / x86 Decal plugin and stages the deployable DLLs into .\dist.
  Run from a NORMAL PowerShell prompt (the build needs no admin).
  Then register with register-windows.ps1 from an ADMIN prompt.

  Prereqs (see docs/BUILD_ON_WINDOWS.md):
    - .NET SDK 8/9/10           https://dotnet.microsoft.com/download
    - .NET Framework 4.8 Developer Pack
    - Decal 3 + VirindiViewService installed

  The raw command this wraps (authoritative copy is in docs/BUILD_ON_WINDOWS.md):
    dotnet build src\NB3.Plugin\NerfusBuffus3.csproj -c Release /p:Platform=x86 `
      -p:CoreTfm=net48 /p:DecalSdk="<decal>" /p:VvsSdk="<vvs>"
#>
[CmdletBinding()]
param(
  [string]$DecalSdk      = "C:\Games\Decal 3.0",
  [string]$VvsSdk        = "C:\Games\VirindiPlugins\VirindiViewService",
  [string]$Configuration = "Release"
)

$ErrorActionPreference = "Stop"
$root = $PSScriptRoot
$proj = Join-Path $root "src\NB3.Plugin\NerfusBuffus3.csproj"

Write-Host "== Nerfus Buffus III build ==" -ForegroundColor Cyan
Write-Host "Project : $proj"
Write-Host "DecalSdk: $DecalSdk"
Write-Host "VvsSdk  : $VvsSdk"

# Verify the reference assemblies exist before building, so a wrong -DecalSdk/-VvsSdk fails
# with a clear message instead of a wall of CS0246 "type not found" errors (doc 12 sec.2).
$refs = @(
  (Join-Path $DecalSdk "Decal.Adapter.dll"),
  (Join-Path $DecalSdk "Decal.FileService.dll"),
  (Join-Path $VvsSdk   "VirindiViewService.dll")
)
foreach ($r in $refs) {
  if (-not (Test-Path $r)) {
    Write-Warning "Reference assembly not found: $r"
    Write-Warning "Pass -DecalSdk / -VvsSdk pointing at the folders that hold these DLLs."
    Write-Warning "  (VirindiViewService lives in its OWN plugin folder, NOT the Decal folder.)"
  }
}

# -p:CoreTfm=net48 builds NB3.Core as net48 (its real runtime target - Decal loads it
# in-process into the net48 client). The core's default TFM is netstandard2.0, which needs
# the NETStandard.Library metapackage from a NuGet feed; nuget.config clears all sources for
# the offline gates, so an ns2.0 restore fails with NU1100 even on a connected box. net48
# resolves from the on-disk .NET Framework 4.8 Developer Pack - no feed. See docs/BUILD_ON_WINDOWS.md.
& dotnet build $proj -c $Configuration /p:Platform=x86 -p:CoreTfm=net48 "/p:DecalSdk=$DecalSdk" "/p:VvsSdk=$VvsSdk"
if ($LASTEXITCODE -ne 0) { throw "dotnet build failed (exit $LASTEXITCODE)." }

# Stage the deployable set. Locate the freshly built plugin DLL wherever MSBuild put it
# (bin\x86\Release with /p:Platform=x86), then copy it plus the core it loads (doc 12 sec.5).
$binRoot = Join-Path $root "src\NB3.Plugin\bin"
$built = Get-ChildItem -Path $binRoot -Recurse -Filter "NerfusBuffus3.dll" -ErrorAction SilentlyContinue |
         Sort-Object LastWriteTime -Descending | Select-Object -First 1
if (-not $built) { throw "Build succeeded but NerfusBuffus3.dll was not found under $binRoot." }
$outDir = $built.Directory.FullName

$dist = Join-Path $root "dist"
New-Item -ItemType Directory -Force -Path $dist | Out-Null
foreach ($name in @("NerfusBuffus3.dll","NB3.Core.dll","NerfusBuffus3.pdb","NB3.Core.pdb")) {
  $src = Join-Path $outDir $name
  if (Test-Path $src) { Copy-Item -Path $src -Destination $dist -Force }
}

Write-Host ""
Write-Host "Build OK. Staged to: $dist" -ForegroundColor Green
Get-ChildItem $dist | Format-Table Name, Length, LastWriteTime
Write-Host "Next: run  .\register-windows.ps1  from an ADMIN PowerShell (COM-register + tell Decal)." -ForegroundColor Yellow
