<#
.SYNOPSIS
  Configure ANCM shadow copy and publish AuthBaseXiHan for lock-free IIS updates.

.DESCRIPTION
  MUST be run as Administrator (IIS query + directory ACL need elevation).

  What it does:
    1. Create a shadow copy folder OUTSIDE the site folder (so publish/WebDeploy
       never syncs or deletes the running assemblies).
    2. Grant the IIS application pool identity Modify (read/write/delete) on it.
    3. dotnet publish to the site folder. web.config in the project root already
       carries <handlerSettings> with enableShadowCopy / shadowCopyDirectory /
       cleanShadowCopyDirectory, and the Web SDK merges (not overwrites) it during publish.
    4. Verify the generated web.config contains all three handlerSetting names.

  After this, ANCM runs the app from the shadow folder. The DLLs in the site folder
  are no longer locked, so you can publish again at any time without stopping IIS.
  Note: ANCM still recycles the app on change; a brief 503 is possible. For true
  zero-downtime use blue/green deployment.

.PARAMETER AppPoolName
  IIS application pool name. If omitted the script tries to detect it from the site
  whose physical path matches PublishDir, then falls back to "AuthBaseXiHan".
.PARAMETER PublishDir
  Target site folder. Default: D:\个人发布项目\AuthBaseXiHan
.PARAMETER ShadowDir
  Shadow copy folder. Default: <parent of PublishDir>\AuthBaseXiHan_ShadowCopy
.PARAMETER SkipPublish
  Only prepare the directory and ACL, do not run dotnet publish.
.PARAMETER Recycle
  Recycle the app pool after publish. Usually unnecessary because ANCM detects
  file changes and restarts the app on its own.
#>
[CmdletBinding()]
param(
  [string]$AppPoolName,
  [string]$PublishDir = "D:\个人发布项目\AuthBaseXiHan",
  [string]$ShadowDir,
  [switch]$SkipPublish,
  [switch]$Recycle
)

$ErrorActionPreference = "Stop"

function Test-IsAdministrator {
  $identity = [Security.Principal.WindowsIdentity]::GetCurrent()
  $principal = New-Object Security.Principal.WindowsPrincipal($identity)
  return $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
}

function Get-AppPoolForPath([string]$sitePath) {
  # Preferred: WebAdministration module (IIS management scripts installed)
  try {
    Import-Module WebAdministration -ErrorAction Stop
    $site = Get-Website | Where-Object { $_.PhysicalPath.TrimEnd('\') -eq $sitePath.TrimEnd('\') } | Select-Object -First 1
    if ($site) { return $site.applicationPool }
  } catch {
    # fall through to appcmd
  }

  # Fallback: appcmd
  $appcmd = Join-Path $env:WINDIR "System32\inetsrv\appcmd.exe"
  if (Test-Path $appcmd) {
    $lines = & $appcmd list app 2>$null
    foreach ($line in $lines) {
      if ($line -match 'APP\s+"([^"]+)"\s+\(applicationPool:([^)]+)\)') {
        $candidateApp = $Matches[1]
        $candidatePool = $Matches[2]
        $physical = & $appcmd list app "$candidateApp" /text:physicalPath 2>$null
        if ($physical -and $physical.TrimEnd('\') -eq $sitePath.TrimEnd('\')) {
          return $candidatePool
        }
      }
    }
  }
  return $null
}

# ---------- preflight ----------
if (-not (Test-IsAdministrator)) {
  Write-Error "This script must be run as Administrator. Right-click PowerShell -> Run as Administrator, then re-run."
}

$projectDir = Split-Path -Parent $MyInvocation.MyCommand.Definition
if (-not $ShadowDir) {
  $ShadowDir = Join-Path (Split-Path -Parent $PublishDir) "AuthBaseXiHan_ShadowCopy"
}

Write-Host "ProjectDir : $projectDir"
Write-Host "PublishDir : $PublishDir"
Write-Host "ShadowDir  : $ShadowDir"
Write-Host ""

# ---------- 1. shadow directory ----------
if (-not (Test-Path $ShadowDir)) {
  New-Item -ItemType Directory -Path $ShadowDir -Force | Out-Null
  Write-Host "[1/4] Created shadow directory."
} else {
  Write-Host "[1/4] Shadow directory already exists."
}

# ---------- 2. app pool identity + ACL ----------
if (-not $AppPoolName) {
  $AppPoolName = Get-AppPoolForPath $PublishDir
}
if (-not $AppPoolName) {
  $AppPoolName = "DefaultAppPool"
  Write-Warning "Could not detect app pool from IIS. Using default '$AppPoolName'. Re-run with -AppPoolName <name> if your pool differs."
}
Write-Host "[2/4] App pool: $AppPoolName"

# (OI)(CI)M = Object/Container Inherit + Modify (read, write, delete)
$ace = "IIS AppPool\${AppPoolName}:(OI)(CI)M"
& icacls $ShadowDir /grant $ace /T | Out-Null
if ($LASTEXITCODE -ne 0) {
  Write-Error "icacls failed for '$ace'. Verify the app pool name is correct."
}
Write-Host "       Granted Modify on shadow directory to IIS AppPool\$AppPoolName"

# ---------- 3. publish ----------
if (-not $SkipPublish) {
  $project = Join-Path $projectDir "AuthBaseXiHan.csproj"
  if (-not (Test-Path $project)) { Write-Error "Project not found: $project" }
  Write-Host "[3/4] dotnet publish -> $PublishDir"
  & dotnet publish $project -c Release -o $PublishDir --nologo
  if ($LASTEXITCODE -ne 0) { Write-Error "dotnet publish failed." }
} else {
  Write-Host "[3/4] SkipPublish set, skipping dotnet publish."
}

# ---------- 4. verify web.config ----------
$webConfig = Join-Path $PublishDir "web.config"
if (-not (Test-Path $webConfig)) { Write-Error "web.config not found: $webConfig" }
$xml = [xml](Get-Content $webConfig -Raw)
$node = $xml.configuration.location.'system.webServer'.aspNetCore
if ($null -eq $node) { Write-Error "No <aspNetCore> node in web.config." }
# Shadow copy is configured as <handlerSettings><handlerSetting name=... value=... />,
# NOT as attributes on <aspNetCore>. Verify by name.
$settings = @{}
foreach ($s in $node.handlerSettings.handlerSetting) { $settings[$s.name] = $s.value }
foreach ($name in @("enableShadowCopy", "shadowCopyDirectory", "cleanShadowCopyDirectory")) {
  if (-not $settings.ContainsKey($name)) { Write-Error "web.config is missing handlerSetting '$name'." }
}
Write-Host "[4/4] web.config verified: shadow copy enabled via handlerSettings."

# ---------- optional recycle ----------
if ($Recycle) {
  $appcmd = Join-Path $env:WINDIR "System32\inetsrv\appcmd.exe"
  if (Test-Path $appcmd) {
    Write-Host "Recycling app pool '$AppPoolName' ..."
    & $appcmd recycle apppool /apppool.name:$AppPoolName
  }
}

Write-Host ""
Write-Host "Done. On the next request/recycle ANCM copies assemblies into:"
Write-Host "  $ShadowDir"
Write-Host "The original DLLs in $PublishDir are no longer locked and can be overwritten anytime."
