# Builds and runs the update-flow test (tests\UpdateTest.cs) with the .NET Framework compiler.
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$csc = "$env:WINDIR\Microsoft.NET\Framework64\v4.0.30319\csc.exe"
$out = Join-Path $env:TEMP 'edgelink-update-build'
New-Item -ItemType Directory -Force $out | Out-Null

# Dummy setup program: writes the arguments it was started with next to itself
$dummy = Join-Path $out 'dummy-setup.cs'
Set-Content $dummy -Encoding ascii 'class S { static void Main(string[] a) { System.IO.File.WriteAllText(System.IO.Path.Combine(System.AppDomain.CurrentDomain.BaseDirectory, "setup-args.txt"), string.Join(" ", a)); } }'
& $csc /nologo /target:exe /out:"$out\dummy-setup.exe" $dummy
if ($LASTEXITCODE) { exit 1 }

& $csc /nologo /target:exe /out:"$out\UpdateTest.exe" /r:System.dll /r:System.Core.dll /r:System.Drawing.dll /r:System.Windows.Forms.dll /r:System.Web.Extensions.dll `
    "$root\tests\UpdateTest.cs" "$root\companion\Updater.cs" "$root\companion\EdgeLinkCore.cs"
if ($LASTEXITCODE) { exit 1 }
& "$out\UpdateTest.exe" "$out\dummy-setup.exe"
exit $LASTEXITCODE
