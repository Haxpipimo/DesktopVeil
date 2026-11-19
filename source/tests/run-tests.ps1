param([switch]$Live, [string]$PreviewPath, [string]$AppPath)
$ErrorActionPreference = 'Stop'
if ($AppPath -and -not $Live) { throw '-AppPath requires -Live.' }
$releaseApp = if ($AppPath) {
    $file = Get-Item -LiteralPath $AppPath
    if ($file.PSIsContainer -or $file.Name -ne 'DesktopVeil.exe') { throw '-AppPath must point to DesktopVeil.exe.' }
    $file.FullName
}
$sourceDir = Split-Path $PSScriptRoot -Parent
$testDir = Join-Path ([System.IO.Path]::GetTempPath()) ('DesktopVeil-tests-' + [Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $testDir | Out-Null
try {
    $app = Join-Path $testDir 'DesktopVeil.exe'
    if ($releaseApp) {
        Copy-Item -LiteralPath $releaseApp -Destination $app
    } else {
        & (Join-Path $sourceDir 'build.ps1') -OutputPath $app
    }
    $testExe = Join-Path $testDir 'DesktopVeil.Tests.exe'
    $windowsDirectory = [Environment]::GetFolderPath([Environment+SpecialFolder]::Windows)
    $compiler = Join-Path $windowsDirectory 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
    if (-not (Test-Path -LiteralPath $compiler)) { $compiler = Join-Path $windowsDirectory 'Microsoft.NET\Framework\v4.0.30319\csc.exe' }
    $testSources = @('Tests.cs', 'DockTests.cs', 'SecurityTests.cs') | ForEach-Object { Join-Path $PSScriptRoot $_ }
    & $compiler /nologo /noconfig /warnaserror+ /utf8output /codepage:65001 /target:exe "/reference:$app" /reference:System.dll /reference:System.Core.dll /reference:System.Windows.Forms.dll /reference:System.Drawing.dll "/out:$testExe" @testSources
    if ($LASTEXITCODE -ne 0) { throw 'Test build failed.' }
    $testArgs = @()
    if ($Live) {
        if ($releaseApp) { $testArgs += @('--live', $releaseApp) }
        else { $testArgs += @('--live', $app) }
    }
    if ($PreviewPath) { $testArgs += @('--preview', $ExecutionContext.SessionState.Path.GetUnresolvedProviderPathFromPSPath($PreviewPath)) }
    & $testExe @testArgs
    if ($LASTEXITCODE -ne 0) { throw 'Tests failed.' }
} finally {
    # Delete only the uniquely created test directory, after verifying its parent.
    $resolvedTestDir = [System.IO.Path]::GetFullPath($testDir)
    $resolvedTempDir = [System.IO.Path]::GetFullPath([System.IO.Path]::GetTempPath()).TrimEnd('\')
    if ((Split-Path $resolvedTestDir -Parent) -eq $resolvedTempDir -and (Split-Path $resolvedTestDir -Leaf) -like 'DesktopVeil-tests-*') {
        Remove-Item -LiteralPath $resolvedTestDir -Recurse -Force
    }
}
