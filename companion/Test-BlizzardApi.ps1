<#
  Test-BlizzardApi.ps1  -  does Blizzard's web API cover WoW Forever?
  1. Create a free client at https://develop.battle.net/access/clients
  2. Put the ID and secret in companion\blizzard.ini (never shared, never committed):
        ClientId=xxxxxxxx
        ClientSecret=xxxxxxxx
        Region=us
  3. Run:  powershell -NoProfile -ExecutionPolicy Bypass -File Test-BlizzardApi.ps1 [-Character Name] [-Realm slug]
  Prints which namespaces answer, and any realm/region named Forever. The secret is never printed.
#>
param([string]$Character = '', [string]$Realm = '')
$ErrorActionPreference = 'Stop'
[Net.ServicePointManager]::SecurityProtocol = [Net.SecurityProtocolType]::Tls12
$ini = Join-Path $PSScriptRoot 'blizzard.ini'
if (-not (Test-Path $ini)) { Write-Host "Missing $ini - see the header of this script." -ForegroundColor Yellow; exit 1 }
$cfg = @{}; Get-Content $ini | ? { $_ -match '^\s*([^#;=]+?)\s*=\s*(.*?)\s*$' } | % { $cfg[$matches[1]] = $matches[2] }
$region = if ($cfg.Region) { $cfg.Region.ToLower() } else { 'us' }

$pair = [Convert]::ToBase64String([Text.Encoding]::ASCII.GetBytes("$($cfg.ClientId):$($cfg.ClientSecret)"))
$tok = Invoke-RestMethod -Method Post -Uri 'https://oauth.battle.net/token' -Headers @{ Authorization = "Basic $pair" } -Body @{ grant_type = 'client_credentials' }
Write-Host "Token OK (expires in $($tok.expires_in) s)" -ForegroundColor Green
$h = @{ Authorization = "Bearer $($tok.access_token)" }
$base = "https://$region.api.blizzard.com"

function Try-Get($path, $ns) {
    try { $r = Invoke-RestMethod -Uri "$base$path`?namespace=$ns&locale=en_US" -Headers $h; return @{ ok = $true; data = $r } }
    catch { $code = try { [int]$_.Exception.Response.StatusCode } catch { 0 }; return @{ ok = $false; code = $code } }
}

$suffixes = 'forever','wowf','camelot','classicf','classic1x','classic','classicann','classic2x','classic3x'
$hits = @()
foreach ($s in $suffixes) {
    $ns = "dynamic-$s-$region"
    $r = Try-Get '/data/wow/realm/index' $ns
    if ($r.ok) {
        $names = @($r.data.realms | % { $_.name })
        $forever = @($r.data.realms | ? { $_.name -match 'forever' -or $_.slug -match 'forever' })
        Write-Host ("{0,-28} OK   {1} realms{2}" -f $ns, $names.Count, $(if ($forever) { '  <-- Forever: ' + (($forever | % { "$($_.name) [$($_.slug)]" }) -join ', ') } else { '' })) -ForegroundColor $(if ($forever) { 'Green' } else { 'Gray' })
        if ($forever) { $hits += @{ suffix = $s; realms = $forever } }
    } else { Write-Host ("{0,-28} {1}" -f $ns, $r.code) -ForegroundColor DarkGray }
}
foreach ($s in $suffixes) {
    $r = Try-Get '/data/wow/region/index' "dynamic-$s-$region"
    if ($r.ok) { $r.data.regions | % { $reg = Invoke-RestMethod -Uri ($_.href) -Headers $h -ErrorAction SilentlyContinue; if ($reg.name -match 'forever|wowf') { Write-Host "Region match in dynamic-$s-$region : $($reg.name) (id $($reg.id))" -ForegroundColor Green } } }
}

if ($Character) {
    $slugs = @(); if ($Realm) { $slugs += $Realm }; foreach ($hit in $hits) { $slugs += @($hit.realms | % { $_.slug }) }
    $sfx = @($hits | % { $_.suffix }); if (-not $sfx) { $sfx = $suffixes }
    foreach ($s in $sfx) { foreach ($slug in ($slugs | Select -Unique)) {
        $r = Try-Get "/profile/wow/character/$slug/$($Character.ToLower())" "profile-$s-$region"
        Write-Host ("character {0}/{1} in profile-{2}-{3}: {4}" -f $slug, $Character, $s, $region, $(if ($r.ok) { "FOUND level $($r.data.level) $($r.data.character_class.name)" } else { $r.code })) -ForegroundColor $(if ($r.ok) { 'Green' } else { 'DarkGray' })
    } }
}
if (-not $hits) { Write-Host "`nNo Forever realms found in any known namespace - the API does not appear to cover Forever yet." -ForegroundColor Yellow }
