# Builds the Windows version and packages it as an MSI like the official releases' (see Product.wxs), with
# WiX Toolset 3.14 as they are. The MSI is put in Installer\bin.
#
#   powershell -ExecutionPolicy Bypass -File Installer\build-msi.ps1
#
# -SkipPublish packages the last build (in Installer\obj\publish) again rather than building it afresh.

param([switch]$SkipPublish)

$ErrorActionPreference = 'Stop'

$root = Split-Path $PSScriptRoot -Parent
$obj = Join-Path $PSScriptRoot 'obj'
$bin = Join-Path $PSScriptRoot 'bin'
$publish = Join-Path $obj 'publish'
$stage = Join-Path $obj 'stage'
$wixObj = Join-Path $obj 'wix'
$exeName = 'FPVTracksideCore.exe'

# Self-contained and a single file, as the releases are: the .NET runtime is in the exe.
if (-not $SkipPublish)
{
    if (Test-Path $publish) { Remove-Item $publish -Recurse -Force }
    dotnet publish (Join-Path $root 'FPVTracksideCore\FPVTracksideCore.csproj') -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -o $publish -nologo
    if ($LASTEXITCODE -ne 0) { throw 'Building failed.' }
}

# What's installed. Left out are debug symbols, docs and .NET Framework app configs, the .NET Core 2.1 runtime
# files an old package brings with it, the Mac ffmpeg executables, and the Linux and Mac ffmpeg libraries.
if (Test-Path $stage) { Remove-Item $stage -Recurse -Force }
robocopy $publish $stage /E /NFL /NDL /NJH /NJS /NP `
    /XF *.pdb *.dll.config System.Reactive.xml api-ms-win-*.dll ucrtbase.dll apphost.exe clrcompression.dll dbgshim.dll mscordaccore*.dll mscorrc.debug.dll sos.dll sos_*.dll `
    /XD (Join-Path $publish 'ffmpeg') (Join-Path $publish 'ffmpeg-libs\linux') (Join-Path $publish 'ffmpeg-libs\macos') | Out-Null
if ($LASTEXITCODE -ge 8) { throw 'Copying the built files failed.' }

# The releases come with the DirectShow GMFBridge installer, which the video source settings offer to run. It's
# not in the repository, so it's taken from an installed copy.
if (-not (Test-Path (Join-Path $stage 'GMFBridge.msi')))
{
    $installed = Join-Path $env:LOCALAPPDATA 'FPVTrackside\GMFBridge.msi'
    if (Test-Path $installed) { Copy-Item $installed $stage }
    else { Write-Warning 'GMFBridge.msi was not found, so the installer is without it.' }
}

$versionInfo = (Get-Item (Join-Path $stage $exeName)).VersionInfo

# The installer's version has to be numbers, e.g. 2.78.0.906. The app's informational version, e.g.
# 2.78.0.906-BIRD+9f1dedb747, has the label of a build that isn't an official release (see Directory.Build.targets),
# which goes in the installer's name and the name it's listed by in installed apps.
$version = $versionInfo.FileVersion
$labelledVersion = $versionInfo.ProductVersion.Split('+')[0]
$productName = 'FPVTrackside'
if ($labelledVersion -match '-(.+)$') { $productName += ' ' + $Matches[1] }

# WiX Toolset 3.14, from NuGet.
$packages = $env:NUGET_PACKAGES
if (-not $packages) { $packages = Join-Path $env:USERPROFILE '.nuget\packages' }
$wix = Join-Path $packages 'wix\3.14.1\tools'
if (-not (Test-Path (Join-Path $wix 'candle.exe')))
{
    $restore = Join-Path $obj 'wix-tools'
    New-Item $restore -ItemType Directory -Force | Out-Null
    Set-Content (Join-Path $restore 'wix-tools.csproj') -Encoding UTF8 -Value @'
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <TargetFramework>net9.0</TargetFramework>
  </PropertyGroup>
  <ItemGroup>
    <PackageDownload Include="WiX" Version="[3.14.1]" />
  </ItemGroup>
</Project>
'@
    dotnet restore (Join-Path $restore 'wix-tools.csproj') -nologo
    if ($LASTEXITCODE -ne 0) { throw 'Getting WiX failed.' }
}

if (Test-Path $wixObj) { Remove-Item $wixObj -Recurse -Force }
New-Item $wixObj -ItemType Directory | Out-Null
New-Item $bin -ItemType Directory -Force | Out-Null

& (Join-Path $wix 'heat.exe') dir $stage -nologo -cg AppFiles -dr INSTALLFOLDER -ag -srd -sfrag -scom -sreg -var var.SourceDir -out (Join-Path $wixObj 'AppFiles.wxs')
if ($LASTEXITCODE -ne 0) { throw 'Harvesting the files failed.' }

foreach ($source in (Join-Path $PSScriptRoot 'Product.wxs'), (Join-Path $wixObj 'AppFiles.wxs'))
{
    $compiled = Join-Path $wixObj ([IO.Path]::GetFileNameWithoutExtension($source) + '.wixobj')
    & (Join-Path $wix 'candle.exe') -nologo "-dVersion=$version" "-dProductName=$productName" "-dSourceDir=$stage" "-dExeName=$exeName" -out $compiled $source
    if ($LASTEXITCODE -ne 0) { throw "Compiling $source failed." }
}

# These checks want everything installed to the user's profile to have a registry key path, and each of its
# folders removed by name on uninstall. Like the releases' installer, this doesn't.
$msi = Join-Path $bin "FPVTrackside $labelledVersion.msi"
& (Join-Path $wix 'light.exe') -nologo -spdb -sice:ICE38 -sice:ICE64 -sice:ICE91 -out $msi (Join-Path $wixObj 'Product.wixobj') (Join-Path $wixObj 'AppFiles.wixobj')
if ($LASTEXITCODE -ne 0) { throw 'Linking the installer failed.' }

Write-Host "Built $msi"
