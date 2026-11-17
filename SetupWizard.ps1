$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Windows.Forms,System.Drawing
[Windows.Forms.Application]::EnableVisualStyles()
. (Join-Path $PSScriptRoot 'SetupPaths.ps1')
$install = Get-ClippingsInstallPath
$updating = Test-ClippingsUpgrade $install
$desktopLink = Join-Path ([Environment]::GetFolderPath('DesktopDirectory')) 'clippings.lnk'
$startupFolder = [Environment]::GetFolderPath('Startup')
$form = New-Object Windows.Forms.Form
$form.Text = 'clippings setup'
$form.ClientSize = [Drawing.Size]::new(590, 340)
$form.StartPosition = 'CenterScreen'
$form.FormBorderStyle = 'Sizable'
$form.MinimumSize = [Drawing.Size]::new(450, 380)
$form.SizeGripStyle = 'Show'
$form.MaximizeBox = $true
$form.Icon = [Drawing.Icon]::new((Join-Path $PSScriptRoot 'clippings.ico'))
$form.BackColor = [Drawing.Color]::FromArgb(31,17,48)
$form.ForeColor = [Drawing.Color]::FromArgb(247,239,255)
$form.Font = [Drawing.Font]::new('Segoe UI',11)
$heading = New-Object Windows.Forms.Label
$heading.Text = if ($updating) { 'Update clippings' } else { 'Install clippings' }
$heading.Font = [Drawing.Font]::new('Segoe UI',20,[Drawing.FontStyle]::Bold)
$heading.SetBounds(22,17,540,45)
$locationLabel = New-Object Windows.Forms.Label
$locationLabel.Text = 'Install location'
$locationLabel.SetBounds(24,66,400,26)
$path = New-Object Windows.Forms.TextBox
$path.Text = $install
$path.AccessibleName = 'Install location'
$path.SetBounds(24,96,430,28)
$browse = New-Object Windows.Forms.Button
$browse.Text = 'Browse...'
$browse.FlatStyle = 'Flat'
$browse.SetBounds(466,95,95,30)
$browse.Anchor = [Windows.Forms.AnchorStyles]'Top, Right'
$browse.Add_Click({
    $picker = New-Object Windows.Forms.FolderBrowserDialog
    try {
        $picker.Description = 'Choose where to install clippings.'
        if (Test-Path -LiteralPath $path.Text -PathType Container) { $picker.SelectedPath = $path.Text }
        if ($picker.ShowDialog($form) -eq 'OK') { $path.Text = $picker.SelectedPath }
    } finally { $picker.Dispose() }
})
$desktop = New-Object Windows.Forms.CheckBox
$desktop.Text = 'Create a desktop shortcut'
$desktop.Checked = Test-Path $desktopLink
$desktop.SetBounds(24,140,535,32)
$startup = New-Object Windows.Forms.CheckBox
$startup.Text = 'Open in the tray at Windows sign-in'
$startup.Checked = (Test-Path (Join-Path $startupFolder 'clippings.lnk')) -or (Test-Path (Join-Path $startupFolder 'Replay Shelf.lnk'))
$startup.SetBounds(24,176,535,32)
$status = New-Object Windows.Forms.Label
$status.Text = if ($updating) { 'Your clip folder and capture settings will carry over.' } else { 'The capture guide opens after installation.' }
$status.SetBounds(24,216,535,58)
$installButton = New-Object Windows.Forms.Button
$installButton.Text = if ($updating) { 'Update' } else { 'Install' }
$installButton.FlatStyle = 'Flat'
$installButton.SetBounds(421,288,140,34)
$cancel = New-Object Windows.Forms.Button
$cancel.Text = 'Cancel'
$cancel.FlatStyle = 'Flat'
$cancel.DialogResult = 'Cancel'
$cancel.SetBounds(301,288,108,34)
foreach ($control in @($heading,$locationLabel,$path,$desktop,$startup,$status)) {
    $control.Anchor = [Windows.Forms.AnchorStyles]'Top, Left, Right'
}
foreach ($control in @($installButton,$cancel)) {
    $control.Anchor = [Windows.Forms.AnchorStyles]'Bottom, Right'
}
$form.Controls.AddRange(@($heading,$locationLabel,$path,$browse,$desktop,$startup,$status,$installButton,$cancel))
$form.AcceptButton = $installButton
$form.CancelButton = $cancel
$timer = New-Object Windows.Forms.Timer
$timer.Interval = 200
$script:installer = $null
$script:installLog = $null
$script:installErrors = $null
$script:installed = $false
$timer.Add_Tick({
    if (!$script:installer -or !$script:installer.HasExited) { return }
    $timer.Stop()
    $script:installer.WaitForExit()
    $code = $script:installer.ExitCode
    $script:installer.Dispose()
    $script:installer = $null
    if ($code -eq 0) {
        $script:installed = $true
        $status.Text = 'clippings is ready.'
        $installButton.Text = 'Open clippings'
        $cancel.Text = 'Close'
    } else {
        $failure = (Get-Content $script:installErrors -ErrorAction SilentlyContinue | Select-Object -First 1)
        $status.Text = if ($failure) { $failure } else { if ($null -eq $code) { "Setup ended without reporting a result. Try running Setup again." } else { "Setup failed with exit code $code." } }
        $path.Enabled = $browse.Enabled = $desktop.Enabled = $startup.Enabled = $true
    }
    $installButton.Enabled = $cancel.Enabled = $true
})
$installButton.Add_Click({
    if ($script:installed) {
        $installedExe = Join-Path $script:chosenInstall 'clippings.exe'
        if ($updating) { Start-Process $installedExe } else { Start-Process $installedExe -ArgumentList '--setup-guide' }
        $form.Close()
        return
    }
    if (Get-Process clippings,ReplayShelf -ErrorAction SilentlyContinue) {
        $status.Text = 'Exit clippings from its tray menu first, then choose Update.'
        return
    }
    try { $script:chosenInstall = Resolve-ClippingsInstallPath $path.Text }
    catch { $status.Text = $_.Exception.Message; return }
    $path.Text = $script:chosenInstall
    foreach ($log in @($script:installLog,$script:installErrors)) {
        if ($log) { Remove-Item -LiteralPath $log -ErrorAction SilentlyContinue }
    }
    $script:installLog = [IO.Path]::GetTempFileName()
    $script:installErrors = [IO.Path]::GetTempFileName()
    $arguments = '-NoProfile -ExecutionPolicy Bypass -File "' + (Join-Path $PSScriptRoot 'Setup.ps1') + '" -NoLaunch -ConfigureShortcuts'
    $arguments += ' -InstallPath "' + $script:chosenInstall.TrimEnd('\','/') + '\."'
    if ($desktop.Checked) { $arguments += ' -DesktopShortcut' }
    if ($startup.Checked) { $arguments += ' -StartAtSignIn' }
    try {
        $script:installer = Start-Process powershell.exe -ArgumentList $arguments -WindowStyle Hidden -PassThru -RedirectStandardOutput $script:installLog -RedirectStandardError $script:installErrors
        $status.Text = 'Installing clippings and checking dependencies…'
        $null = $script:installer.Handle
        $path.Enabled = $browse.Enabled = $desktop.Enabled = $startup.Enabled = $installButton.Enabled = $cancel.Enabled = $false
        $timer.Start()
    } catch { $status.Text = $_.Exception.Message }
})
$form.Add_FormClosing({ param($sender,$e) if ($script:installer -and !$script:installer.HasExited) { $e.Cancel = $true } })
try { [void]$form.ShowDialog() }
finally {
    $timer.Dispose()
    $form.Dispose()
    foreach($tempPath in @($script:installLog,$script:installErrors)) {
        if($tempPath) { Remove-Item -LiteralPath $tempPath -ErrorAction SilentlyContinue }
    }
}
