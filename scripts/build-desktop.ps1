param([string]$DotnetExe = $env:HVACR_DOTNET, [string]$PackageSource = $env:HVACR_NUGET_SOURCE,
    [string]$PrivateSettingsPath, [switch]$PublicRelease)
$ErrorActionPreference = 'Stop'
$projectRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
if (-not $DotnetExe) {
    $bundled = Join-Path $projectRoot '..\.tools\dotnet\dotnet.exe'
    $DotnetExe = if (Test-Path -LiteralPath $bundled) { $bundled } else { (Get-Command dotnet -ErrorAction Stop).Source }
}
$desktopProject = Join-Path $projectRoot 'desktop\Hvacr.Desktop\Hvacr.Desktop.csproj'
if (-not (Test-Path -LiteralPath $desktopProject)) { throw 'Private desktop source is required before packaging.' }
if ($PublicRelease -and $PrivateSettingsPath) { throw 'Public releases cannot embed private connection settings.' }
if (-not $PublicRelease -and -not $PrivateSettingsPath) {
    $privateCandidates = @()
    if ($env:HVACR_SETTINGS) { $privateCandidates += $env:HVACR_SETTINGS }
    $dataDirectory = if ($env:HVACR_DATA_DIR) { $env:HVACR_DATA_DIR } else { Join-Path $env:APPDATA 'HVACR' }
    $privateCandidates += @((Join-Path $dataDirectory 'settings.json'),
        (Join-Path $projectRoot 'artifacts\release\settings.json'), (Join-Path $projectRoot '.local\settings.json'))
    $PrivateSettingsPath = $privateCandidates | Where-Object { Test-Path -LiteralPath $_ -PathType Leaf } | Select-Object -First 1
}
if ($PrivateSettingsPath) {
    $PrivateSettingsPath = (Resolve-Path -LiteralPath $PrivateSettingsPath).Path
    $null = Get-Content -LiteralPath $PrivateSettingsPath -Raw | ConvertFrom-Json -ErrorAction Stop
}
$publishDirectory = Join-Path $projectRoot ('.local\publish\' + [DateTime]::UtcNow.ToString('yyyyMMdd-HHmmss'))
$releaseDirectory = Join-Path $projectRoot $(if ($PublicRelease) { 'artifacts\github-release' } else { 'artifacts\release' })
New-Item -ItemType Directory -Force -Path $publishDirectory,$releaseDirectory | Out-Null
$arguments = @('publish', $desktopProject, '-c', 'Release', '-r', 'win-x64', '-o', $publishDirectory, '/p:DebugType=None', '/p:DebugSymbols=false')
if ($PackageSource) { $arguments += @('--source', $PackageSource) }
$arguments += ('/p:HvacrPrivateSettingsPath=' + $PrivateSettingsPath)
& $DotnetExe @arguments
if ($LASTEXITCODE -ne 0) { throw 'Desktop publish failed.' }
Copy-Item -LiteralPath (Join-Path $publishDirectory 'Hvacr.Desktop.exe') -Destination (Join-Path $releaseDirectory 'hvacr.exe') -Force
# Preserve previous support files privately; the delivery directory only needs the exe.
$archiveDirectory = Join-Path $projectRoot ('.local\release-support\' + [DateTime]::UtcNow.ToString('yyyyMMdd-HHmmss'))
$releasePrefix = [IO.Path]::GetFullPath($releaseDirectory).TrimEnd('\') + '\'
$projectPrefix = [IO.Path]::GetFullPath($projectRoot).TrimEnd('\') + '\'
foreach ($supportName in $(if ($PublicRelease) { @() } else { @('settings.json','demo.cmd','safe-control.cmd','使用说明.md','smoke-data','demo-data','smoke-result.json','smoke-stage.txt') })) {
    $supportPath = [IO.Path]::GetFullPath((Join-Path $releaseDirectory $supportName))
    $archivePath = [IO.Path]::GetFullPath((Join-Path $archiveDirectory $supportName))
    if (-not $supportPath.StartsWith($releasePrefix, [StringComparison]::OrdinalIgnoreCase) -or
        -not $archivePath.StartsWith($projectPrefix, [StringComparison]::OrdinalIgnoreCase)) { throw 'Support archive path is outside the workspace.' }
    if (Test-Path -LiteralPath $supportPath) {
        if ((Get-Item -LiteralPath $supportPath -Force).Attributes -band [IO.FileAttributes]::ReparsePoint) { throw 'Refusing to archive a linked support path.' }
        New-Item -ItemType Directory -Force -Path $archiveDirectory | Out-Null
        Move-Item -LiteralPath $supportPath -Destination $archivePath -ErrorAction Stop
    }
}
Write-Host ('Ready: ' + (Join-Path $releaseDirectory 'hvacr.exe'))
