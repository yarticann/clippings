param([string]$OutputPath, [string]$CompilerPath)

$ErrorActionPreference = 'Stop'
$diagnostics = Get-Content -LiteralPath (Join-Path $PSScriptRoot 'Diagnostics.cs') -Raw
$release = [regex]::Match($diagnostics, 'AssemblyInformationalVersion\("v(?<version>\d+\.\d{2})"\)')
if (!$release.Success) { throw 'Could not read the release version from Diagnostics.cs.' }
$version = $release.Groups['version'].Value
if (!$OutputPath) { $OutputPath = Join-Path (Get-Location).Path "clippings v$version Setup.exe" }
$OutputPath = [IO.Path]::GetFullPath($OutputPath)
if ([IO.Path]::GetExtension($OutputPath) -ne '.exe' -or (Test-Path -LiteralPath $OutputPath)) {
    throw 'Choose an unused output filename ending in .exe.'
}

if (!$CompilerPath) {
    $CompilerPath = Join-Path $env:LOCALAPPDATA 'Programs\Inno Setup 6\ISCC.exe'
    if (!(Test-Path -LiteralPath $CompilerPath -PathType Leaf)) {
        $CompilerPath = Join-Path ${env:ProgramFiles(x86)} 'Inno Setup 6\ISCC.exe'
    }
}
if (!(Test-Path -LiteralPath $CompilerPath -PathType Leaf)) {
    throw 'Install Inno Setup 6 or supply the path to ISCC.exe.'
}

$stage = Join-Path $env:TEMP ('clippings-build-' + [guid]::NewGuid().ToString('N'))
try {
    New-Item -ItemType Directory $stage | Out-Null
    $package = Join-Path $stage 'NAudio.zip'
    $naudio = Join-Path $stage 'NAudio.dll'
    Invoke-WebRequest -UseBasicParsing 'https://api.nuget.org/v3-flatcontainer/naudio/1.10.0/naudio.1.10.0.nupkg' -OutFile $package
    Add-Type -AssemblyName System.IO.Compression.FileSystem
    $archive = [IO.Compression.ZipFile]::OpenRead($package)
    try {
        [IO.Compression.ZipFileExtensions]::ExtractToFile($archive.GetEntry('lib/net35/NAudio.dll'), $naudio, $false)
    } finally { $archive.Dispose() }
    $license = Join-Path $stage 'NAudio-LICENSE.txt'
    Invoke-WebRequest -UseBasicParsing 'https://raw.githubusercontent.com/naudio/NAudio/f3c2a15379731239363fd0c26d6b5fe8162a5087/license.txt' -OutFile $license
    if ((Get-FileHash -LiteralPath $license).Hash -ne '0027E744E6750BC02408B90C6909A7A33093477083D59A7E2660477E05307689') {
        throw 'NAudio license did not match the expected release.'
    }
    $compiler = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
    $files = 'ReplayShelf.cs','Settings.cs','VideoSettings.cs','ClipActions.cs','Feedback.cs','DisplaySize.cs',
        'Onboarding.cs','ClipStorage.cs','Diagnostics.cs','DialogLayout.cs'
    $sources = $files | ForEach-Object { Join-Path $PSScriptRoot $_ }
    $app = Join-Path $stage 'clippings.exe'
    & $compiler /nologo /target:winexe /platform:x64 /optimize+ "/out:$app" "/win32icon:$PSScriptRoot\clippings.ico" /reference:System.Windows.Forms.dll /reference:System.Drawing.dll /reference:Microsoft.VisualBasic.dll /reference:System.Management.dll "/reference:$naudio" @sources
    if ($LASTEXITCODE -ne 0) { throw 'Clippings compilation failed.' }

    & $CompilerPath "/DAppVersion=$version" "/DPayloadDir=$stage" '/DOutputName=ClippingsSetup' "/O$stage" (Join-Path $PSScriptRoot 'ClippingsSetup.iss')
    if ($LASTEXITCODE -ne 0) { throw 'Inno Setup compilation failed.' }
    [IO.File]::Copy((Join-Path $stage 'ClippingsSetup.exe'), $OutputPath, $false)
    Get-Item -LiteralPath $OutputPath | Select-Object FullName,Length
} finally {
    if (Test-Path -LiteralPath $stage) { Remove-Item -LiteralPath $stage -Recurse -Force }
}