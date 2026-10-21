param([string]$Python = "python",[switch]$SkipEngine)
$ErrorActionPreference='Stop'
$root=Split-Path $PSScriptRoot -Parent
$ime=Join-Path $root 'components\auto-ime-switcher'
$pdf=Join-Path $root 'components\excel-auto-pdf'
if(-not $SkipEngine) {
    & $Python (Join-Path $pdf 'tools\build_engine.py')
    if($LASTEXITCODE -ne 0){throw 'PDF engine build failed'}
}
$out=Join-Path $root 'dist\v1.0.1'
New-Item -ItemType Directory -Force -Path $out | Out-Null
$engine=Join-Path $pdf 'dist\engine\ExcelPdfEngine.exe'
$engineHash=(Get-FileHash -LiteralPath $engine -Algorithm SHA256).Hash.ToLower()
$generated=Join-Path $root 'build'
New-Item -ItemType Directory -Force -Path $generated | Out-Null
$buildInfo=Join-Path $generated 'BuildInfo.cs'
('namespace WindowsWorkAssistant { internal static class BuildInfo { internal const string Version="1.0.1"; internal const string EngineSha256="'+$engineHash+'"; } }') | Set-Content -LiteralPath $buildInfo -Encoding UTF8
$sources=@(Get-ChildItem -LiteralPath (Join-Path $ime 'src') -Filter '*.cs' -File | ForEach-Object FullName)+@(Get-ChildItem -LiteralPath (Join-Path $root 'src') -Filter '*.cs' -File | ForEach-Object FullName)+$buildInfo
$compiler=Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
$arguments=@('/nologo','/target:winexe','/main:WindowsWorkAssistant.Program','/platform:x64',('/out:'+(Join-Path $out 'WindowsWorkAssistant.exe')),('/win32manifest:'+(Join-Path $ime 'src\app.manifest')),('/win32icon:'+(Join-Path $ime 'src\app.ico')),('/resource:'+$engine+',ExcelPdfEngine.exe'),'/reference:System.Windows.Forms.dll','/reference:System.Drawing.dll','/reference:System.Runtime.Serialization.dll','/reference:System.Xml.Linq.dll','/reference:System.Web.Extensions.dll')+$sources
& $compiler @arguments
if($LASTEXITCODE -ne 0){throw 'Suite build failed'}
Write-Output ('SUITE_BUILD_PASS '+(Join-Path $out 'WindowsWorkAssistant.exe'))
