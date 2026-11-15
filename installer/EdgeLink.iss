; EdgeLink setup (Inno Setup 6). Build:  ISCC installer\EdgeLink.iss   (after companion\build.cmd)
; Per-user install: no administrator prompt. Output: dist\release\EdgeLink-Setup-<version>.exe

#define AppName "EdgeLink"
#define AppExe "EdgeLink.exe"
#define Src "..\dist\EdgeLink"
#define AppVer GetVersionNumbersString(Src + "\" + AppExe)
#define ShortVer Copy(AppVer, 1, RPos(".", AppVer) - 1)

[Setup]
AppId={{6C0B6A0E-7C1F-4E0B-9D4E-ED6E11A0C5F1}
AppName={#AppName}
AppVersion={#ShortVer}
AppVerName={#AppName} {#ShortVer}
AppPublisher=ultimateunraid
AppPublisherURL=https://github.com/ultimateunraid/EdgeLink
AppSupportURL=https://github.com/ultimateunraid/EdgeLink/issues
AppUpdatesURL=https://github.com/ultimateunraid/EdgeLink/releases
VersionInfoVersion={#AppVer}
DefaultDirName={localappdata}\Programs\EdgeLink
DisableProgramGroupPage=yes
DisableDirPage=auto
PrivilegesRequired=lowest
OutputDir=..\dist\release
OutputBaseFilename=EdgeLink-Setup-{#ShortVer}
SetupIconFile=..\companion\EdgeLink.ico
UninstallDisplayIcon={app}\{#AppExe}
LicenseFile=..\LICENSE
InfoBeforeFile=privacy.txt
Compression=lzma2
SolidCompression=yes
WizardStyle=modern
CloseApplications=no

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"

[Tasks]
Name: "addon"; Description: "Install the WoW addon into my World of Warcraft folder"
Name: "icons"; Description: "Download item icons and map art from Wowhead (sends item IDs and zone names)"
Name: "startup"; Description: "Start EdgeLink when Windows starts"; Flags: unchecked

[Files]
Source: "{#Src}\{#AppExe}"; DestDir: "{app}"; Flags: ignoreversion
Source: "{#Src}\EdgeLink.exe.config"; DestDir: "{app}"; Flags: ignoreversion
Source: "..\companion\EdgeLink.ini"; DestDir: "{app}"; Flags: onlyifdoesntexist
Source: "{#Src}\addon\*"; DestDir: "{app}\addon"; Flags: ignoreversion recursesubdirs createallsubdirs
Source: "{#Src}\widget\*"; DestDir: "{app}\widget"; Flags: ignoreversion recursesubdirs createallsubdirs
Source: "{#Src}\edgelink-wow.icuewidget"; DestDir: "{app}"; Flags: ignoreversion
Source: "..\README.md"; DestDir: "{app}"; Flags: ignoreversion
Source: "..\LICENSE"; DestDir: "{app}"; DestName: "LICENSE.txt"; Flags: ignoreversion
Source: "..\CODE_SIGNING_POLICY.md"; DestDir: "{app}"; Flags: ignoreversion

[InstallDelete]
; widget files are replaced as a set so nothing stale is served after an update
Type: filesandordirs; Name: "{app}\widget"

[Icons]
Name: "{userprograms}\EdgeLink"; Filename: "{app}\{#AppExe}"

[Registry]
Root: HKCU; Subkey: "Software\Microsoft\Windows\CurrentVersion\Run"; ValueType: string; ValueName: "EdgeLink"; ValueData: """{app}\{#AppExe}"""; Tasks: startup; Flags: uninsdeletevalue
; the tray app's own settings and its Start-with-Windows entry: removed on uninstall
Root: HKCU; Subkey: "Software\EdgeLink"; Flags: dontcreatekey uninsdeletekey
Root: HKCU; Subkey: "Software\Microsoft\Windows\CurrentVersion\Run"; ValueType: none; ValueName: "EdgeLink"; Flags: dontcreatekey uninsdeletevalue

[Run]
Filename: "{app}\{#AppExe}"; Parameters: "--install-addon"; StatusMsg: "Installing the WoW addon..."; Tasks: addon; Flags: runhidden
Filename: "{app}\{#AppExe}"; Description: "Start EdgeLink now"; Flags: nowait postinstall skipifsilent
Filename: "{app}\edgelink-wow.icuewidget"; Description: "Add the widget to iCUE (accept the import in iCUE, then place it on the Edge)"; Flags: shellexec nowait postinstall skipifsilent; Check: IcueKnowsWidgets

[UninstallRun]
Filename: "{app}\{#AppExe}"; Parameters: "--uninstall-addon"; RunOnceId: "RemoveAddon"; Flags: runhidden

[Code]
procedure StopEdgeLink;
var rc: Integer;
begin
  Exec(ExpandConstant('{sys}\taskkill.exe'), '/IM {#AppExe} /F', '', SW_HIDE, ewWaitUntilTerminated, rc);
  Sleep(400);
end;

function PrepareToInstall(var NeedsRestart: Boolean): String;
begin
  StopEdgeLink;
  Result := '';
end;

function InitializeUninstall(): Boolean;
begin
  StopEdgeLink;
  Result := True;
end;

function IcueKnowsWidgets(): Boolean;
begin
  Result := RegKeyExists(HKCR, '.icuewidget') or RegKeyExists(HKCU, 'Software\Classes\.icuewidget');
end;

procedure DisableIcons;
var lines: TArrayOfString; i: Integer; ini: String;
begin
  ini := ExpandConstant('{app}\EdgeLink.ini');
  if LoadStringsFromFile(ini, lines) then
  begin
    for i := 0 to GetArrayLength(lines) - 1 do
      if Pos('icons=', Lowercase(Trim(lines[i]))) = 1 then lines[i] := 'Icons=false';
    SaveStringsToFile(ini, lines, False);
  end;
end;

procedure CurStepChanged(CurStep: TSetupStep);
begin
  if (CurStep = ssPostInstall) and not WizardIsTaskSelected('icons') then DisableIcons;
  if (CurStep = ssPostInstall) and not WizardSilent() and not IcueKnowsWidgets() then
    MsgBox('iCUE was not found, so the widget was not opened. After installing iCUE, right-click the EdgeLink tray icon and choose "Install widget in iCUE".', mbInformation, MB_OK);
end;

procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
begin
  if CurUninstallStep = usPostUninstall then
    if MsgBox('Also delete EdgeLink''s saved data (log, image cache, learned trails, saved inventories)?', mbConfirmation, MB_YESNO or MB_DEFBUTTON2) = IDYES then
      DelTree(ExpandConstant('{localappdata}\EdgeLink'), True, True, True);
end;
