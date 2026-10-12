# Builds Debug, backs up the new artifacts in Downloads, deploys both applications,
# and restarts only the applications that were running before deployment.
param([switch]$SkipBuild, [switch]$BackupOnly)

$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path -Parent $PSScriptRoot
$pluginBuild = Join-Path $repoRoot 'LightSync.Plugin\bin\Debug\net9.0-windows10.0.26100.0'
$standaloneBuild = Join-Path $repoRoot 'Ambilight\bin\Debug'
$pluginInstall = Join-Path $env:LOCALAPPDATA 'LenovoLegionToolkit\Plugins'
$standaloneInstall = 'C:\LightSync'
$toolkitExe = 'C:\Program Files\LenovoLegionToolkit\Lenovo Legion Toolkit.exe'
$standaloneExe = Join-Path $standaloneInstall 'LightSync.exe'
$dependencies = @('Colore.dll', 'MoreLinq.dll', 'OpenRGB.NET.dll', 'SharpDX.dll',
    'SharpDX.Direct3D11.dll', 'SharpDX.DXGI.dll', 'System.Reactive.dll')

function Copy-DeploymentFile([string]$Source, [string]$Destination) {
    # Windows can briefly retain mapped DLLs while a terminated process is being cleaned up.
    for ($attempt = 0; ; $attempt++) {
        try { Copy-Item -LiteralPath $Source -Destination $Destination -Force; return }
        catch [IO.IOException] {
            if ($attempt -ge 39) { throw }
            Start-Sleep -Milliseconds 250
        }
    }
}

$principal = [Security.Principal.WindowsPrincipal]::new([Security.Principal.WindowsIdentity]::GetCurrent())
if (-not $principal.IsInRole([Security.Principal.WindowsBuiltinRole]::Administrator)) {
    throw 'Run this script from an elevated PowerShell to replace the plugin loaded by Toolkit.'
}

if (-not $SkipBuild) {
    & dotnet build (Join-Path $repoRoot 'LightSync.Plugin\LightSync.Plugin.csproj') -c Debug -v:q
    if ($LASTEXITCODE -ne 0) { throw 'LightSync plugin build failed.' }
    $msbuild = 'C:\Program Files\Microsoft Visual Studio\18\Community\MSBuild\Current\Bin\MSBuild.exe'
    & $msbuild (Join-Path $repoRoot 'Ambilight\Ambilight.csproj') /p:Configuration=Debug /p:Platform=AnyCPU /v:quiet /nologo
    if ($LASTEXITCODE -ne 0) { throw 'LightSync standalone build failed.' }
}

foreach ($path in @($toolkitExe, $standaloneExe, (Join-Path $pluginBuild 'LightSync.Plugin.dll'),
    (Join-Path $standaloneBuild 'LightSync.exe'))) {
    if (-not (Test-Path -LiteralPath $path)) { throw "Missing file: $path" }
}

