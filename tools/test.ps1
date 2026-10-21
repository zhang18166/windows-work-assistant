$ErrorActionPreference='Stop'
$root=Split-Path $PSScriptRoot -Parent
$ime=Join-Path $root 'components\auto-ime-switcher'
$engine=Join-Path $root 'components\excel-auto-pdf\dist\engine\ExcelPdfEngine.exe'
$sources=@(Get-ChildItem -LiteralPath (Join-Path $ime 'src') -Filter '*.cs' -File | ForEach-Object FullName)+@(Get-ChildItem -LiteralPath (Join-Path $root 'src') -Filter '*.cs' -File | ForEach-Object FullName)+(Join-Path $root 'build\BuildInfo.cs')+(Join-Path $root 'tests\SmokeTests.cs')
$out=Join-Path $root ('build\SuiteSmoke-'+[guid]::NewGuid().ToString('N').Substring(0,8)+'.exe')
$compiler=Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
$arguments=@('/nologo','/target:exe','/main:WindowsWorkAssistant.SmokeTests','/platform:x64',('/out:'+$out),('/resource:'+$engine+',ExcelPdfEngine.exe'),('/win32manifest:'+(Join-Path $ime 'src\app.manifest')),'/reference:System.Windows.Forms.dll','/reference:System.Drawing.dll','/reference:System.Runtime.Serialization.dll','/reference:System.Xml.Linq.dll','/reference:System.Web.Extensions.dll')+$sources
& $compiler @arguments
if($LASTEXITCODE -ne 0){throw 'Test compilation failed'}
& $out
if($LASTEXITCODE -ne 0){throw 'Suite smoke failed'}
