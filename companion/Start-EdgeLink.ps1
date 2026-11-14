<#
.SYNOPSIS
    EdgeLink companion: reads the EdgeLink addon's pixel strip from the WoW window
    and serves the data to Xeneon Edge widgets over http://localhost.

.DESCRIPTION
    Read-only. Captures a 640x4 pixel area at the top-left of the WoW client area
    (screen capture only; no process memory access, no input sent to the game).

    Endpoints:
      http://localhost:<Port>/        Widget UI (from ..\widget)
      http://localhost:<Port>/state   JSON snapshot + bridge stats

.PARAMETER Port
    Local HTTP port. Default 8787.

.PARAMETER ProcessName
    WoW process name(s) without .exe. Default 'Wow'.

.PARAMETER IntervalMs
    Delay between captures. Default 33 (about 30 reads/sec).

.PARAMETER TestImage
    Decode strip PNG files instead of the screen, print the JSON and exit.

.PARAMETER ServeTest
    With -TestImage: load the decoded test snapshot, then keep serving it (UI preview without WoW).

.EXAMPLE
    .\Start-EdgeLink.ps1
.EXAMPLE
    .\Start-EdgeLink.ps1 -TestImage ..\tests\out\page0.png, ..\tests\out\page1.png
#>
[CmdletBinding()]
param(
    [int]$Port = 8787,
    [string[]]$ProcessName = @('Wow'),
    [int]$IntervalMs = 33,
    [string[]]$TestImage,
    [switch]$ServeTest,
    [switch]$NoIcons,
    [switch]$Quiet
)

$ErrorActionPreference = 'Stop'
$root = $PSScriptRoot
$widgetDir = Join-Path (Split-Path $root -Parent) 'widget'

if (-not ('EdgeLink.Bridge' -as [type])) {
    $source = Get-Content -Raw -Path (Join-Path $root 'EdgeLinkCore.cs')
    $refs = @('System.Drawing')
    if ($PSVersionTable.PSEdition -eq 'Core') { $refs = @('System.Drawing.Common', 'System.Drawing.Primitives', 'System.Net.HttpListener', 'System.Diagnostics.Process', 'System.Collections', 'System.Threading.Thread', 'System.ComponentModel.Primitives', 'System.Runtime.InteropServices', 'System.Console') }
    Add-Type -TypeDefinition $source -ReferencedAssemblies $refs -Language CSharp
}

$bridge = New-Object EdgeLink.Bridge
$bridge.ProcessNames = $ProcessName
$bridge.Port = $Port
$bridge.IntervalMs = $IntervalMs
$bridge.WidgetDir = $widgetDir
if (-not $NoIcons) {
    $bridge.Icons = New-Object EdgeLink.IconCache (Join-Path $root 'cache\icons')
    $bridge.Maps = New-Object EdgeLink.MapCache (Join-Path $root 'cache\maps')
}
$bridge.Verbose = -not $Quiet

# HTTPS (same port) when Install-EdgeLinkCert.ps1 has been run.
$cert = Get-ChildItem Cert:\CurrentUser\My |
    Where-Object { $_.FriendlyName -eq 'EdgeLink localhost' -and $_.HasPrivateKey -and $_.NotAfter -gt (Get-Date) } |
    Sort-Object NotAfter -Descending | Select-Object -First 1
if ($cert) {
    $bridge.Certificate = $cert
    if ($cert.NotAfter -lt (Get-Date).AddDays(30)) {
        Write-Warning "EdgeLink HTTPS certificate expires $($cert.NotAfter.ToString('yyyy-MM-dd')). Re-run Install-EdgeLinkCert.ps1."
    }
}

if ($TestImage) {
    # -File passes "a.png,b.png" as one string; accept either form.
    $files = $TestImage | ForEach-Object { $_ -split ',' } | Where-Object { $_ } | ForEach-Object { (Resolve-Path $_.Trim()).Path }
    $json = $bridge.DecodeImages([string[]]$files)
    if (-not $ServeTest) { $json; return }
    Write-Host "Loaded test snapshot from $($files.Count) image(s); serving it." -ForegroundColor Yellow
}

try {
    $bridge.Start()
} catch {
    Write-Error "Could not start the local web server on port $Port. Is another copy running? $($_.Exception.Message)"
    return
}

Write-Host "EdgeLink companion running." -ForegroundColor Cyan
Write-Host "  Widget : http://localhost:$Port/"
Write-Host "  State  : http://localhost:$Port/state"
if ($bridge.HttpsEnabled) { Write-Host "  HTTPS  : https://localhost:$Port/ (certificate $($cert.Thumbprint.Substring(0,8))...)" }
else { Write-Host "  HTTPS  : off (run companion\Install-EdgeLinkCert.ps1 if iCUE shows ERR_SSL_PROTOCOL_ERROR)" -ForegroundColor DarkYellow }
Write-Host "  Watching process: $($ProcessName -join ', ')   (Ctrl+C to stop)"
try {
    while ($true) { Start-Sleep -Seconds 1 }
} finally {
    $bridge.Stop()
    Write-Host "EdgeLink companion stopped."
}
