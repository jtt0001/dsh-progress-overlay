# run-overlay.ps1 - launch the DSH progress overlay hosted in-process (no exe on disk).
#
# Used as a fallback when a security suite quarantines freshly compiled unsigned
# dsh-overlay.exe (observed with 360 Total Security). Compiles the same C#
# source in memory via Add-Type and runs it on this STA thread.
# This file must stay pure ASCII (Windows PowerShell 5.1 decodes BOM-less files
# as ANSI).
param(
    [string]$url,
    [string]$state
)
$ErrorActionPreference = 'Stop'
if (-not $url) { $url = 'http://127.0.0.1:3080/api/dsh-overlay/stream' }

$here = Split-Path -Parent $MyInvocation.MyCommand.Path
$sourceFile = Join-Path $here 'dsh-overlay.cs'
if (-not (Test-Path $sourceFile)) { throw "overlay source not found: $sourceFile" }

# Read the C# source strictly as UTF-8 (the file is BOM-less UTF-8).
$code = [System.IO.File]::ReadAllText($sourceFile, [System.Text.UTF8Encoding]::new($false))

# Add-Type auto-references mscorlib/System/System.Core. WPF / WinForms
# must be named explicitly (resolved from the GAC by simple name).
Add-Type -TypeDefinition $code -Language CSharp -ReferencedAssemblies @(
    'System.Xaml',
    'WindowsBase',
    'PresentationCore',
    'PresentationFramework',
    'System.Windows.Forms',
    'System.Drawing'
)

# Add-Type marks the compiled types non-public, so look the type up via
# assembly enumeration instead of a type literal.
$program = $null
foreach ($assembly in [System.AppDomain]::CurrentDomain.GetAssemblies()) {
    $program = $assembly.GetTypes() | Where-Object { $_.FullName -eq 'DshOverlay.Program' } | Select-Object -First 1
    if ($program) { break }
}
if (-not $program) { throw 'DshOverlay.Program type not found after Add-Type' }
$method = $program.GetMethod('Main', [System.Reflection.BindingFlags]'NonPublic,Static')
if (-not $method) { throw 'DshOverlay.Program.Main not found' }
$bound = New-Object object[] 1
$bound[0] = [string[]]@("--url", $url, "--state", $state)
try {
    $null = $method.Invoke($null, $bound)
}
catch {
    Write-Output ("OVERLAY_FATAL: " + $_.Exception.ToString())
    exit 90
}
