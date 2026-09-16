param([switch]$Test)
$ErrorActionPreference = 'Stop'
$projectRoot = $PSScriptRoot
$compiler = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
if (-not (Test-Path -LiteralPath $compiler)) {
    $compiler = Join-Path $env:WINDIR 'Microsoft.NET\Framework\v4.0.30319\csc.exe'
}
if (-not (Test-Path -LiteralPath $compiler)) { throw '.NET Framework 4.8이 필요합니다.' }
$distPath = Join-Path $projectRoot 'dist'
New-Item -ItemType Directory -Path $distPath -Force | Out-Null
$sourceFiles = @(Get-ChildItem -LiteralPath (Join-Path $projectRoot 'src') -Filter '*.cs' | ForEach-Object FullName)
$exe = Join-Path $distPath 'DdalggakBackup.exe'
$options = @('/nologo', '/target:winexe', '/platform:anycpu', '/optimize+', '/warn:4', '/codepage:65001', '/langversion:5', ('/out:' + $exe), ('/win32manifest:' + (Join-Path $projectRoot 'src\app.manifest')), '/reference:System.dll', '/reference:System.Core.dll', '/reference:System.Drawing.dll', '/reference:System.Windows.Forms.dll', '/reference:System.Xml.dll', '/reference:System.Web.Extensions.dll')
& $compiler @options @sourceFiles
if ($LASTEXITCODE -ne 0) { throw 'GUI build failed.' }
Copy-Item -LiteralPath (Join-Path $projectRoot 'src\App.config') -Destination ($exe + '.config') -Force
if (Test-Path -LiteralPath (Join-Path $projectRoot 'README.md')) {
    Copy-Item -LiteralPath (Join-Path $projectRoot 'README.md') -Destination (Join-Path $distPath '사용안내.md') -Force
}
Write-Output ('Built: ' + $exe)
if ($Test) {
    $testExe = Join-Path $projectRoot 'tests\BackupEngineTests.exe'
    $coreFiles = @($sourceFiles | Where-Object { [IO.Path]::GetFileName($_) -notin @('MainForm.cs', 'Program.cs') })
    $testOptions = @('/nologo', '/target:exe', '/platform:anycpu', '/optimize+', '/warn:4', '/codepage:65001', '/langversion:5', ('/out:' + $testExe), '/reference:System.dll', '/reference:System.Core.dll', '/reference:System.Xml.dll', '/reference:System.Web.Extensions.dll')
    & $compiler @testOptions @coreFiles (Join-Path $projectRoot 'tests\BackupEngineTests.cs')
    if ($LASTEXITCODE -ne 0) { throw 'Tests build failed.' }
    Copy-Item -LiteralPath (Join-Path $projectRoot 'src\App.config') -Destination ($testExe + '.config') -Force
    & $testExe
    if ($LASTEXITCODE -ne 0) { throw 'Tests failed.' }
}
