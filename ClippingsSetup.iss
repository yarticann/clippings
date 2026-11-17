[Setup]
AppId=Clippings
AppName=clippings
AppVersion={#AppVersion}
AppPublisher=yarticann
DefaultDirName={code:DefaultInstallDir}
UsePreviousAppDir=no
DisableDirPage=no
DisableProgramGroupPage=yes
PrivilegesRequired=lowest
ArchitecturesAllowed=x64compatible
AppMutex=Local\ReplayShelf
SetupMutex=Local\ClippingsSetup
CloseApplications=no
SetupIconFile=clippings.ico
UninstallDisplayIcon={app}\clippings.exe
OutputBaseFilename={#OutputName}
WizardStyle=modern
Compression=lzma2
SolidCompression=yes

[Files]
Source: "{#PayloadDir}\clippings.exe"; DestDir: "{app}"; Flags: ignoreversion
Source: "{#PayloadDir}\NAudio.dll"; DestDir: "{app}"; Flags: ignoreversion
Source: "{#PayloadDir}\NAudio-LICENSE.txt"; DestDir: "{app}"; Flags: ignoreversion

[Tasks]
Name: "desktopicon"; Description: "Create a desktop shortcut"; Flags: unchecked
Name: "startup"; Description: "Open clippings in the tray at Windows sign-in"; Flags: unchecked

[Icons]
Name: "{userprograms}\clippings"; Filename: "{app}\clippings.exe"; WorkingDir: "{app}"
Name: "{userdesktop}\clippings"; Filename: "{app}\clippings.exe"; WorkingDir: "{app}"; Tasks: desktopicon
Name: "{userstartup}\clippings"; Filename: "{app}\clippings.exe"; WorkingDir: "{app}"; Parameters: "--tray"; Tasks: startup

[Run]
Filename: "{app}\clippings.exe"; Description: "Open clippings"; Flags: postinstall nowait skipifsilent

[Code]
var
  FfmpegDir: String;

function DefaultInstallDir(Param: String): String;
var
  Shell, Shortcut: Variant;
  Link, Target, Legacy: String;
begin
  Result := ExpandConstant('{localappdata}\clippings');
  Link := ExpandConstant('{userprograms}\clippings.lnk');
  if FileExists(Link) then
  begin
    try
      Shell := CreateOleObject('WScript.Shell');
      Shortcut := Shell.CreateShortcut(Link);
      Target := Shortcut.TargetPath;
      if (CompareText(ExtractFileName(Target), 'clippings.exe') = 0) and FileExists(Target) then
      begin
        Result := ExtractFileDir(Target);
        Exit;
      end;
    except
      Log('Could not read the existing Clippings Start menu shortcut.');
    end;
  end;
  if FileExists(AddBackslash(Result) + 'clippings.exe') then Exit;
  Legacy := ExpandConstant('{localappdata}\ReplayShelf');
  if FileExists(Legacy + '\clippings.exe') or FileExists(Legacy + '\ReplayShelf.exe') then
    Result := Legacy;
end;

procedure InitializeWizard;
var
  Tasks: String;
begin
  Tasks := '';
  if FileExists(ExpandConstant('{userstartup}\clippings.lnk')) or
     FileExists(ExpandConstant('{userstartup}\Replay Shelf.lnk')) then
    Tasks := 'startup';
  if FileExists(ExpandConstant('{userdesktop}\clippings.lnk')) then
  begin
    if Tasks <> '' then Tasks := Tasks + ',';
    Tasks := Tasks + 'desktopicon';
  end;
  if Tasks <> '' then WizardSelectTasks(Tasks);
end;

function HasFfmpeg(Dir: String): Boolean;
begin
  Result := (Dir <> '') and FileExists(AddBackslash(Dir) + 'ffmpeg.exe') and
    FileExists(AddBackslash(Dir) + 'ffprobe.exe') and FileExists(AddBackslash(Dir) + 'ffplay.exe');
end;

function FindFfmpeg: String;
var
  Candidate, Packages, PackageDir, Links: String;
  Package, Distribution: TFindRec;
  Saved: TArrayOfString;
begin
  Result := '';
  if LoadStringsFromFile(AddBackslash(WizardDirValue) + 'ffmpeg-path.txt', Saved) and (GetArrayLength(Saved) > 0) then
  begin
    Candidate := Trim(Saved[0]);
    if HasFfmpeg(Candidate) then
    begin
      Result := Candidate;
      Exit;
    end;
  end;
  Candidate := ExtractFileDir(FileSearch('ffmpeg.exe', GetEnv('PATH')));
  if HasFfmpeg(Candidate) then
  begin
    Result := Candidate;
    Exit;
  end;
  Links := ExpandConstant('{localappdata}\Microsoft\WinGet\Links');
  if HasFfmpeg(Links) then
  begin
    Result := Links;
    Exit;
  end;
  Packages := ExpandConstant('{localappdata}\Microsoft\WinGet\Packages');
  if FindFirst(Packages + '\Gyan.FFmpeg_*', Package) then
  begin
    try
      repeat
        PackageDir := Packages + '\' + Package.Name;
        if FindFirst(PackageDir + '\ffmpeg-*', Distribution) then
        begin
          try
            repeat
              Candidate := PackageDir + '\' + Distribution.Name + '\bin';
              if HasFfmpeg(Candidate) then
              begin
                Result := Candidate;
                Exit;
              end;
            until not FindNext(Distribution);
          finally
            FindClose(Distribution);
          end;
        end;
      until not FindNext(Package);
    finally
      FindClose(Package);
    end;
  end;
end;

function PrepareToInstall(var NeedsRestart: Boolean): String;
var
  Winget: String;
  ExitCode: Integer;
begin
  Result := '';
  if FileExists(AddBackslash(WizardDirValue) + 'portable.mode') then
  begin
    Result := 'Choose a folder outside your portable copy of clippings.';
    Exit;
  end;
  FfmpegDir := FindFfmpeg;
  if FfmpegDir <> '' then Exit;

  Winget := ExpandConstant('{localappdata}\Microsoft\WindowsApps\winget.exe');
  if not FileExists(Winget) then Winget := FileSearch('winget.exe', GetEnv('PATH'));
  if Winget = '' then
  begin
    Result := 'Windows Package Manager was unavailable. Install the full Gyan FFmpeg package and run Setup again.';
    Exit;
  end;
  if not Exec(Winget, 'install --id Gyan.FFmpeg --exact --source winget --scope user --force --silent --disable-interactivity --accept-source-agreements --accept-package-agreements',
    '', SW_HIDE, ewWaitUntilTerminated, ExitCode) then
  begin
    Result := 'Setup could not start Windows Package Manager.';
    Exit;
  end;
  FfmpegDir := FindFfmpeg;
  if FfmpegDir = '' then
    Result := Format('Setup could not find ffmpeg, ffprobe and ffplay after installing FFmpeg (exit code %d).', [ExitCode]);
end;

procedure CurStepChanged(CurStep: TSetupStep);
var
  PathLines: TArrayOfString;
begin
  if CurStep <> ssPostInstall then Exit;
  SetArrayLength(PathLines, 1);
  PathLines[0] := FfmpegDir;
  if not SaveStringsToUTF8File(ExpandConstant('{app}\ffmpeg-path.txt'), PathLines, False) then
    RaiseException('Setup could not save the FFmpeg path.');
  DeleteFile(ExpandConstant('{userprograms}\Replay Shelf.lnk'));
  DeleteFile(ExpandConstant('{userstartup}\Replay Shelf.lnk'));
  if not WizardIsTaskSelected('startup') then DeleteFile(ExpandConstant('{userstartup}\clippings.lnk'));
  if not WizardIsTaskSelected('desktopicon') then DeleteFile(ExpandConstant('{userdesktop}\clippings.lnk'));
end;