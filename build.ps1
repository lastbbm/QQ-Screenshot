param(
    [switch]$Quiet
)

$ErrorActionPreference = 'Stop'
$root = $PSScriptRoot
$out = Join-Path $root 'DpSnip.exe'
$sourceDir = Join-Path $root 'src'
$iconPath = Join-Path $root 'assets\DpSnip.ico'
$manifestPath = Join-Path $sourceDir 'app.manifest'

$csc = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
$frameworkDir = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319'
$facadeDir = 'C:\Program Files (x86)\Reference Assemblies\Microsoft\Framework\.NETFramework\v4.8\Facades'
$metadataDir = Join-Path $env:WINDIR 'System32\WinMetadata'

foreach ($required in @($csc, $iconPath, $manifestPath, $metadataDir, $facadeDir)) {
    if (-not (Test-Path $required)) {
        throw "Required build input is missing: $required"
    }
}

$sourceFiles = Get-ChildItem -Path $sourceDir -Filter '*.cs' | Sort-Object Name | Select-Object -ExpandProperty FullName
$references = @(
    (Join-Path $frameworkDir 'System.dll'),
    (Join-Path $frameworkDir 'System.Core.dll'),
    (Join-Path $frameworkDir 'System.Drawing.dll'),
    (Join-Path $frameworkDir 'System.Windows.Forms.dll'),
    (Join-Path $frameworkDir 'System.Runtime.WindowsRuntime.dll'),
    (Join-Path $facadeDir 'System.Runtime.dll'),
    (Join-Path $facadeDir 'System.Runtime.InteropServices.WindowsRuntime.dll')
)
$references += Get-ChildItem -Path $metadataDir -Filter 'Windows.*.winmd' | Select-Object -ExpandProperty FullName

$arguments = @(
    '/nologo',
    '/target:winexe',
    '/platform:x64',
    '/optimize+',
    '/debug-',
    '/codepage:65001',
    '/nowarn:1701,1702',
    "/win32icon:$iconPath",
    "/win32manifest:$manifestPath",
    "/out:$out"
) + ($references | ForEach-Object { "/reference:$_" }) + $sourceFiles

if (-not $Quiet) {
    Write-Host 'Building DpSnip...' -ForegroundColor Cyan
}

& $csc @arguments
if ($LASTEXITCODE -ne 0) {
    throw "C# compiler exited with code $LASTEXITCODE"
}

$file = Get-Item $out
if (-not $Quiet) {
    Write-Host ("Built {0} ({1:N1} KB)" -f $file.FullName, ($file.Length / 1KB)) -ForegroundColor Green
}
