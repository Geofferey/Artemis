<#
.SYNOPSIS
    Publishes Artemis as a self-contained build and builds the plugins next to it.

.DESCRIPTION
    Mirrors the CI publish (.github/workflows/master.yml):
        build\<runtime>\   self-contained Artemis
        build\plugins\     one folder per plugin from the Artemis.Plugins repo

    Packages are always restored from nuget.org explicitly, so the build does not depend on
    the machine's NuGet configuration.

    No version is stamped unless -Version is given. Unversioned builds report "local", which
    disables auto-updating. A stamped version enables auto-update, and the app may replace
    this build with an official release.

.EXAMPLE
    .\build.ps1

.EXAMPLE
    .\build.ps1 -SkipPlugins -Runtime linux-x64
#>
[CmdletBinding()]
param(
    [ValidateSet('Release', 'Debug')]
    [string]$Configuration = 'Release',

    [ValidateSet('win-x64', 'linux-x64', 'osx-x64')]
    [string]$Runtime = 'win-x64',

    [string]$OutputPath = (Join-Path $PSScriptRoot 'build'),

    [string]$PluginsPath = (Join-Path $PSScriptRoot '..\Artemis.Plugins'),

    # Stamps an assembly version like CI does. Enables auto-update, see description.
    [string]$Version,

    [string]$NuGetSource = 'https://api.nuget.org/v3/index.json',

    [switch]$SkipPlugins
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

function Invoke-DotNet
{
    param([Parameter(Mandatory)][string[]]$Arguments)

    Write-Host "> dotnet $($Arguments -join ' ')" -ForegroundColor DarkGray
    & dotnet @Arguments
    if ($LASTEXITCODE -ne 0)
    {
        throw "dotnet $($Arguments[0]) failed with exit code $LASTEXITCODE"
    }
}

function Write-Step
{
    param([string]$Message)
    Write-Host ""
    Write-Host "==> $Message" -ForegroundColor Cyan
}

$timer = [Diagnostics.Stopwatch]::StartNew()

if (!(Get-Command dotnet -ErrorAction SilentlyContinue))
{
    throw "The .NET SDK was not found. Install the .NET 10 SDK from https://dotnet.microsoft.com/download"
}

$platformProject = @{ 'win-x64' = 'Windows'; 'linux-x64' = 'Linux'; 'osx-x64' = 'MacOS' }[$Runtime]
$project = Join-Path $PSScriptRoot "src\Artemis.UI.$platformProject\Artemis.UI.$platformProject.csproj"
$appOutput = Join-Path $OutputPath $Runtime
$pluginsOutput = Join-Path $OutputPath 'plugins'

if (!$SkipPlugins -and !(Test-Path (Join-Path $PluginsPath 'src\Artemis.Plugins.sln')))
{
    throw "Artemis.Plugins was not found at '$PluginsPath'. Clone it with:`n  git clone https://github.com/Artemis-RGB/Artemis.Plugins `"$PluginsPath`"`nor pass -PluginsPath or -SkipPlugins."
}

if ($Version)
{
    Write-Warning "Stamping version $Version enables auto-update. This build may be replaced by an official release."
}

# Start from empty output folders so files from earlier builds don't end up in this one
foreach ($folder in @($appOutput, $pluginsOutput))
{
    if (Test-Path $folder)
    {
        Remove-Item $folder -Recurse -Force
    }
}

Write-Step "Publishing Artemis ($Configuration, $Runtime)"
$publishArgs = @(
    'publish', $project,
    '--configuration', $Configuration,
    '--runtime', $Runtime,
    '--self-contained',
    '--output', $appOutput,
    '--source', $NuGetSource
)
if ($Version)
{
    $publishArgs += "-p:Version=$Version"
}
Invoke-DotNet $publishArgs

if (!$SkipPlugins)
{
    Write-Step "Building plugins ($Configuration)"
    $pluginSolution = Join-Path $PluginsPath 'src\Artemis.Plugins.sln'
    Invoke-DotNet @('restore', $pluginSolution, '--source', $NuGetSource)
    Invoke-DotNet @('build', $pluginSolution, '--configuration', $Configuration, '--no-restore')

    Write-Step "Collecting plugins into $pluginsOutput"
    New-Item -ItemType Directory -Path $pluginsOutput -Force | Out-Null
    $pluginProjects = Get-ChildItem (Join-Path $PluginsPath 'src') -Recurse -Filter *.csproj |
        Where-Object { Test-Path (Join-Path $_.DirectoryName 'plugin.json') }

    foreach ($pluginProject in $pluginProjects)
    {
        # Most plugins output to bin\x64\<Configuration>, those without a platform to bin\<Configuration>
        $binFolder = Join-Path $pluginProject.DirectoryName 'bin'
        $manifest = Get-ChildItem $binFolder -Recurse -Filter plugin.json -ErrorAction SilentlyContinue |
            Where-Object { $_.FullName -like "*\$Configuration\*" } |
            Select-Object -First 1
        if (!$manifest)
        {
            throw "No build output with plugin.json found for $($pluginProject.BaseName) under $binFolder"
        }

        $destination = Join-Path $pluginsOutput $pluginProject.BaseName
        Copy-Item $manifest.DirectoryName $destination -Recurse
        Write-Host "  $($pluginProject.BaseName)"
    }
}

$timer.Stop()
Write-Step "Done in $([int]$timer.Elapsed.TotalSeconds)s"
Write-Host "  Artemis: $appOutput"
if (!$SkipPlugins)
{
    Write-Host "  Plugins: $pluginsOutput ($(@($pluginProjects).Count))"
}
