$clippingsData = Join-Path $env:LOCALAPPDATA 'ReplayShelf'
$clippingsMenuLink = Join-Path ([Environment]::GetFolderPath('Programs')) 'clippings.lnk'

function Get-ClippingsInstallPath {
    if (Test-Path -LiteralPath $clippingsMenuLink -PathType Leaf) {
        $shortcut = (New-Object -ComObject WScript.Shell).CreateShortcut($clippingsMenuLink)
        if ([IO.Path]::GetFileName($shortcut.TargetPath) -eq 'clippings.exe' -and
            (Test-Path -LiteralPath $shortcut.TargetPath -PathType Leaf)) {
            return [IO.Path]::GetDirectoryName($shortcut.TargetPath)
        }
    }
    return $clippingsData
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
