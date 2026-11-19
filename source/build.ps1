param([string]$OutputPath)
$ErrorActionPreference = 'Stop'
$windowsDirectory = [Environment]::GetFolderPath([Environment+SpecialFolder]::Windows)
$compiler = Join-Path $windowsDirectory 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
if (-not (Test-Path -LiteralPath $compiler)) {
    $compiler = Join-Path $windowsDirectory 'Microsoft.NET\Framework\v4.0.30319\csc.exe'
}
$destination = if ($OutputPath) {
    $ExecutionContext.SessionState.Path.GetUnresolvedProviderPathFromPSPath($OutputPath)
} else {
    Join-Path (Split-Path $PSScriptRoot -Parent) 'DesktopVeil.exe'
}
$sources = @('Program.cs', 'Native.cs', 'State.cs', 'EdgeDock.cs', 'ButtonForm.cs') | ForEach-Object { Join-Path $PSScriptRoot $_ }
$compilerArgs = @('/nologo', '/noconfig', '/target:winexe', '/platform:anycpu', '/optimize+', '/utf8output', '/codepage:65001', '/warn:4', '/warnaserror+',
    '/reference:System.dll', '/reference:System.Core.dll', '/reference:System.Drawing.dll', '/reference:System.Windows.Forms.dll', '/reference:System.Xml.dll',
    "/win32manifest:$(Join-Path $PSScriptRoot 'app.manifest')", "/out:$destination") + $sources
& $compiler @compilerArgs
if ($LASTEXITCODE -ne 0) { throw 'Build failed.' }
Write-Output "Built: $destination"
