param(
    [string]$ZipPath,
    [string]$FFmpegFolder,
    [string]$NAudioPath = (Join-Path $env:LOCALAPPDATA 'ReplayShelf\NAudio.dll')
)

$ErrorActionPreference = 'Stop'
$diagnostics = Get-Content -LiteralPath (Join-Path $PSScriptRoot 'Diagnostics.cs') -Raw
$release = [regex]::Match($diagnostics, 'AssemblyInformationalVersion\("(?<version>v\d+\.\d{2})"\)')
if (!$release.Success) { throw 'Could not read the release version from Diagnostics.cs.' }
$bundleName = 'clippings ' + $release.Groups['version'].Value
if (!$ZipPath) { $ZipPath = Join-Path (Get-Location).Path ($bundleName + '.zip') }
$ZipPath = [IO.Path]::GetFullPath($ZipPath)
if ([IO.Path]::GetExtension($ZipPath) -ne '.zip') { throw 'Choose an output filename ending in .zip.' }
if (Test-Path -LiteralPath $ZipPath) { throw "The output file already exists: $ZipPath" }
if (!(Test-Path -LiteralPath $NAudioPath -PathType Leaf)) { throw 'NAudio.dll is missing. Pass -NAudioPath or build on a PC with clippings installed.' }
if (!$FFmpegFolder) {
    $ff = Get-ChildItem "$env:LOCALAPPDATA\Microsoft\WinGet\Packages" -Recurse -Filter ffmpeg.exe -ErrorAction SilentlyContinue |
        Where-Object FullName -like '*Gyan.FFmpeg*' | Select-Object -First 1
    if ($ff) { $FFmpegFolder = $ff.DirectoryName }
}
if (!$FFmpegFolder) { throw 'Pass -FFmpegFolder with a Windows FFmpeg build containing ffmpeg.exe, ffprobe.exe, and ffplay.exe.' }
foreach ($tool in 'ffmpeg.exe', 'ffprobe.exe', 'ffplay.exe') {
    if (!(Test-Path -LiteralPath (Join-Path $FFmpegFolder $tool) -PathType Leaf)) { throw "$tool is missing from the FFmpeg folder." }
}
$distribution = (Get-Item -LiteralPath $FFmpegFolder).Parent.FullName
$license = Join-Path $distribution 'LICENSE'
$readme = Join-Path $distribution 'README.txt'
if (!(Test-Path -LiteralPath $license) -or !(Test-Path -LiteralPath $readme)) {
    throw 'Use the FFmpeg distribution with its original LICENSE and README.txt in the parent folder.'
}
$filters = & (Join-Path $FFmpegFolder 'ffmpeg.exe') -hide_banner -filters
if ($LASTEXITCODE -ne 0 -or !($filters -match '\bgfxcapture\b')) { throw 'The FFmpeg build must support Windows Graphics Capture (gfxcapture).' }

$staging = Join-Path $env:TEMP ('clippings-portable-' + [Guid]::NewGuid().ToString('N'))
$bundle = Join-Path $staging $bundleName
$tools = Join-Path $bundle 'ffmpeg'
$pending = Join-Path (Split-Path -Parent $ZipPath) ('.clippings-' + [Guid]::NewGuid().ToString('N') + '.zip')
try {
    New-Item -ItemType Directory -Path $tools -Force | Out-Null
    Copy-Item -LiteralPath $NAudioPath -Destination (Join-Path $bundle 'NAudio.dll')
    foreach ($tool in 'ffmpeg.exe', 'ffprobe.exe', 'ffplay.exe') {
        Copy-Item -LiteralPath (Join-Path $FFmpegFolder $tool) -Destination $tools
    }
    Copy-Item -LiteralPath $license,$readme -Destination $tools
    Set-Content -LiteralPath (Join-Path $bundle 'portable.mode') -Encoding ASCII -Value 'clippings portable'
    Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'PORTABLE.txt') -Destination $bundle

    $compiler = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
    $sources = Get-ChildItem -LiteralPath $PSScriptRoot -Filter '*.cs' | Select-Object -ExpandProperty FullName
    & $compiler /nologo /target:winexe /platform:x64 /optimize+ "/win32icon:$PSScriptRoot\clippings.ico" "/out:$bundle\clippings.exe" /reference:System.Windows.Forms.dll /reference:System.Drawing.dll /reference:Microsoft.VisualBasic.dll /reference:System.Management.dll "/reference:$bundle\NAudio.dll" @sources
    if ($LASTEXITCODE -ne 0) { throw 'Portable build failed.' }

    tar.exe -a -cf $pending -C $staging $bundleName
    if ($LASTEXITCODE -ne 0) { throw 'Could not create the portable ZIP.' }
    $entries = tar.exe -tf $pending
    if ($LASTEXITCODE -ne 0) { throw 'Could not verify the portable ZIP.' }
    foreach ($name in 'clippings.exe', 'portable.mode', 'NAudio.dll', 'ffmpeg/ffmpeg.exe', 'ffmpeg/ffprobe.exe', 'ffmpeg/ffplay.exe') {
        if ($entries -notcontains "$bundleName/$name") { throw "The ZIP is missing $name." }
    }
    [IO.File]::Move($pending,$ZipPath)
    Get-Item -LiteralPath $ZipPath | Select-Object FullName,Length
}
finally {
    if (Test-Path -LiteralPath $pending) { Remove-Item -LiteralPath $pending -Force }
    if (Test-Path -LiteralPath $staging) { Remove-Item -LiteralPath $staging -Recurse -Force }
}
