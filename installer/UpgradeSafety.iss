{ Minimal ownership record and recoverable retirement of removed payload files. }
{ Included by SeleVox.iss; no user-facing override or downgrade switch. }
const
  PayloadName = '.selevox-payload';
  PendingName = '.selevox-upgrade-pending';
  StateName = '.selevox-upgrade-state';
type
  TRelease = record
    Major, Minor, Patch: Integer;
    Rc: String;
  end;
var
  NewFiles, OldFiles: TStringList;
  BaseVersion, OldHash: String;
  Repairing, Completing, UpgradeStarted: Boolean;

function VersionSize(FileName: String; var Handle: Cardinal): Cardinal;
  external 'GetFileVersionInfoSizeW@version.dll stdcall';
function VersionData(FileName: String; Handle, Size: Cardinal; Data: String): Boolean;
  external 'GetFileVersionInfoW@version.dll stdcall';
function QueryVersion(Data, Name: String; var Value, Size: Cardinal): Boolean;
  external 'VerQueryValueW@version.dll stdcall';
function CopyVersionText(Destination: String; Source: Cardinal; Count: Integer): Integer;
  external 'lstrcpynW@kernel32.dll stdcall';

function ProductVersion(FileName: String): String;
var Size, Handle, Value, Count: Cardinal; Data, Text, Language: String; I: Integer;
begin
  Result := '';
  Size := VersionSize(FileName, Handle);
  if (Size = 0) or (Size > 1048576) then Exit;
  SetLength(Data, (Size+1) div 2);
  if not VersionData(FileName, 0, Size, Data) then Exit;
  for I := 0 to 1 do begin
    if I = 0 then Language := '000004b0' else Language := '040904b0';
    if QueryVersion(Data, '\StringFileInfo\' + Language + '\ProductVersion', Value, Count) and (Count > 1) and (Count < 4096) then begin
      SetLength(Text, Count);
      CopyVersionText(Text, Value, Count);
      Result := Trim(Copy(Text, 1, Count-1));
      Exit;
    end;
  end;
end;

function Digits(Value: String; AllowZero: Boolean): Boolean;
var I: Integer;
begin
  Result := False;
  if Value = '' then Exit;
  if (Length(Value) > 1) and (Value[1] = '0') then Exit;
  if not AllowZero and (Value = '0') then Exit;
  for I := 1 to Length(Value) do if (Value[I] < '0') or (Value[I] > '9') then Exit;
  Result := True;
end;

function ParseRelease(Value: String; var Version: TRelease): Boolean;
var Numeric, Part: String; P, I, Number: Integer;
begin
  Result := False; Version.Rc := '';
  P := Pos('-rc.', Value);
  if P > 0 then begin
    Version.Rc := Copy(Value, P+4, Length(Value));
    if not Digits(Version.Rc, False) then Exit;
    Numeric := Copy(Value, 1, P-1);
  end else Numeric := Value;
  for I := 0 to 2 do begin
    P := Pos('.', Numeric);
    if I < 2 then begin
      if P = 0 then Exit;
      Part := Copy(Numeric, 1, P-1); Delete(Numeric, 1, P);
    end else begin
      if P <> 0 then Exit;
      Part := Numeric;
    end;
    if not Digits(Part, True) or (Length(Part) > 5) then Exit;
    Number := StrToIntDef(Part, -1);
    if (Number < 0) or (Number > 65534) then Exit;
    if I = 0 then Version.Major := Number;
    if I = 1 then Version.Minor := Number;
    if I = 2 then Version.Patch := Number;
  end;
  Result := True;
end;

function CompareRelease(A, B: TRelease): Integer;
begin
  Result := A.Major-B.Major; if Result <> 0 then Exit;
  Result := A.Minor-B.Minor; if Result <> 0 then Exit;
  Result := A.Patch-B.Patch; if Result <> 0 then Exit;
  if A.Rc = B.Rc then begin Result := 0; Exit; end;
  if A.Rc = '' then begin Result := 1; Exit; end;
  if B.Rc = '' then begin Result := -1; Exit; end;
  Result := Length(A.Rc)-Length(B.Rc); if Result <> 0 then Exit;
  if A.Rc > B.Rc then Result := 1 else Result := -1;
end;

function NumericVersion(Value: String): String;
var V: TRelease;
begin
  Result := '';
  if ParseRelease(Value, V) then Result := IntToStr(V.Major)+'.'+IntToStr(V.Minor)+'.'+IntToStr(V.Patch)+'.0';
end;

function SafeRelative(Path: String): Boolean;
var Part, Rest: String; P: Integer;
begin
  Result := False;
  if (Path = '') or (Path[1] = '\') or (Pos(':', Path) > 0) or (Pos('/', Path) > 0) or (Pos('=', Path) > 0) or (Pos(#0, Path) > 0) then Exit;
  if (Pos('.selevox-', Lowercase(Path)) = 1) or (Pos('unins', Lowercase(Path)) = 1) then Exit;
  Rest := Path;
  repeat
    P := Pos('\', Rest);
    if P > 0 then begin Part := Copy(Rest, 1, P-1); Delete(Rest, 1, P); end else begin Part := Rest; Rest := ''; end;
    if (Part = '') or (Part = '.') or (Part = '..') or (Part <> Trim(Part)) or (Copy(Part, Length(Part), 1) = '.') then Exit;
  until Rest = '';
  Result := True;
end;

function IsHash(Value: String): Boolean;
var I: Integer;
begin
  Result := False;
  if Length(Value) <> 64 then Exit;
  for I := 1 to 64 do if Pos(Value[I], '0123456789abcdef') = 0 then Exit;
  Result := True;
end;

function OwnedName(Files: TStringList; Index: Integer): String;
begin
  Result := Copy(Files[Index], 1, Pos('=', Files[Index])-1);
end;

function OwnedHash(Files: TStringList; Name: String): String;
var I: Integer;
begin
  Result := '';
  for I := 0 to Files.Count-1 do if CompareText(OwnedName(Files, I), Name) = 0 then begin
    Result := Copy(Files[I], Pos('=', Files[I])+1, Length(Files[I])); Exit;
  end;
end;

function ReadManifest(Path: String; Files: TStringList; var Version: String): Boolean;
var Lines: TArrayOfString; I, P: Integer; Name, Hash: String; V: TRelease;
begin
  Result := False; Files.Clear;
  if not LoadStringsFromFile(Path, Lines) then Exit;
  if GetArrayLength(Lines) < 7 then Exit;
  if Lines[0] <> 'SeleVox payload v1' then Exit;
  Version := Lines[1]; if not ParseRelease(Version, V) then Exit;
  for I := 2 to GetArrayLength(Lines)-1 do begin
    P := Pos('=', Lines[I]);
    if P = 0 then Exit;
    Name := Copy(Lines[I], 1, P-1); Hash := Copy(Lines[I], P+1, Length(Lines[I]));
    if not SafeRelative(Name) or not IsHash(Hash) or (OwnedHash(Files, Name) <> '') then Exit;
    Files.Add(Name+'='+Hash);
  end;
  Result := (OwnedHash(Files, 'SeleVox.App.exe') <> '') and (OwnedHash(Files, 'SeleVox.Cli.exe') <> '') and
    (OwnedHash(Files, 'SeleVox.App.dll') <> '') and (OwnedHash(Files, 'SeleVox.Cli.dll') <> '') and (OwnedHash(Files, 'SeleVox.Core.dll') <> '');
end;

function AnchorVersionsMatch(Version: String; Files: TStringList; Recovery: Boolean): Boolean;
var Names: TArrayOfString; I: Integer; Path, Product, Numeric: String;
begin
  Result := False;
  Names := ['SeleVox.App.exe', 'SeleVox.Cli.exe', 'SeleVox.App.dll', 'SeleVox.Cli.dll', 'SeleVox.Core.dll'];
  for I := 0 to GetArrayLength(Names)-1 do begin
    Path := ExpandConstant('{app}\')+Names[I];
    if Recovery and not FileExists(Path) then Continue;
    Product := ProductVersion(Path);
    if Recovery then begin
      if (Product <> Version) and (Product <> '{#Version}') then Exit;
    end else if Product <> Version then Exit;
    if not GetVersionNumbersString(Path, Numeric) or (Numeric <> NumericVersion(Product)) then Exit;
    if not Recovery and (GetSHA256OfFile(Path) <> OwnedHash(Files, Names[I])) then Exit;
  end;
  Result := True;
end;

function CheckUpgrade: String;
var Installed, Version, RegisteredHash, Marker, State, Path, Backup: String;
    Current, Target: TRelease; I, Handle: Integer;
begin
  Result := ''; BaseVersion := ''; OldHash := ''; Repairing := False; Completing := False;
  if NewFiles = nil then NewFiles := TStringList.Create;
  if OldFiles = nil then OldFiles := TStringList.Create;
  OldFiles.Clear;
  ExtractTemporaryFile(PayloadName);
  if not ReadManifest(ExpandConstant('{tmp}\')+PayloadName, NewFiles, Version) or (Version <> '{#Version}') then begin Result := 'Setup payload inventory is invalid.'; Exit; end;
  Marker := ExpandConstant('{app}\')+PendingName;
  State := ExpandConstant('{app}\')+StateName;
  if FileExists(Marker) then begin
    Repairing := True;
    if GetIniString('Upgrade', 'Target', '', Marker) <> '{#Version}' then begin Result := 'An unfinished installation must first be repaired using Setup '+GetIniString('Upgrade', 'Target', 'unknown', Marker)+'. Attempted: {#Version}. Keep recovery files.'; Exit; end;
    if GetIniString('Upgrade', 'NewHash', '', Marker) <> '{#PayloadHash}' then begin Result := 'Recovery payload differs. Use the original target version Setup and keep recovery files.'; Exit; end;
    Completing := GetIniString('Upgrade', 'Complete', '', Marker) = '1';
    ParseRelease('{#Version}', Target);
    BaseVersion := GetIniString('Upgrade', 'Base', '', Marker);
    OldHash := GetIniString('Upgrade', 'OldHash', '', Marker);
    if BaseVersion <> '' then begin
      if not ParseRelease(BaseVersion, Current) or not IsHash(OldHash) or (CompareRelease(Target, Current) < 0) then begin Result := 'Invalid recovery base version.'; Exit; end;
      if not Completing then begin
        if not FileExists(State+'\previous.manifest') or (GetSHA256OfFile(State+'\previous.manifest') <> OldHash) or
          not ReadManifest(State+'\previous.manifest', OldFiles, Version) or (Version <> BaseVersion) then begin Result := 'Recovery ownership record is invalid. Keep this directory for manual recovery; do not run SeleVox.'; Exit; end;
      end;
    end else if OldHash <> '' then begin Result := 'Invalid fresh-install recovery record.'; Exit; end;
    if RegKeyExists(HKCU, UninstallKey) then begin
      if not RegQueryStringValue(HKCU, UninstallKey, 'DisplayVersion', Installed) or ((Installed <> BaseVersion) and (Installed <> '{#Version}')) then begin Result := 'Recovery version metadata is inconsistent. Keep recovery files.'; Exit; end;
      if RegQueryStringValue(HKCU, UninstallKey, 'PayloadSHA256', RegisteredHash) then begin
        if (RegisteredHash <> OldHash) and (RegisteredHash <> '{#PayloadHash}') then begin Result := 'Recovery ownership metadata is inconsistent.'; Exit; end;
      end else if Installed <> '{#Version}' then begin Result := 'Recovery ownership metadata is missing.'; Exit; end;
    end else if BaseVersion <> '' then begin Result := 'Recovery uninstall identity is missing.'; Exit; end;
    if not AnchorVersionsMatch(BaseVersion, OldFiles, True) then begin Result := 'Recovery program version metadata is inconsistent.'; Exit; end;
  end else if RegKeyExists(HKCU, UninstallKey) then begin
    if not RegQueryStringValue(HKCU, UninstallKey, 'DisplayVersion', Installed) or not ParseRelease(Installed, Current) then begin Result := 'Installed version is unrecognized; attempted {#Version}. No files were changed.'; Exit; end;
    ParseRelease('{#Version}', Target);
    Path := ExpandConstant('{app}\')+PayloadName;
    if not FileExists(Path) or not RegQueryStringValue(HKCU, UninstallKey, 'PayloadSHA256', RegisteredHash) or
      (GetSHA256OfFile(Path) <> RegisteredHash) or not ReadManifest(Path, OldFiles, Version) or (Version <> Installed) or
      not AnchorVersionsMatch(Installed, OldFiles, False) then begin Result := 'Installed version/ownership metadata is inconsistent ('+Installed+'; attempted {#Version}). No files were changed. Keep user files; use the existing uninstaller before a clean install if this predates ownership records.'; Exit; end;
    if CompareRelease(Target, Current) < 0 then begin Result := 'Downgrade refused: installed '+Installed+', attempted {#Version}. Use the installed version or a newer Setup.'; Exit; end;
    BaseVersion := Installed; OldHash := RegisteredHash;
    if DirExists(State) then begin Result := 'An unrecognized recovery directory exists. No files were changed.'; Exit; end;
  end else if DirExists(State) then begin Result := 'An unrecognized recovery directory exists. No files were changed.'; Exit; end;
  Path := ExpandConstant('{app}\')+PayloadName;
  if FileExists(Path) then begin
    Handle := OpenFileExclusive(Path, $C0010000, 0, 0, 3, 0, 0);
    if Handle = -1 then begin Result := 'Payload ownership record lacks write/delete permissions. No files were changed.'; Exit; end;
    CloseHandle(Handle);
  end;
  for I := 0 to NewFiles.Count-1 do begin
    Path := ExpandConstant('{app}\')+OwnedName(NewFiles, I);
    if FileExists(Path) and (OwnedHash(OldFiles, OwnedName(NewFiles, I)) = '') and not Repairing then begin Result := 'A new program file collides with an unmanaged file. Move that file yourself before retrying.'; Exit; end;
    if FileExists(Path) then begin
      Handle := OpenFileExclusive(Path, $C0010000, 0, 0, 3, 0, 0);
      if Handle = -1 then begin Result := 'Program files are in use or lack write/delete permissions. No files were changed.'; Exit; end;
      CloseHandle(Handle);
    end;
  end;
  for I := 0 to OldFiles.Count-1 do if OwnedHash(NewFiles, OwnedName(OldFiles, I)) = '' then begin
    Path := ExpandConstant('{app}\')+OwnedName(OldFiles, I); Backup := State+'\retired\'+OwnedName(OldFiles, I);
    if FileExists(Path) and FileExists(Backup) then begin Result := 'Duplicate retired component ownership is ambiguous.'; Exit; end;
    if FileExists(Path) then begin
      if GetSHA256OfFile(Path) <> OwnedHash(OldFiles, OwnedName(OldFiles, I)) then begin Result := 'A retired program component was modified; refusing to remove it. No files were changed.'; Exit; end;
      Handle := OpenFileExclusive(Path, $80010000, 0, 0, 3, 0, 0);
      if Handle = -1 then begin Result := 'A retired program component is locked or lacks delete permission. No files were changed.'; Exit; end;
      CloseHandle(Handle);
    end;
    if FileExists(Backup) and (GetSHA256OfFile(Backup) <> OwnedHash(OldFiles, OwnedName(OldFiles, I))) then begin Result := 'Retired component backup does not match its ownership record.'; Exit; end;
  end;
end;

procedure BeginUpgrade;
var Marker, State, Path, Backup: String; I: Integer;
begin
  { Revalidate immediately before changing any installed state. }
  Path := CheckUpgrade; if Path <> '' then RaiseException(Path);
  Marker := ExpandConstant('{app}\')+PendingName; State := ExpandConstant('{app}\')+StateName;
  UpgradeStarted := True;
  if not Repairing then begin
    if not ForceDirectories(State) then RaiseException('Cannot create upgrade recovery directory. No program files were replaced.');
    if BaseVersion <> '' then
      if not CopyFile(ExpandConstant('{app}\')+PayloadName, State+'\previous.manifest', True) then RaiseException('Cannot save the previous ownership record.');
    if not SetIniString('Upgrade', 'Target', '{#Version}', Marker) or not SetIniString('Upgrade', 'Base', BaseVersion, Marker) or
      not SetIniString('Upgrade', 'OldHash', OldHash, Marker) or not SetIniString('Upgrade', 'NewHash', '{#PayloadHash}', Marker) then RaiseException('Cannot write upgrade recovery marker. Keep recovery files.');
  end;
  for I := 0 to OldFiles.Count-1 do if OwnedHash(NewFiles, OwnedName(OldFiles, I)) = '' then begin
    Path := ExpandConstant('{app}\')+OwnedName(OldFiles, I); Backup := State+'\retired\'+OwnedName(OldFiles, I);
    if FileExists(Path) then begin
      if not ForceDirectories(ExtractFileDir(Backup)) or not RenameFile(Path, Backup) then RaiseException('Cannot retire an owned component. Re-run this same target Setup to repair; keep recovery files.');
      Log('Retired owned component: '+OwnedName(OldFiles, I));
    end;
  end;
end;

function RemoveEmptyRecoveryDirectories(Directory: String): Boolean;
var Find: TFindRec; Path: String;
begin
  Result := False;
  if not DirExists(Directory) then begin Result := True; Exit; end;
  if FindFirst(AddBackslash(Directory)+'*', Find) then begin
    try
      repeat
        if (Find.Name <> '.') and (Find.Name <> '..') then begin
          Path := AddBackslash(Directory)+Find.Name;
          if ((Find.Attributes and FILE_ATTRIBUTE_DIRECTORY) = 0) or ((Find.Attributes and FILE_ATTRIBUTE_REPARSE_POINT) <> 0) then Exit;
          if not RemoveEmptyRecoveryDirectories(Path) then Exit;
        end;
      until not FindNext(Find);
    finally FindClose(Find); end;
  end;
  Result := RemoveDir(Directory);
end;

procedure FinishUpgrade;
var Marker, State, Backup: String; I: Integer;
begin
  Marker := ExpandConstant('{app}\')+PendingName; State := ExpandConstant('{app}\')+StateName;
  if GetSHA256OfFile(ExpandConstant('{app}\')+PayloadName) <> '{#PayloadHash}' then RaiseException('Installed ownership record mismatch. Re-run this same target Setup to repair.');
  if not RegWriteStringValue(HKCU, UninstallKey, 'PayloadSHA256', '{#PayloadHash}') then RaiseException('Cannot finalize payload ownership. Re-run this same target Setup to repair.');
  for I := 0 to OldFiles.Count-1 do if OwnedHash(NewFiles, OwnedName(OldFiles, I)) = '' then begin
    Backup := State+'\retired\'+OwnedName(OldFiles, I);
    if FileExists(Backup) then begin
      if (GetSHA256OfFile(Backup) <> OwnedHash(OldFiles, OwnedName(OldFiles, I))) or not DeleteFile(Backup) then RaiseException('Cannot safely clean a retired component. Keep recovery files; repair with this same Setup.');
    end;
  end;
  if not SetIniString('Upgrade', 'Complete', '1', Marker) then RaiseException('Cannot finalize recovery marker.');
  { Remove only owned files and empty directories, never an unknown tree. }
  if FileExists(State+'\previous.manifest') then
    if (GetSHA256OfFile(State+'\previous.manifest') <> OldHash) or not DeleteFile(State+'\previous.manifest') then RaiseException('Recovery record cleanup failed; repair with this same Setup.');
  if not RemoveEmptyRecoveryDirectories(State) then RaiseException('Unknown recovery files remain. Keep them and repair with this same Setup; no unknown files were deleted.');
  if not DeleteFile(Marker) then RaiseException('Cannot clear incomplete-install marker. Repair with this same Setup.');
  Log('Payload verified; upgrade committed.');
end;

procedure DeinitializeSetup;
begin
  if UpgradeStarted then if FileExists(ExpandConstant('{app}\')+PendingName) then
    SuppressibleMsgBox('SeleVox installation is unfinished. Do not run it. Keep its recovery directory and re-run Setup {#Version} to repair. Do not use an older Setup.', mbError, MB_OK, IDOK);
  if NewFiles <> nil then NewFiles.Free;
  if OldFiles <> nil then OldFiles.Free;
end;
