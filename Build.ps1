[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [string] $GameDirectory
)

$ErrorActionPreference = 'Stop'
$game = (Resolve-Path -LiteralPath $GameDirectory).Path
$managed = Join-Path $game 'PC Store Simulator Together_Data/Managed'
$bepInEx = Join-Path $game 'BepInEx/core'
$output = Join-Path $PSScriptRoot 'build'

$sdkVersion = dotnet --version
if ($LASTEXITCODE -ne 0) {
    throw 'Install the .NET SDK and run this script again.'
}

$selectedSdk = dotnet --list-sdks | Where-Object { $_.StartsWith("$sdkVersion ") } | Select-Object -First 1
if ($selectedSdk -notmatch '^[^ ]+ \[(.*)\]$') {
    throw "Could not locate .NET SDK $sdkVersion."
}
$compiler = Join-Path $Matches[1] "$sdkVersion/Roslyn/bincore/csc.dll"

$gameAssemblies = @(
    'mscorlib', 'System', 'System.Core', 'System.Memory', 'netstandard',
    'Assembly-CSharp', 'Fusion.Runtime', 'Fusion.Common', 'Fusion.Sockets',
    'UnityEngine', 'UnityEngine.CoreModule', 'UnityEngine.AudioModule', 'UnityEngine.UI',
    'Unity.TextMeshPro', 'UnityEngine.UnityWebRequestModule', 'UnityEngine.UnityWebRequestAudioModule',
    'com.rlabrecque.steamworks.net'
)
$dependencies = @($gameAssemblies | ForEach-Object { Join-Path $managed "$_.dll" })
$dependencies += @('BepInEx', '0Harmony' | ForEach-Object { Join-Path $bepInEx "$_.dll" })
$references = foreach ($dependency in $dependencies) {
    if (!(Test-Path -LiteralPath $dependency -PathType Leaf)) {
        throw "Missing dependency $dependency"
    }
    "/reference:$dependency"
}

New-Item -ItemType Directory -Path $output -Force | Out-Null
$dll = Join-Path $output 'MusicSyncFix.dll'
$sources = @('MusicSyncFix.cs', 'CoopPlayback.cs' | ForEach-Object { Join-Path $PSScriptRoot $_ })
dotnet $compiler /nologo /target:library /unsafe /optimize+ /deterministic /nostdlib+ /langversion:latest /warnaserror+ "/out:$dll" @references @sources
if ($LASTEXITCODE -ne 0) {
    throw "Compilation failed with exit code $LASTEXITCODE."
}

Get-Item -LiteralPath $dll
