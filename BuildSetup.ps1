param([string]$OutputPath)

$ErrorActionPreference = 'Stop'
$diagnostics = Get-Content -LiteralPath (Join-Path $PSScriptRoot 'Diagnostics.cs') -Raw
$release = [regex]::Match($diagnostics, 'AssemblyInformationalVersion\("(?<label>v(?<version>\d+\.\d{2}))"\)')
if (!$release.Success) { throw 'Could not read the release version from Diagnostics.cs.' }
$label = $release.Groups['label'].Value
$version = $release.Groups['version'].Value
if (!$OutputPath) { $OutputPath = Join-Path (Get-Location).Path "clippings $label Setup.exe" }
$OutputPath = [IO.Path]::GetFullPath($OutputPath)
if ([IO.Path]::GetExtension($OutputPath) -ne '.exe') { throw 'Choose an output filename ending in .exe.' }

$staging = Join-Path $env:TEMP ('clippings-setup-' + [Guid]::NewGuid().ToString('N'))
try {
    New-Item -ItemType Directory -Path $staging | Out-Null
    $payload = Join-Path $staging 'setup.zip'
    $files = Get-ChildItem -LiteralPath $PSScriptRoot -Filter '*.cs' -File | Select-Object -ExpandProperty FullName
    $files += 'Setup.ps1', 'SetupWizard.ps1', 'SetupPaths.ps1', 'clippings.ico' | ForEach-Object { Join-Path $PSScriptRoot $_ }
    Compress-Archive -LiteralPath $files -DestinationPath $payload

    $launcher = Join-Path $staging 'Launcher.cs'
    @"
using System;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Reflection;
using System.Windows.Forms;

[assembly: AssemblyProduct("clippings Setup")]
[assembly: AssemblyVersion("$version.0.0")]
[assembly: AssemblyFileVersion("$version.0.0")]
[assembly: AssemblyInformationalVersion("$label")]

static class Launcher
{
    [STAThread]
    static int Main()
    {
        string work = Path.Combine(Path.GetTempPath(), "clippings-setup-" + Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(work);
            string payload = Path.Combine(work, "setup.zip");
            using (Stream resource = Assembly.GetExecutingAssembly().GetManifestResourceStream("setup.zip"))
            using (Stream output = File.Create(payload))
                resource.CopyTo(output);

            string files = Path.Combine(work, "files");
            ZipFile.ExtractToDirectory(payload, files);
            string powershell = Path.Combine(Environment.SystemDirectory, @"WindowsPowerShell\v1.0\powershell.exe");
            string arguments = "-NoProfile -STA -ExecutionPolicy Bypass -File \"" + Path.Combine(files, "SetupWizard.ps1") + "\"";
            using (Process wizard = Process.Start(new ProcessStartInfo(powershell, arguments)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                WorkingDirectory = files
            }))
            {
                wizard.WaitForExit();
                if (wizard.ExitCode != 0) throw new InvalidOperationException("Setup exited with code " + wizard.ExitCode + ".");
            }
            return 0;
        }
        catch (Exception error)
        {
            MessageBox.Show(error.Message, "clippings Setup", MessageBoxButtons.OK, MessageBoxIcon.Error);
            return 1;
        }
        finally
        {
            try { if (Directory.Exists(work)) Directory.Delete(work, true); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }
}
"@ | Set-Content -LiteralPath $launcher -Encoding ASCII

    $compiler = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
    $candidate = Join-Path $staging 'Setup.exe'
    & $compiler /nologo /target:winexe /platform:x64 /optimize+ "/out:$candidate" "/win32icon:$PSScriptRoot\clippings.ico" "/resource:$payload,setup.zip" /reference:System.Windows.Forms.dll /reference:System.IO.Compression.FileSystem.dll $launcher
    if ($LASTEXITCODE -ne 0) { throw 'Could not compile the setup launcher.' }
    [IO.File]::Copy($candidate, $OutputPath, $false)
    Get-Item -LiteralPath $OutputPath | Select-Object FullName,Length
}
finally {
    if (Test-Path -LiteralPath $staging) { Remove-Item -LiteralPath $staging -Recurse -Force }
}
