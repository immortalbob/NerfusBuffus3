#requires -Version 5.1
<#
  Nerfus Buffus III (revival) - COM registration + Decal plugin key.
  MUST be run from an ADMINISTRATOR PowerShell.

  Registers .\dist\NerfusBuffus3.dll with the 32-bit RegAsm (/codebase) and writes the Decal
  plugin registration under WOW6432Node. Re-run ONLY when the DLL path or the CLSID changes -
  not for an ordinary rebuild in place (doc 12 sec.4/sec.6).
#>
[CmdletBinding()]
param(
  [string]$Dist = (Join-Path $PSScriptRoot "dist")
)
$ErrorActionPreference = "Stop"

# Require elevation - RegAsm and HKLM writes both need it.
$id = [Security.Principal.WindowsIdentity]::GetCurrent()
$principal = New-Object Security.Principal.WindowsPrincipal($id)
if (-not $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
  throw "Run this from an ADMINISTRATOR PowerShell (right-click PowerShell > Run as administrator)."
}

$dll = Join-Path $Dist "NerfusBuffus3.dll"
if (-not (Test-Path $dll)) { throw "Not found: $dll  - run .\build-windows.ps1 first." }

# 32-bit RegAsm ONLY - Framework\, never Framework64\ (doc 12 sec.4). The AC client is 32-bit
# and reads COM registration from WOW6432Node; the 64-bit RegAsm writes where it can't see it,
# and Decal then silently never finds the plugin.
$regasm = Join-Path $env:WINDIR "Microsoft.NET\Framework\v4.0.30319\RegAsm.exe"
if (-not (Test-Path $regasm)) { throw "32-bit RegAsm not found at $regasm (install a .NET Framework 4.x runtime)." }

Write-Host "== COM-registering (32-bit RegAsm /codebase) ==" -ForegroundColor Cyan
& $regasm /codebase $dll
if ($LASTEXITCODE -ne 0) { throw "RegAsm failed (exit $LASTEXITCODE)." }

# Decal plugin registration. GUID matches [Guid] on NB3.Plugin.PluginCore (do not change one
# without the other - doc 12 sec.4.1).
$guid = "{915ED3D0-26CD-493D-80E8-34A3099FF511}"
$key  = "HKLM:\SOFTWARE\WOW6432Node\Decal\Plugins\$guid"
Write-Host "== Writing Decal plugin key ==" -ForegroundColor Cyan
Write-Host "   $key"
New-Item -Path $key -Force | Out-Null
Set-Item        -Path $key -Value "Nerfus Buffus III"                                             # (default) = friendly name
New-ItemProperty -Path $key -Name "Object"    -Value "NB3.Plugin.PluginCore"                    -PropertyType String -Force | Out-Null
New-ItemProperty -Path $key -Name "Assembly"  -Value $dll                                       -PropertyType String -Force | Out-Null
New-ItemProperty -Path $key -Name "Path"      -Value $Dist                                      -PropertyType String -Force | Out-Null
New-ItemProperty -Path $key -Name "Surrogate" -Value "{71A69713-6593-47EC-0002-0000000DECA1}"   -PropertyType String -Force | Out-Null
New-ItemProperty -Path $key -Name "Enabled"   -Value 1                                          -PropertyType DWord  -Force | Out-Null

Write-Host ""
Write-Host "Registered." -ForegroundColor Green
Write-Host "Launch AC through Decal, open Manage Plugins, enable 'Nerfus Buffus III', log in." -ForegroundColor Green
Write-Host "If it doesn't appear: confirm which hive VVS's own plugin key uses and match it (doc 09 sec.4.2)." -ForegroundColor Yellow
