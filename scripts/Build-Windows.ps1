param(
    [string]$FFmpegDirectory,
    [string]$OutputDirectory,
    [string]$FFmpegSourceUrl
)

$ErrorActionPreference = 'Stop'
$repository = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
if (!$OutputDirectory) {
    $OutputDirectory = Join-Path $repository ('artifacts/vmerger-win-x64-' + (Get-Date -Format 'yyyyMMdd-HHmmss'))
}
$destination = [IO.Path]::GetFullPath($OutputDirectory)
if (Test-Path -LiteralPath $destination) { throw "Output already exists: $destination. Choose a new directory." }

if (!$FFmpegDirectory) {
    $searchDirectories = @(
        ($env:PATH -split ';')
        ([Environment]::GetEnvironmentVariable('Path', 'User') -split ';')
        ([Environment]::GetEnvironmentVariable('Path', 'Machine') -split ';')
    )
    $FFmpegDirectory = $searchDirectories | Where-Object {
        $_ -and (Test-Path -LiteralPath (Join-Path $_ 'ffmpeg.exe')) -and
        (Test-Path -LiteralPath (Join-Path $_ 'ffprobe.exe'))
    } | Select-Object -First 1
}
if (!$FFmpegDirectory) { throw 'Pass -FFmpegDirectory pointing to a directory containing ffmpeg.exe and ffprobe.exe.' }
$toolVersions = @{}
$toolSourceLinks = @{}
foreach ($tool in @('ffmpeg', 'ffprobe')) {
    $toolFile = Join-Path $FFmpegDirectory ($tool + '.exe')
    if (!(Test-Path -LiteralPath $toolFile)) { throw "Missing tool: $toolFile" }
    $versionOutput = & $toolFile -version
    if ($LASTEXITCODE -ne 0) { throw "$tool failed its version check." }
    $versionOutput | Select-Object -First 1
    $toolVersions[$tool] = @($versionOutput)
    $versionLine = $versionOutput | Select-Object -First 1
    if ($FFmpegSourceUrl) {
        $toolSourceLinks[$tool] = $FFmpegSourceUrl
    }
    elseif ($versionLine -match '\bgit-([a-fA-F0-9]{7,40})\b') {
        $toolSourceLinks[$tool] = 'https://github.com/FFmpeg/FFmpeg/archive/' + $Matches[1] + '.zip'
    }
    elseif ($versionLine -match '^\S+ version ([0-9]+\.[0-9]+(?:\.[0-9]+)?)(?:[-\s]|$)') {
        $toolSourceLinks[$tool] = 'https://github.com/FFmpeg/FFmpeg/archive/refs/tags/n' + $Matches[1] + '.zip'
    }
    else {
        $toolSourceLinks[$tool] = 'UNRESOLVED: supply -FFmpegSourceUrl for this build.'
        Write-Warning "Cannot infer the source version of $tool. Supply -FFmpegSourceUrl."
    }
}

