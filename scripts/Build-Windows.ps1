param(
    [string]$FFmpegDirectory,
    [string]$OutputDirectory
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
foreach ($tool in @('ffmpeg', 'ffprobe')) {
    $toolFile = Join-Path $FFmpegDirectory ($tool + '.exe')
    if (!(Test-Path -LiteralPath $toolFile)) { throw "Missing tool: $toolFile" }
    $versionOutput = & $toolFile -version
    if ($LASTEXITCODE -ne 0) { throw "$tool failed its version check." }
    $versionOutput | Select-Object -First 1
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
    [PSCustomObject]@{ File = 'tools/' + $tool; SHA256 = (Get-FileHash -LiteralPath $copied -Algorithm SHA256).Hash }
}
$manifest | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $destination 'bundled-tools.json') -Encoding UTF8
@'
Start Vmerger.Desktop.exe. Keep this entire folder together.
This personal-use Windows x64 build includes the .NET runtime, LibVLC, FFmpeg and ffprobe.
No PATH changes or separate runtime installation are required.
Project recovery uses the same local app data directory as the development build.
This is a prototype: actual media boundaries and joins still require verification.
Before public distribution, complete third-party license/source notices and release validation.
'@ | Set-Content -LiteralPath (Join-Path $destination 'START-HERE.txt') -Encoding UTF8
Write-Output "Build ready: $(Join-Path $destination 'Vmerger.Desktop.exe')"
