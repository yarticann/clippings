$defaultClippingsInstall = Join-Path $env:LOCALAPPDATA 'clippings'
$legacyClippingsInstall = Join-Path $env:LOCALAPPDATA 'ReplayShelf'
$clippingsStateFiles = 'settings.xml', 'setup-seen', 'hotkey.txt'
$clippingsMenuLink = Join-Path ([Environment]::GetFolderPath('Programs')) 'clippings.lnk'

function Get-ClippingsInstallPath {
    if (Test-Path -LiteralPath $clippingsMenuLink -PathType Leaf) {
        $shortcut = (New-Object -ComObject WScript.Shell).CreateShortcut($clippingsMenuLink)
        if ([IO.Path]::GetFileName($shortcut.TargetPath) -eq 'clippings.exe' -and
            (Test-Path -LiteralPath $shortcut.TargetPath -PathType Leaf)) {
            return [IO.Path]::GetDirectoryName($shortcut.TargetPath)
        }
    }
    foreach ($candidate in @(
        (Join-Path $defaultClippingsInstall 'clippings.exe'),
        (Join-Path $legacyClippingsInstall 'clippings.exe'),
        (Join-Path $legacyClippingsInstall 'ReplayShelf.exe')
    )) {
        if (Test-Path -LiteralPath $candidate -PathType Leaf) {
            return [IO.Path]::GetDirectoryName($candidate)
        }
    }
    return $defaultClippingsInstall
}

function Test-ClippingsUpgrade([string]$install) {
    foreach ($name in 'clippings.exe', 'ReplayShelf.exe') {
        if (Test-Path -LiteralPath (Join-Path $install $name) -PathType Leaf) { return $true }
    }
    foreach ($folder in $defaultClippingsInstall, $legacyClippingsInstall) {
        foreach ($name in $clippingsStateFiles) {
            if (Test-Path -LiteralPath (Join-Path $folder $name) -PathType Leaf) { return $true }
        }
    }
    return $false
}

function Resolve-ClippingsInstallPath([string]$value) {
    $value = [Environment]::ExpandEnvironmentVariables($value.Trim())
    if ($value -notmatch '^(?:[A-Za-z]:[\\/]|\\\\[^\\/]+[\\/][^\\/]+)') {
        throw 'Enter a full path or choose an install folder with Browse.'
    }
    $path = [IO.Path]::GetFullPath($value)
    if ((Test-Path -LiteralPath $path -PathType Leaf) -or
        (Test-Path -LiteralPath (Join-Path $path 'portable.mode') -PathType Leaf)) {
        throw 'Choose a folder that is not a file or a Clippings portable installation.'
    }
    return $path
}
