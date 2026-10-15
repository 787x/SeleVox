; Build only through scripts/Build-Installer.ps1. AppId is permanent.
[Setup]
AppId={{BDC7B521-DF8E-44A7-AD0A-4765EAC38629}
AppName=SeleVox
AppVersion={#Version}
AppVerName=SeleVox {#Version}
VersionInfoVersion={#NumericVersion}
VersionInfoProductTextVersion={#Version}
AppPublisher=SeleVox contributors
AppPublisherURL=https://github.com/787x/SeleVox
DefaultDirName={localappdata}\Programs\SeleVox
DefaultGroupName=SeleVox
DisableProgramGroupPage=yes
PrivilegesRequired=lowest
ArchitecturesAllowed=x64os
ArchitecturesInstallIn64BitMode=x64os
MinVersion=10.0.22000
OutputDir={#OutputDirectory}
OutputBaseFilename=SeleVox-{#VersionTag}-win-x64-setup
Compression=lzma2
SolidCompression=yes
WizardStyle=modern
LicenseFile={#PublishDirectory}\LICENSE
UninstallDisplayIcon={app}\SeleVox.App.exe
CloseApplications=no
RestartApplications=no
AllowCancelDuringInstall=no
SetupLogging=yes

[Tasks]
Name: desktopicon; Description: "Create a desktop shortcut"; Flags: unchecked

[Files]
Source: "{#PublishDirectory}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{userprograms}\SeleVox\SeleVox"; Filename: "{app}\SeleVox.App.exe"
Name: "{userprograms}\SeleVox\Uninstall SeleVox"; Filename: "{uninstallexe}"
Name: "{userdesktop}\SeleVox"; Filename: "{app}\SeleVox.App.exe"; Tasks: desktopicon

[Run]
Filename: "{app}\SeleVox.App.exe"; Description: "Run SeleVox"; Flags: nowait postinstall skipifsilent unchecked

[Code]
const
  RunKey = 'Software\Microsoft\Windows\CurrentVersion\Run';
  UninstallKey = 'Software\Microsoft\Windows\CurrentVersion\Uninstall\{BDC7B521-DF8E-44A7-AD0A-4765EAC38629}_is1';

function GetCurrentProcessId: LongWord;
  external 'GetCurrentProcessId@kernel32.dll stdcall';
function GetFileAttributes(Path: String): LongWord;
  external 'GetFileAttributesW@kernel32.dll stdcall';
function OpenFileExclusive(Path: String; Access, Share: LongWord; Security: Integer; Creation, Flags, Template: LongWord): Integer;
  external 'CreateFileW@kernel32.dll stdcall';
function CloseHandle(Handle: Integer): Boolean;
  external 'CloseHandle@kernel32.dll stdcall';

function AppCommand: String;
begin
  Result := '"' + ExpandConstant('{app}\SeleVox.App.exe') + '" --background';
end;

function IsSeleVoxCommand(Value: String): Boolean;
var Path: String;
begin
  Result := False;
  if (Length(Value) < 15) or (Length(Value) > 260) then Exit;
  if (Copy(Value, 1, 1) <> '"') or (Copy(Value, Length(Value)-13, 14) <> '" --background') then Exit;
  Path := Copy(Value, 2, Length(Value)-15);
  Result := (Pos('"', Path) = 0) and (Pos(#13, Path) = 0) and (Pos(#10, Path) = 0) and
    (CompareText(ExtractFileName(Path), 'SeleVox.App.exe') = 0) and
    (((Length(Path) >= 3) and (Copy(Path, 2, 2) = ':\')) or (Copy(Path, 1, 2) = '\\'));
end;

function BeginMaintenance: Boolean;
var Service, Process, Sid: Variant; Name: String;
begin
  Result := False;
  try
    Service := CreateOleObject('WbemScripting.SWbemLocator');
    Service := Service.ConnectServer('.', 'root\CIMV2');
    Process := Service.Get('Win32_Process.Handle="' + IntToStr(GetCurrentProcessId) + '"');
    if Process.GetOwnerSid(Sid) <> 0 then Exit;
    Name := 'Local\SeleVox-Maintenance-' + String(Sid);
    if CheckForMutexes(Name) then Exit;
    CreateMutex(Name);
    Result := True;
  except
    Log('Ownership verification failed: ' + GetExceptionMessage);
    Result := False;
  end;
end;

function InitializeSetup: Boolean;
begin
  Result := BeginMaintenance;
  if not Result then SuppressibleMsgBox('Another SeleVox maintenance operation is running or ownership cannot be verified.', mbError, MB_OK, IDOK);
end;

function ProcessesStopped: Boolean;
var Service, Processes, Process, SelfProcess, User, Domain, OtherUser, OtherDomain: Variant;
    I: Integer;
begin
  Result := False;
  try
    Service := CreateOleObject('WbemScripting.SWbemLocator');
    Service := Service.ConnectServer('.', 'root\CIMV2');
    SelfProcess := Service.Get('Win32_Process.Handle="' + IntToStr(GetCurrentProcessId) + '"');
    if SelfProcess.GetOwner(User, Domain) <> 0 then Exit;
    Processes := Service.ExecQuery('SELECT * FROM Win32_Process WHERE Name=''SeleVox.App.exe'' OR Name=''SeleVox.Cli.exe''');
    for I := 0 to Processes.Count - 1 do begin
      Process := Processes.ItemIndex(I);
      if Process.GetOwner(OtherUser, OtherDomain) <> 0 then Exit;
      if (CompareText(User, OtherUser) = 0) and (CompareText(Domain, OtherDomain) = 0) then Exit;
    end;
    Result := True;
  except
    Log('Ownership verification failed: ' + GetExceptionMessage);
    Result := False;
  end;
end;

function FilesUnlocked(Directory: String): Boolean;
var Find: TFindRec; Path: String; Handle: Integer;
begin
  Result := True;
  if FindFirst(AddBackslash(Directory) + '*', Find) then begin
    try
      repeat
        if (Find.Name <> '.') and (Find.Name <> '..') then begin
          Path := AddBackslash(Directory) + Find.Name;
          if (Find.Attributes and FILE_ATTRIBUTE_REPARSE_POINT) <> 0 then begin Result := False; Exit; end;
          if (Find.Attributes and FILE_ATTRIBUTE_DIRECTORY) <> 0 then begin
            if not FilesUnlocked(Path) then begin Result := False; Exit; end;
          end else begin
            Handle := OpenFileExclusive(Path, $80000000, 0, 0, 3, 0, 0);
            if Handle = -1 then begin Result := False; Exit; end;
            CloseHandle(Handle);
          end;
        end;
      until not FindNext(Find);
    finally FindClose(Find); end;
  end;
end;

function DirectoryIdentity(Path: String): String;
begin
  { Compare existing prefixes through their 8.3 identity, including when the
    final directory does not exist yet. This prevents short-name data aliases. }
  if DirExists(Path) then Result := GetShortName(Path)
  else if Length(Path) > 3 then
    Result := AddBackslash(DirectoryIdentity(ExtractFileDir(Path))) + ExtractFileName(Path)
  else Result := Path;
end;

function SafeDirectory: Boolean;
var Directory, DataDir, Parent, Previous, ProgramIdentity, DataIdentity: String; Find: TFindRec;
begin
  Directory := RemoveBackslashUnlessRoot(ExpandFileName(ExpandConstant('{app}')));
  DataDir := ExpandConstant('{localappdata}\SeleVox');
  ProgramIdentity := DirectoryIdentity(Directory);
  DataIdentity := DirectoryIdentity(DataDir);
  Result := False;
  if (Length(Directory) < 4) or (Copy(Directory, 1, 2) = '\\') or
    (Pos(AddBackslash(Lowercase(ProgramIdentity)), AddBackslash(Lowercase(DataIdentity))) = 1) or
    (Pos(AddBackslash(Lowercase(DataIdentity)), AddBackslash(Lowercase(ProgramIdentity))) = 1) then Exit;
  Parent := Directory;
  while Length(Parent) > 3 do begin
    if (Copy(Parent, Length(Parent), 1) = '.') or (Copy(Parent, Length(Parent), 1) = ' ') then Exit;
    if DirExists(Parent) and ((GetFileAttributes(Parent) and $400) <> 0) then Exit;
    Parent := ExtractFileDir(Parent);
  end;
  if DirExists(Directory) then begin
    if not RegQueryStringValue(HKCU, UninstallKey, 'InstallLocation', Previous) or
      (CompareText(DirectoryIdentity(RemoveBackslashUnlessRoot(Previous)), ProgramIdentity) <> 0) then begin
      if FindFirst(AddBackslash(Directory) + '*', Find) then begin
        try
          repeat
            if (Find.Name <> '.') and (Find.Name <> '..') then Exit;
          until not FindNext(Find);
        finally FindClose(Find); end;
      end;
    end;
  end;
  Result := True;
end;

#include PayloadVerification

function PrepareToInstall(var NeedsRestart: Boolean): String;
begin
  Result := '';
  if not SafeDirectory then Result := 'Choose an empty program directory outside user data (no junctions), or the existing SeleVox installation.'
  else if Length(AppCommand) > 260 then Result := 'Installation path exceeds the Windows startup command limit.'
  else if not ProcessesStopped then Result := 'Exit SeleVox from its tray menu and close CLI/MCP tasks before continuing. No process will be terminated. Process ownership must be verifiable.'
  else if not FilesUnlocked(ExpandConstant('{app}')) then Result := 'Installation files are in use or inaccessible. Close applications and retry.';
end;

procedure CurStepChanged(CurStep: TSetupStep);
var Existing: String;
begin
  if CurStep = ssPostInstall then begin
    { A user must never get a successful install after ignoring a file error. }
    VerifyPayload;
    if RegQueryStringValue(HKCU, RunKey, 'SeleVox', Existing) and IsSeleVoxCommand(Existing) and
      (Existing <> AppCommand) then
      if not RegWriteStringValue(HKCU, RunKey, 'SeleVox', AppCommand) then
        RaiseException('Could not migrate the existing startup entry. Check Windows startup settings.');
  end;
end;

function InitializeUninstall: Boolean;
begin
  Result := BeginMaintenance and ProcessesStopped;
  if not Result then SuppressibleMsgBox('Exit SeleVox and close CLI/MCP tasks before uninstalling. No process will be terminated.', mbError, MB_OK, IDOK);
end;

procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
var Existing: String;
begin
  if CurUninstallStep = usUninstall then begin
    if not ProcessesStopped then RaiseException('SeleVox is running. Exit it before uninstalling.');
    if not FilesUnlocked(ExpandConstant('{app}')) then RaiseException('Installation files are in use or inaccessible. Close applications before uninstalling.');
    if RegQueryStringValue(HKCU, RunKey, 'SeleVox', Existing) and (CompareText(Existing, AppCommand) = 0) then
      if not RegDeleteValue(HKCU, RunKey, 'SeleVox') then RaiseException('Cannot remove startup entry.');
  end;
end;
