$ErrorActionPreference = 'Stop'
$projectRoot = Split-Path $PSScriptRoot -Parent
$framework = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319'
$msbuild = Join-Path $framework 'MSBuild.exe'
$compiler = Join-Path $framework 'csc.exe'
& $msbuild (Join-Path $projectRoot 'src\AutoImeSwitcher.csproj') /t:Build /p:Configuration=Release /verbosity:minimal /nologo
if ($LASTEXITCODE -ne 0) { throw 'Application build failed' }
$testOut = Join-Path $projectRoot 'tests\bin'
New-Item -ItemType Directory -Path $testOut -Force | Out-Null
[xml]$project = Get-Content -LiteralPath (Join-Path $projectRoot 'src\AutoImeSwitcher.csproj')
$sources = @($project.Project.ItemGroup.Compile | Where-Object { $_.Include } | ForEach-Object {
    Join-Path (Join-Path $projectRoot 'src') $_.Include
})
$testExe = Join-Path $testOut 'RegressionTests.exe'
$compilerArgs = @('/nologo', '/target:exe', '/main:AutoImeSwitcher.RegressionTests', ('/out:' + $testExe),
    '/reference:System.Windows.Forms.dll', '/reference:System.Drawing.dll',
    '/reference:System.Runtime.Serialization.dll', '/reference:System.Xml.Linq.dll') +
    $sources + (Join-Path $projectRoot 'tests\RegressionTests.cs')
& $compiler @compilerArgs
if ($LASTEXITCODE -ne 0) { throw 'Regression test build failed' }
& $testExe
if ($LASTEXITCODE -ne 0) { throw 'Regression tests failed' }
