[CmdletBinding()]
param(
    [string]$OutputDirectory = (Join-Path $PSScriptRoot 'dist'),
    [string]$DotNetPath = 'dotnet'
)

$ErrorActionPreference = 'Stop'
$repoRoot = [IO.Path]::GetFullPath($PSScriptRoot)
$outputRoot = [IO.Path]::GetFullPath($OutputDirectory)
$publishRoot = Join-Path $outputRoot 'package'
$iconSource = Join-Path $repoRoot 'Assets\WidgetNotesIcon.png'
$iconTarget = Join-Path $repoRoot 'Assets\WidgetNotes.ico'
$iconBuilder = Join-Path $repoRoot 'tools\IconBuilder\IconBuilder.csproj'
$project = Join-Path $repoRoot 'WidgetNotes.csproj'
$nugetConfig = Join-Path $repoRoot 'NuGet.Config'
$buildHome = Join-Path $repoRoot '.tools'
$localDotNet = Join-Path $buildHome 'dotnet-sdk\dotnet.exe'
if ($DotNetPath -eq 'dotnet' -and (Test-Path -LiteralPath $localDotNet)) { $DotNetPath = $localDotNet }
$env:DOTNET_CLI_HOME = Join-Path $buildHome 'dotnet-home'
$env:APPDATA = Join-Path $buildHome 'appdata'
$env:NUGET_PACKAGES = Join-Path $buildHome 'nuget'
$env:DOTNET_SKIP_FIRST_TIME_EXPERIENCE = '1'
$env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'
$env:DOTNET_NOLOGO = '1'

if (-not (Test-Path -LiteralPath $iconSource)) { throw "Icon source is missing: $iconSource" }
if (-not (Test-Path -LiteralPath $iconBuilder)) { throw "Icon builder is missing: $iconBuilder" }
New-Item -ItemType Directory -Path $publishRoot -Force | Out-Null
New-Item -ItemType Directory -Path (Join-Path $env:APPDATA 'NuGet') -Force | Out-Null

& $DotNetPath restore $iconBuilder --configfile $nugetConfig
if ($LASTEXITCODE -ne 0) { throw 'Icon builder restore failed.' }
& $DotNetPath run --project $iconBuilder -c Release --no-restore -- $iconSource $iconTarget
if ($LASTEXITCODE -ne 0) { throw 'Icon build failed.' }

& $DotNetPath restore $project --configfile $nugetConfig --runtime win-x64
if ($LASTEXITCODE -ne 0) { throw 'WidgetNotes restore failed.' }
& $DotNetPath publish $project -c Release -r win-x64 --self-contained true --no-restore -o $publishRoot -p:Version=1.4.0
if ($LASTEXITCODE -ne 0) { throw 'WidgetNotes publish failed.' }

foreach ($legacy in @('ThirdPartyNotices.txt', 'ThirdParty\WPF-Liquid-Glass-Effect-MIT.txt')) {
    $legacyFile = Join-Path $publishRoot $legacy
    if (Test-Path -LiteralPath $legacyFile) { Remove-Item -LiteralPath $legacyFile -Force }
}
foreach ($name in @('Install.cmd', 'Install.ps1', 'Uninstall.ps1', 'README.md', 'DotNet-LICENSE.txt', 'DotNet-ThirdPartyNotices.txt')) {
    $source = Join-Path $repoRoot $name
    if (Test-Path -LiteralPath $source) { Copy-Item -LiteralPath $source -Destination (Join-Path $publishRoot $name) -Force }
}

$archive = Join-Path $outputRoot 'WidgetNotes-Windows-x64.zip'
Compress-Archive -Path (Join-Path $publishRoot '*') -DestinationPath $archive -CompressionLevel Optimal -Force
Write-Host "App:     $(Join-Path $publishRoot 'WidgetNotes.exe')"
Write-Host "Setup:   $(Join-Path $publishRoot 'Install.cmd')"
Write-Host "Package: $archive"
