param([string]$InstallPath, [switch]$NoLaunch, [switch]$ConfigureShortcuts, [switch]$DesktopShortcut, [switch]$StartAtSignIn)

$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'SetupPaths.ps1')
$previousInstall = Get-ClippingsInstallPath
$install = if ($InstallPath) { Resolve-ClippingsInstallPath $InstallPath } else { $previousInstall }
$exe = Join-Path $install 'clippings.exe'
$updating = (Test-Path -LiteralPath $exe) -or (Test-Path -LiteralPath (Join-Path $previousInstall 'clippings.exe')) -or
    (Test-Path -LiteralPath (Join-Path $clippingsData 'ReplayShelf.exe')) -or
    (Test-Path -LiteralPath (Join-Path $clippingsData 'settings.xml')) -or
    (Test-Path -LiteralPath (Join-Path $clippingsData 'setup-seen')) -or
    (Test-Path -LiteralPath (Join-Path $clippingsData 'hotkey.txt'))
New-Item -ItemType Directory -Force -Path $install | Out-Null
if (Get-Process clippings,ReplayShelf -ErrorAction SilentlyContinue) {
    throw 'Exit clippings from the system tray before running Setup again.'
}

function Find-FfmpegFolder {
    $commands = Get-Command ffmpeg.exe -CommandType Application -All -ErrorAction SilentlyContinue
    $packages = Join-Path $env:LOCALAPPDATA 'Microsoft\WinGet\Packages'
    $folders = @($commands | ForEach-Object { Split-Path -Parent $_.Source })
    $folders += @(Get-ChildItem -LiteralPath $packages -Directory -Filter 'Gyan.FFmpeg_*' -ErrorAction SilentlyContinue |
        ForEach-Object { Get-ChildItem -LiteralPath $_.FullName -File -Filter ffmpeg.exe -Recurse -ErrorAction SilentlyContinue } |
        ForEach-Object DirectoryName)
    foreach ($folder in $folders | Select-Object -Unique) {
        if ((Test-Path -LiteralPath (Join-Path $folder 'ffmpeg.exe') -PathType Leaf) -and
            (Test-Path -LiteralPath (Join-Path $folder 'ffprobe.exe') -PathType Leaf) -and
            (Test-Path -LiteralPath (Join-Path $folder 'ffplay.exe') -PathType Leaf)) {
            return $folder
        }
    }
}

$ffFolder = Find-FfmpegFolder
if (!$ffFolder) {
    if (!(Get-Command winget -ErrorAction SilentlyContinue)) {
        throw 'Windows Package Manager is unavailable. Install a full FFmpeg package with ffmpeg.exe, ffprobe.exe, and ffplay.exe, then run Setup again.'
    }
    winget install --id Gyan.FFmpeg --exact --scope user --force --accept-source-agreements --accept-package-agreements
    $wingetExit = $LASTEXITCODE
    $ffFolder = Find-FfmpegFolder
    if (!$ffFolder) {
        throw "Setup could not find ffmpeg.exe, ffprobe.exe, and ffplay.exe in one folder after running WinGet (exit code $wingetExit). Install the full Gyan.FFmpeg package and run Setup again."
    }
}

if (!(Test-Path (Join-Path $install 'NAudio.dll'))) {
$package = Join-Path $env:TEMP ('ReplayShelf-' + [Guid]::NewGuid().ToString('N') + '.zip')
try {
    Invoke-WebRequest -UseBasicParsing 'https://api.nuget.org/v3-flatcontainer/naudio/1.10.0/naudio.1.10.0.nupkg' -OutFile $package
    Add-Type -AssemblyName System.IO.Compression.FileSystem
    $zip = [IO.Compression.ZipFile]::OpenRead($package)
    try {
        [IO.Compression.ZipFileExtensions]::ExtractToFile(
            $zip.GetEntry('lib/net35/NAudio.dll'), (Join-Path $install 'NAudio.dll'), $true)
    } finally { $zip.Dispose() }
} finally { Remove-Item $package -ErrorAction SilentlyContinue }
}

$compiler = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
$candidate = Join-Path $install 'clippings.setup.exe'
$references = @('/reference:System.Windows.Forms.dll', '/reference:System.Drawing.dll', '/reference:Microsoft.VisualBasic.dll', '/reference:System.Management.dll', "/reference:$install\NAudio.dll")
& $compiler "/win32icon:$PSScriptRoot\clippings.ico" /nologo /target:winexe /platform:x64 /optimize+ "/out:$candidate" @references (Join-Path $PSScriptRoot 'ReplayShelf.cs') (Join-Path $PSScriptRoot 'Settings.cs') (Join-Path $PSScriptRoot 'VideoSettings.cs') (Join-Path $PSScriptRoot 'ClipActions.cs') (Join-Path $PSScriptRoot 'Feedback.cs') (Join-Path $PSScriptRoot 'DisplaySize.cs') (Join-Path $PSScriptRoot 'Onboarding.cs') (Join-Path $PSScriptRoot 'ClipStorage.cs') (Join-Path $PSScriptRoot 'Diagnostics.cs') (Join-Path $PSScriptRoot 'DialogLayout.cs')
if ($LASTEXITCODE -ne 0) { throw 'clippings compilation failed.' }
if (Test-Path $exe) { [IO.File]::Replace($candidate,$exe,(Join-Path $install 'clippings.previous.exe')) }
else { [IO.File]::Move($candidate,$exe) }
Set-Content -LiteralPath (Join-Path $install 'ffmpeg-path.txt') -Value $ffFolder -Encoding UTF8

$menu = [IO.Path]::GetDirectoryName($clippingsMenuLink)
$linkPath = $clippingsMenuLink
$link = (New-Object -ComObject WScript.Shell).CreateShortcut($linkPath)
$link.TargetPath = $exe
$link.IconLocation = "$exe,0"
$link.WorkingDirectory = $install
$link.Save()
$oldLink = Join-Path $menu "Replay Shelf.lnk"
if (Test-Path $oldLink) { Remove-Item -LiteralPath $oldLink }
$startup = [Environment]::GetFolderPath("Startup")
$oldStartup = Join-Path $startup "Replay Shelf.lnk"
$newStartup = Join-Path $startup "clippings.lnk"
if ($StartAtSignIn -or (!$ConfigureShortcuts -and ((Test-Path $oldStartup) -or (Test-Path $newStartup)))) {
    $link = (New-Object -ComObject WScript.Shell).CreateShortcut($newStartup)
    $link.TargetPath = $exe
    $link.IconLocation = "$exe,0"
    $link.Arguments = "--tray"
    $link.WorkingDirectory = $install
    $link.Save()
    if (Test-Path $oldStartup) { Remove-Item -LiteralPath $oldStartup }
}
if ($ConfigureShortcuts -and !$StartAtSignIn) {
    foreach ($entry in @($oldStartup,$newStartup)) { if (Test-Path $entry) { Remove-Item -LiteralPath $entry } }
}
if ($ConfigureShortcuts) {
    $desktopLink = Join-Path ([Environment]::GetFolderPath('DesktopDirectory')) 'clippings.lnk'
    if ($DesktopShortcut) {
        $link = (New-Object -ComObject WScript.Shell).CreateShortcut($desktopLink)
        $link.TargetPath = $exe
        $link.IconLocation = "$exe,0"
        $link.WorkingDirectory = $install
        $link.Save()
    } elseif (Test-Path $desktopLink) { Remove-Item -LiteralPath $desktopLink }
}
if (!$NoLaunch) {
    if (!$updating) { Start-Process $exe -ArgumentList '--setup-guide' }
    else { Start-Process $exe }
}
