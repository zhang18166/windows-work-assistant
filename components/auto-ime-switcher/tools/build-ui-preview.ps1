param([string]$ExampleProgramPath = '')
$ErrorActionPreference = 'Stop'
$projectRoot = Split-Path $PSScriptRoot -Parent
[xml]$project = Get-Content -LiteralPath (Join-Path $projectRoot 'src\AutoImeSwitcher.csproj')
$sources = @($project.Project.ItemGroup.Compile | Where-Object { $_.Include } | ForEach-Object {
    Join-Path (Join-Path $projectRoot 'src') $_.Include
})
$output = Join-Path $projectRoot 'tests\bin'
New-Item -ItemType Directory -Path $output -Force | Out-Null
$previewExe = Join-Path $output 'UiPreview.exe'
$compilerArgs = @('/nologo', '/target:exe', '/main:AutoImeSwitcher.UiPreview', ('/out:' + $previewExe),
    '/reference:System.Windows.Forms.dll', '/reference:System.Drawing.dll', '/reference:System.Runtime.Serialization.dll',
    ('/win32manifest:' + (Join-Path $projectRoot 'src\app.manifest')), ('/win32icon:' + (Join-Path $projectRoot 'src\app.ico'))) +
    $sources + (Join-Path $projectRoot 'tests\UiPreview.cs')
& (Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe') @compilerArgs
if ($LASTEXITCODE -ne 0) { throw 'UI preview build failed' }
& $previewExe (Join-Path $projectRoot 'tests\previews') $ExampleProgramPath
if ($LASTEXITCODE -ne 0) { throw 'UI preview verification failed' }


