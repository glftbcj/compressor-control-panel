param([string]$DotnetExe = $env:HVACR_DOTNET, [string]$PackageSource = $env:HVACR_NUGET_SOURCE, [switch]$Browser)
$ErrorActionPreference = 'Stop'
$projectRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
if (-not $DotnetExe) {
    $bundled = Join-Path $projectRoot '..\.tools\dotnet\dotnet.exe'
    $DotnetExe = if (Test-Path -LiteralPath $bundled) { $bundled } else { (Get-Command dotnet -ErrorAction Stop).Source }
}
Push-Location -LiteralPath $projectRoot
try {
    $arguments = @('build', 'Hvacr.slnx', '-c', 'Release', '--nologo')
    if ($PackageSource) { $arguments += @('--source', $PackageSource) }
    & $DotnetExe @arguments
    if ($LASTEXITCODE -ne 0) { throw 'Build failed.' }
    & $DotnetExe run --project tests/Hvacr.Tests -c Release --no-build
    if ($LASTEXITCODE -ne 0) { throw 'Backend checks failed.' }
    if ($Browser) {
        $env:HVACR_DOTNET = $DotnetExe
        & npm exec -- playwright test
        if ($LASTEXITCODE -ne 0) { throw 'Browser checks failed.' }
    }
} finally { Pop-Location }
