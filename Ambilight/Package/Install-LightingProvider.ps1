<#
  Registers Razer Ambilight as a Windows Dynamic Lighting "background light control" app.

  What it does (one-time, must run as Administrator):
    1. creates a self-signed code-signing certificate (CN=RazerAmbilightDev) and trusts it
    2. builds + signs a small sparse MSIX package containing only the manifest
    3. registers it with C:\RazerAmbilight as the external location, giving Ambilight.exe
       package identity and the com.microsoft.windows.lighting extension

  Afterwards: Settings > Personalization > Dynamic Lighting > (click the laptop keyboard card)
  > Background light control, and drag "Razer Ambilight" above the other apps.

  Usage (elevated):  .\Install-LightingProvider.ps1 [-AppDir C:\RazerAmbilight]
  Remove:            .\Install-LightingProvider.ps1 -Uninstall
#>
param(
    [string]$AppDir = 'C:\RazerAmbilight',
    [switch]$Uninstall
)

$ErrorActionPreference = 'Stop'

# The Appx cmdlets are most reliable in Windows PowerShell 5.1.
if ($PSVersionTable.PSEdition -eq 'Core') {
    $forward = @('-NoProfile', '-ExecutionPolicy', 'Bypass', '-File', $PSCommandPath, '-AppDir', $AppDir)
    if ($Uninstall) { $forward += '-Uninstall' }
    & "$env:SystemRoot\System32\WindowsPowerShell\v1.0\powershell.exe" @forward
    exit $LASTEXITCODE
}

$principal = New-Object Security.Principal.WindowsPrincipal([Security.Principal.WindowsIdentity]::GetCurrent())
if (-not $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
    throw 'Run this script from an elevated (Administrator) PowerShell.'
}

$packageName = 'RazerAmbilight.Lighting'
$subject     = 'CN=RazerAmbilightDev'
$here        = Split-Path -Parent $PSCommandPath

if ($Uninstall) {
    Get-AppxPackage -Name $packageName | Remove-AppxPackage
    Get-ChildItem Cert:\LocalMachine\TrustedPeople | Where-Object Subject -eq $subject | Remove-Item
    Write-Host 'Razer Ambilight lighting package removed.'
    return
}

if (-not (Test-Path (Join-Path $AppDir 'Ambilight.exe'))) { throw "Ambilight.exe not found in $AppDir" }

$sdkBin = Get-ChildItem "${env:ProgramFiles(x86)}\Windows Kits\10\bin" -Directory |
    Where-Object { Test-Path (Join-Path $_.FullName 'x64\makeappx.exe') } |
    Sort-Object Name -Descending | Select-Object -First 1
if (-not $sdkBin) { throw 'Windows SDK (makeappx.exe / signtool.exe) not found.' }
$makeappx = Join-Path $sdkBin.FullName 'x64\makeappx.exe'
$signtool = Join-Path $sdkBin.FullName 'x64\signtool.exe'

$work = Join-Path $env:TEMP 'RazerAmbilightLightingPkg'
if (Test-Path $work) { Remove-Item $work -Recurse -Force }
$stage = Join-Path $work 'stage'
New-Item -ItemType Directory -Path "$stage\Assets", "$stage\public" -Force | Out-Null
Copy-Item (Join-Path $here 'AppxManifest.xml') $stage
Copy-Item (Join-Path $here 'public\readme.txt') "$stage\public"

# Package logos, rendered from the app icon.
Add-Type -AssemblyName System.Drawing
$icon = New-Object System.Drawing.Icon((Join-Path $AppDir 'Color_Wheel.ico'), 256, 256)
foreach ($logo in @(@('StoreLogo', 50), @('Square44x44Logo', 44), @('Square150x150Logo', 150))) {
    $bmp = New-Object System.Drawing.Bitmap($logo[1], $logo[1])
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.InterpolationMode = 'HighQualityBicubic'
    $g.DrawImage($icon.ToBitmap(), 0, 0, $logo[1], $logo[1])
    $g.Dispose()
    $bmp.Save("$stage\Assets\$($logo[0]).png", [System.Drawing.Imaging.ImageFormat]::Png)
    $bmp.Dispose()
}

# Self-signed certificate; Windows only installs a package signed by a trusted publisher.
$cert = Get-ChildItem Cert:\CurrentUser\My | Where-Object Subject -eq $subject | Select-Object -First 1
if (-not $cert) {
    $cert = New-SelfSignedCertificate -Type Custom -Subject $subject -KeyUsage DigitalSignature `
        -FriendlyName 'Razer Ambilight (dev)' -CertStoreLocation Cert:\CurrentUser\My -NotAfter (Get-Date).AddYears(10) `
        -TextExtension @('2.5.29.37={text}1.3.6.1.5.5.7.3.3', '2.5.29.19={text}')
}
$cer = Join-Path $work 'RazerAmbilightDev.cer'
Export-Certificate -Cert $cert -FilePath $cer | Out-Null
Import-Certificate -FilePath $cer -CertStoreLocation Cert:\LocalMachine\TrustedPeople | Out-Null

$password = ConvertTo-SecureString -String ([guid]::NewGuid().ToString('N')) -AsPlainText -Force
$pfx = Join-Path $work 'sign.pfx'
Export-PfxCertificate -Cert $cert -FilePath $pfx -Password $password | Out-Null
$plain = [Runtime.InteropServices.Marshal]::PtrToStringAuto([Runtime.InteropServices.Marshal]::SecureStringToBSTR($password))

$msix = Join-Path $work "$packageName.msix"
& $makeappx pack /d $stage /p $msix /nv /o | Out-Host
if ($LASTEXITCODE -ne 0) { throw 'makeappx failed.' }
& $signtool sign /fd SHA256 /f $pfx /p $plain $msix | Out-Host
if ($LASTEXITCODE -ne 0) { throw 'signtool failed.' }
Remove-Item $pfx -Force

Get-AppxPackage -Name $packageName | Remove-AppxPackage
Add-AppxPackage -Path $msix -ExternalLocation $AppDir

$installed = Get-AppxPackage -Name $packageName
if (-not $installed) { throw 'Registration did not produce an installed package.' }
Write-Host ''
Write-Host "Registered: $($installed.PackageFullName)"
Write-Host "External location: $($installed.InstallLocation)"
Write-Host 'Now (re)start Ambilight from that folder, then open Settings > Personalization > Dynamic Lighting.'
