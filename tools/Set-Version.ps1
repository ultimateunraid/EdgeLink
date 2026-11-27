<#
.SYNOPSIS
    Sets the EdgeLink version everywhere it appears, so a release tag, the companion, the widget and the addon agree.

.DESCRIPTION
    Updates:
      companion/AssemblyInfo.cs            AssemblyVersion / AssemblyFileVersion (x.y.z.0; the installer reads this)
      widget/manifest.json                 "version"
      widget/index.html                    WIDGET_VERSION
      addon/EdgeLink/EdgeLink.toc          ## Version
      addon/EdgeLink/EdgeLink_Forever.toc  ## Version
      CHANGELOG.md                         renames "## Unreleased" to "## <version>" (the release job needs that section)

    Stops without changing anything if a file is missing a version line, or CHANGELOG.md has neither
    an "## Unreleased" nor an "## <version>" section. Windows PowerShell 5.1 compatible.

.EXAMPLE
    .\tools\Set-Version.ps1 1.9.0
    git commit -am "Release 1.9.0"
    git tag v1.9.0
    git push origin main v1.9.0
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory = $true, Position = 0)]
    [ValidatePattern('^\d+\.\d+\.\d+$')]
    [string]$Version
)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$utf8 = New-Object System.Text.UTF8Encoding($false)   # no BOM, like the files in the repo

# file, pattern, replacement (each pattern must match exactly once)
$edits = @(
    @('companion/AssemblyInfo.cs',           '(?m)^\[assembly: AssemblyVersion\("[\d\.]+"\)\]',     "[assembly: AssemblyVersion(""$Version.0"")]"),
    @('companion/AssemblyInfo.cs',           '(?m)^\[assembly: AssemblyFileVersion\("[\d\.]+"\)\]', "[assembly: AssemblyFileVersion(""$Version.0"")]"),
    @('widget/manifest.json',                '"version"\s*:\s*"[\d\.]+"',                           """version"": ""$Version"""),
    @('widget/index.html',                   'var WIDGET_VERSION = "[\d\.]+";',                     "var WIDGET_VERSION = ""$Version"";"),
    @('addon/EdgeLink/EdgeLink.toc',         '(?m)^## Version: [\d\.]+',                            "## Version: $Version"),
    @('addon/EdgeLink/EdgeLink_Forever.toc', '(?m)^## Version: [\d\.]+',                            "## Version: $Version")
)

# Work out every change first; write only if all of them succeed.
$pending = @{}
foreach ($e in $edits) {
    $path = Join-Path $root $e[0]
    if (-not (Test-Path $path)) { throw "Missing file: $($e[0])" }
    $text = if ($pending.ContainsKey($path)) { $pending[$path] } else { [IO.File]::ReadAllText($path) }
    $count = ([regex]::Matches($text, $e[1])).Count
    if ($count -ne 1) { throw "$($e[0]): expected one version line matching '$($e[1])', found $count" }
    $pending[$path] = [regex]::Replace($text, $e[1], $e[2])
}

$changelog = Join-Path $root 'CHANGELOG.md'
$text = [IO.File]::ReadAllText($changelog)
$escaped = [regex]::Escape($Version)
if ([regex]::IsMatch($text, "(?m)^##[ \t]+\[?v?$escaped\]?[ \t]*\r?$")) {
    Write-Host "CHANGELOG.md already has a '## $Version' section."
} elseif ([regex]::IsMatch($text, '(?m)^## Unreleased[ \t]*\r?$')) {
    # git may check files out with CRLF line endings; keep whatever the line ends with
    $pending[$changelog] = ([regex]'(?m)^## Unreleased[ \t]*(\r?)$').Replace($text, "## $Version`$1", 1)
} else {
    throw "CHANGELOG.md has no '## Unreleased' or '## $Version' section. Add the release notes first."
}

foreach ($path in $pending.Keys) {
    [IO.File]::WriteAllText($path, $pending[$path], $utf8)
    Write-Host ("updated " + $path.Substring($root.Length + 1))
}
Write-Host ""
Write-Host "EdgeLink is now version $Version. Next:"
Write-Host "  git commit -am ""Release $Version"""
Write-Host "  git tag v$Version"
Write-Host "  git push origin main v$Version"