$dotnet = (Get-Command dotnet -ErrorAction SilentlyContinue).Source
if (!$dotnet) { $dotnet = Join-Path $env:ProgramFiles 'dotnet/dotnet.exe' }
if (!(Test-Path -LiteralPath $dotnet)) { throw '.NET SDK is required to build the application.' }
& $dotnet publish (Join-Path $repository 'src/Vmerger.Desktop/Vmerger.Desktop.csproj') `
    -c Release -r win-x64 --self-contained true -p:PublishSingleFile=false -p:PublishTrimmed=false -o $destination
if ($LASTEXITCODE -ne 0) { throw 'Publishing failed. The output directory may contain an incomplete build.' }

$toolsDirectory = Join-Path $destination 'tools'
New-Item -ItemType Directory -Path $toolsDirectory | Out-Null
foreach ($tool in @('ffmpeg.exe', 'ffprobe.exe')) {
    Copy-Item -LiteralPath (Join-Path $FFmpegDirectory $tool) -Destination (Join-Path $toolsDirectory $tool)
}
$licensesDirectory = Join-Path $destination 'licenses/ffmpeg'
New-Item -ItemType Directory -Path $licensesDirectory -Force | Out-Null
$distributionDirectory = Split-Path -Parent $FFmpegDirectory
foreach ($notice in @('LICENSE', 'README.txt')) {
    $noticePath = Join-Path $distributionDirectory $notice
    if (Test-Path -LiteralPath $noticePath) {
        Copy-Item -LiteralPath $noticePath -Destination (Join-Path $licensesDirectory $notice)
    }
}
$manifest = foreach ($tool in @('ffmpeg.exe', 'ffprobe.exe')) {
    $copied = Join-Path $toolsDirectory $tool
    $toolName = [IO.Path]::GetFileNameWithoutExtension($tool)
    [PSCustomObject]@{
        File = 'tools/' + $tool
        Version = $toolVersions[$toolName][0]
        Source = $toolSourceLinks[$toolName]
        SHA256 = (Get-FileHash -LiteralPath $copied -Algorithm SHA256).Hash
    }
}
$manifest | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $destination 'bundled-tools.json') -Encoding UTF8

[xml]$packageVersions = Get-Content -LiteralPath (Join-Path $repository 'Directory.Packages.props') -Raw
function Get-PackageVersion([string]$packageName) {
    $entry = $packageVersions.Project.ItemGroup.PackageVersion | Where-Object { $_.Include -eq $packageName }
    if (!$entry) { throw "Missing pinned package version: $packageName" }
    return $entry.Version
}
$avaloniaVersion = Get-PackageVersion 'Avalonia'
$libVlcSharpVersion = Get-PackageVersion 'LibVLCSharp.Avalonia'
$libVlcVersion = Get-PackageVersion 'VideoLAN.LibVLC.Windows'
$runtimeConfig = Get-Content -LiteralPath (Join-Path $destination 'Vmerger.Desktop.runtimeconfig.json') -Raw | ConvertFrom-Json
$runtimeVersion = ($runtimeConfig.runtimeOptions.includedFrameworks | Where-Object { $_.name -eq 'Microsoft.NETCore.App' }).version
@"
PRIMARY SOURCE REFERENCES

These references identify upstream source releases from the bundled tool and package versions.
They are not a complete inventory of transitive dependencies or a verified complete
Corresponding Source distribution. Native dependencies, patches and build scripts
must also be accounted for before redistribution. Keep the applicable license notices.

FFmpeg
$($toolVersions['ffmpeg'][0])
$($toolSourceLinks['ffmpeg'])

ffprobe
$($toolVersions['ffprobe'][0])
$($toolSourceLinks['ffprobe'])

FFmpeg upstream build documentation:
https://ffmpeg.org/ffmpeg.html
For Gyan builds, distributor and external-library information:
https://www.gyan.dev/ffmpeg/builds/
Build configuration reported by the shipped tools: ffmpeg-build-info.txt

LibVLCSharp / LibVLCSharp.Avalonia $libVlcSharpVersion
https://github.com/videolan/libvlcsharp/archive/refs/tags/$libVlcSharpVersion.zip

VideoLAN.LibVLC.Windows $libVlcVersion
VLC core source:
https://download.videolan.org/pub/videolan/vlc/$libVlcVersion/vlc-$libVlcVersion.tar.xz
NuGet packaging/build references:
https://code.videolan.org/videolan/libvlc-nuget
VLC contrib dependency build definitions are included in the VLC source.
The complete matching sources for packaged contrib binaries/plugins are not verified here.

Avalonia $avaloniaVersion (including Fluent theme and Inter font package)
https://github.com/AvaloniaUI/Avalonia/archive/refs/tags/$avaloniaVersion.zip
Inter font upstream reference (the exact embedded font revision is not verified here):
https://github.com/rsms/inter

.NET runtime $runtimeVersion
https://github.com/dotnet/runtime/archive/refs/tags/v$runtimeVersion.zip

Tool versions and SHA-256 hashes: bundled-tools.json
"@ | Set-Content -LiteralPath (Join-Path $destination 'SOURCES.txt') -Encoding UTF8
@(
    'FFmpeg version and configuration:'
    $toolVersions['ffmpeg']
    ''
    'ffprobe version and configuration:'
    $toolVersions['ffprobe']
) | Set-Content -LiteralPath (Join-Path $destination 'ffmpeg-build-info.txt') -Encoding UTF8
@'
Start Vmerger.Desktop.exe. Keep this entire folder together.
This personal-use Windows x64 build includes the .NET runtime, LibVLC, FFmpeg and ffprobe.
No PATH changes or separate runtime installation are required.
Project recovery uses the same local app data directory as the development build.
This is a prototype: actual media boundaries and joins still require verification.
Before public distribution, complete third-party license/source notices and release validation.
Source references: SOURCES.txt. Tool versions/configuration: ffmpeg-build-info.txt.
'@ | Set-Content -LiteralPath (Join-Path $destination 'START-HERE.txt') -Encoding UTF8
Write-Output "Build ready: $(Join-Path $destination 'Vmerger.Desktop.exe')"