$revision = & git -c "safe.directory=$($repoRoot.Replace('\', '/'))" -C $repoRoot rev-parse --short HEAD
if ($LASTEXITCODE -ne 0) { throw 'Cannot read source revision.' }
$downloadSetting = (Get-ItemProperty 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Explorer\User Shell Folders').'{374DE290-123F-4565-9164-39C4925E467B}'
$downloads = [Environment]::ExpandEnvironmentVariables($downloadSetting)
if (-not (Test-Path -LiteralPath $downloads -PathType Container)) { throw 'Downloads folder not found.' }
$stamp = Get-Date -Format 'yyyy-MM-dd-HHmmss'
$stage = Join-Path $env:TEMP ('LightSyncDeployment-' + [guid]::NewGuid().ToString('N'))
$pluginStage = Join-Path $stage 'plugin'
$standaloneStage = Join-Path $stage 'standalone'
New-Item -ItemType Directory -Path (Join-Path $pluginStage 'Dependency'), $standaloneStage -Force | Out-Null
foreach ($name in @('LightSync.Plugin.dll', 'LightSync.Plugin.pdb')) {
    Copy-Item -LiteralPath (Join-Path $pluginBuild $name) -Destination $pluginStage
}
foreach ($name in $dependencies) {
    Copy-Item -LiteralPath (Join-Path $pluginBuild $name) -Destination (Join-Path $pluginStage 'Dependency')
}
Get-ChildItem -LiteralPath $standaloneBuild -File | ForEach-Object {
    Copy-Item -LiteralPath $_.FullName -Destination $standaloneStage
}
if (Test-Path -LiteralPath (Join-Path $standaloneBuild 'Package')) {
    Copy-Item -LiteralPath (Join-Path $standaloneBuild 'Package') -Destination $standaloneStage -Recurse
}

Add-Type -AssemblyName System.IO.Compression.FileSystem
foreach ($artifactRoot in @($pluginStage, $standaloneStage)) { Copy-Item -LiteralPath (Join-Path $repoRoot 'YEELIGHT-MUSIC-MODE.md') -Destination $artifactRoot }
$backups = @()
foreach ($artifact in @(@{ Name = 'LightSync-Plugin'; Path = $pluginStage },
    @{ Name = 'LightSync-Standalone'; Path = $standaloneStage })) {
    $info = "Debug build`nSource commit: $revision`nBuilt: $stamp`nPer-light effects: Screen Ambilight, Solid Color, Breathing, Color Cycle, Rainbow Wave and Rainbow Wheel; saved color, speed and direction. Integrated color wheel and synchronized Apply to all lights preset with brightness.`n"
    Set-Content -LiteralPath (Join-Path $artifact.Path 'BUILD-INFO.txt') -Value $info -Encoding utf8
    $backup = Join-Path $downloads ("$($artifact.Name)-Debug-$stamp-$revision.zip")
    [IO.Compression.ZipFile]::CreateFromDirectory($artifact.Path, $backup, [IO.Compression.CompressionLevel]::Fastest, $false)
    $backups += $backup
    Write-Output "Backup: $backup"
}

if ($BackupOnly) { Write-Output 'Builds packaged without enabling or restarting applications.'; return }

$toolkitProcesses = @(Get-Process -Name 'Lenovo Legion Toolkit' -ErrorAction SilentlyContinue |
    Where-Object { $_.Path -ieq $toolkitExe })
$standaloneProcesses = @(Get-Process -Name 'LightSync' -ErrorAction SilentlyContinue |
    Where-Object { $_.Path -ieq $standaloneExe })
foreach ($process in @($toolkitProcesses) + @($standaloneProcesses)) {
    Stop-Process -Id $process.Id -Force
    Wait-Process -Id $process.Id -Timeout 15 -ErrorAction SilentlyContinue
}

try {
    New-Item -ItemType Directory -Path (Join-Path $pluginInstall 'Dependency') -Force | Out-Null
    foreach ($name in @('LightSync.Plugin.dll', 'LightSync.Plugin.pdb')) {
        Copy-DeploymentFile (Join-Path $pluginStage $name) $pluginInstall
    }
    foreach ($name in $dependencies) {
        Copy-DeploymentFile (Join-Path $pluginStage "Dependency\$name") (Join-Path $pluginInstall 'Dependency')
    }
    Get-ChildItem -LiteralPath $standaloneStage | Where-Object Name -ne 'BUILD-INFO.txt' | ForEach-Object {
        Copy-Item -LiteralPath $_.FullName -Destination $standaloneInstall -Recurse -Force
    }
    foreach ($pair in @(@{ Source = (Join-Path $pluginBuild 'LightSync.Plugin.dll'); Target = (Join-Path $pluginInstall 'LightSync.Plugin.dll') },
        @{ Source = (Join-Path $standaloneBuild 'LightSync.exe'); Target = $standaloneExe })) {
        if ((Get-FileHash -LiteralPath $pair.Source).Hash -ne (Get-FileHash -LiteralPath $pair.Target).Hash) {
            throw "Installed artifact does not match the build: $($pair.Target)"
        }
        Write-Output "Verified: $($pair.Target)"
    }
}
finally {
    if ($toolkitProcesses.Count -gt 0) {
        Start-Process -FilePath $toolkitExe -ArgumentList '--minimized' -WindowStyle Hidden
    }
    if ($standaloneProcesses.Count -gt 0) {
        Start-Process -FilePath $standaloneExe -ArgumentList '--minimized' -WindowStyle Hidden
    }
}
Write-Output 'Deployment complete.'
