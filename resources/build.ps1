# resources/build.ps1 - build dsh-overlay.exe with the system compiler (.NET Framework 4.8 WPF).
#
# No SDK/Visual Studio needed: .NET Framework 4.8 ships Roslyn csc (C# 7.3).
# Output: resources/dsh-overlay.exe (single file; only runtime deps from the OS).
# NOTE: keep this file pure ASCII so Windows PowerShell 5.1 (ANSI decode of
# BOM-less files) parses it correctly on any code page.
$ErrorActionPreference = 'Stop'
$here = Split-Path -Parent $MyInvocation.MyCommand.Path
$out = Join-Path $here 'dsh-overlay.exe'

# 1) locate csc (prefer 4.8 Roslyn)
$csc = $null
$candidates = @(
    "$env:WINDIR\Microsoft.NET\Framework64\v4.0.30319\csc.exe",
    "$env:WINDIR\Microsoft.NET\Framework\v4.0.30319\csc.exe"
)
foreach ($candidate in $candidates) {
    if (Test-Path $candidate) { $csc = $candidate; break }
}
if (-not $csc) { throw 'csc.exe not found - .NET Framework 4.x required' }

# 2) reference assemblies (runtime WPF dir; fall back to ref assemblies dir)
$sysDir = "$env:WINDIR\Microsoft.NET\Framework64\v4.0.30319"
$wpfDir = Join-Path $sysDir 'WPF'
if (-not (Test-Path (Join-Path $wpfDir 'PresentationFramework.dll'))) {
    $wpfDir = 'C:\Program Files (x86)\Reference Assemblies\Microsoft\Framework\.NETFramework'
    $refVer = Get-ChildItem $wpfDir -Directory -ErrorAction SilentlyContinue |
        Where-Object { $_.Name -match '^v4\.\d' } |
        Sort-Object Name -Descending | Select-Object -First 1
    if (-not $refVer) { throw 'WPF reference assemblies not found' }
    $wpfDir = $refVer.FullName
}

$refs = @(
    'mscorlib.dll', 'System.dll', 'System.Core.dll',
    'System.Xaml.dll', 'WindowsBase.dll', 'PresentationCore.dll',
    'PresentationFramework.dll', 'System.Windows.Forms.dll', 'System.Net.dll'
)
$refArgs = foreach ($ref in $refs) {
    $path = Join-Path $wpfDir $ref
    if (-not (Test-Path $path)) { $path = Join-Path $sysDir $ref }
    if (-not (Test-Path $path)) { throw "reference not found: $ref" }
    "/r:$path"
}

$source = Join-Path $here 'dsh-overlay.cs'
if (-not (Test-Path $source)) { throw "source not found: $source" }

$outArgs = @(
    '/nologo', '/target:winexe', '/optimize+', '/platform:anycpu',
    "/out:$out"
) + $refArgs + @($source)

Write-Host 'compiling dsh-overlay.cs ...'
& $csc $outArgs
if ($LASTEXITCODE -ne 0) { throw "csc failed with exit code $LASTEXITCODE" }
Write-Host "built: $out ($((Get-Item $out).Length) bytes)"
