; TelloGameRoom Machine installer (Inno Setup 6).
; Installs the lock-screen app and the MachineGuard service on a gaming PC.
;
; Silent install across many PCs:
;   TelloGameRoom-Machine-Setup.exe /VERYSILENT /SUPPRESSMSGBOXES /NORESTART /SERVER=203.0.113.5 /FINGERPRINT=AB12...
; /SERVER and /FINGERPRINT are optional; they pre-fill the pairing screen so staff only type the pairing code.

#define AppVersion GetEnv("TELLO_VERSION")
#if AppVersion == ""
  #define AppVersion "0.1.0"
#endif

[Setup]
AppId={{6E1C5B0A-7C0B-4E2E-9B55-3B6B0D2F8A11}
AppName=TelloGameRoom Machine
AppVersion={#AppVersion}
AppPublisher=TelloGameRoom
DefaultDirName={autopf}\TelloGameRoom\Machine
DisableProgramGroupPage=yes
DisableDirPage=yes
PrivilegesRequired=admin
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
OutputDir=Output
OutputBaseFilename=TelloGameRoom-Machine-Setup-{#AppVersion}
Compression=lzma2
SolidCompression=yes
WizardStyle=modern
CloseApplications=no
UninstallDisplayName=TelloGameRoom Machine

[Languages]
Name: "en"; MessagesFile: "compiler:Default.isl"
Name: "es"; MessagesFile: "compiler:Languages\Spanish.isl"

[Dirs]
; The Machine app runs as the (standard) customer account and keeps its identity, cached session and logs here.
Name: "{commonappdata}\TelloGameRoom\Machine"; Permissions: users-modify

[Files]
Source: "publish\Machine\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[Registry]
; Second line of defence if the guard service is ever stopped: start the app at every logon.
Root: HKLM; Subkey: "Software\Microsoft\Windows\CurrentVersion\Run"; ValueType: string; ValueName: "TelloGameRoomMachine"; \
  ValueData: """{app}\TelloGameRoom.Machine.exe"""; Flags: uninsdeletevalue

[Run]
Filename: "{sys}\sc.exe"; Parameters: "create TelloGameRoomGuard binPath= ""\""{app}\TelloGameRoom.MachineGuard.exe\"""" start= auto DisplayName= ""TelloGameRoom MachineGuard"""; Flags: runhidden
Filename: "{sys}\sc.exe"; Parameters: "description TelloGameRoomGuard ""Keeps the TelloGameRoom lock screen running."""; Flags: runhidden
; Restart the service by itself if it ever crashes.
Filename: "{sys}\sc.exe"; Parameters: "failure TelloGameRoomGuard reset= 86400 actions= restart/5000/restart/5000/restart/5000"; Flags: runhidden
Filename: "{sys}\sc.exe"; Parameters: "start TelloGameRoomGuard"; Flags: runhidden

[UninstallRun]
Filename: "{sys}\sc.exe"; Parameters: "stop TelloGameRoomGuard"; Flags: runhidden; RunOnceId: "StopGuard"
Filename: "{sys}\taskkill.exe"; Parameters: "/F /IM TelloGameRoom.Machine.exe"; Flags: runhidden; RunOnceId: "KillMachine"
; Give Task Manager back to every user before the files disappear.
Filename: "{app}\TelloGameRoom.MachineGuard.exe"; Parameters: "--cleanup"; Flags: runhidden; RunOnceId: "RestorePolicy"
Filename: "{sys}\sc.exe"; Parameters: "delete TelloGameRoomGuard"; Flags: runhidden; RunOnceId: "DeleteGuard"

[Code]
function JsonArray(const Value: String): String;
begin
  if Value = '' then
    Result := '[]'
  else
    Result := '["' + Value + '"]';
end;

// Upgrades: stop the guard and the app first so their files can be replaced.
function PrepareToInstall(var NeedsRestart: Boolean): String;
var
  ResultCode: Integer;
begin
  Exec(ExpandConstant('{sys}\sc.exe'), 'stop TelloGameRoomGuard', '', SW_HIDE, ewWaitUntilTerminated, ResultCode);
  Exec(ExpandConstant('{sys}\taskkill.exe'), '/F /IM TelloGameRoom.Machine.exe', '', SW_HIDE, ewWaitUntilTerminated, ResultCode);
  Exec(ExpandConstant('{sys}\sc.exe'), 'delete TelloGameRoomGuard', '', SW_HIDE, ewWaitUntilTerminated, ResultCode);
  Sleep(1500);
  Result := '';
end;

// Writes the optional /SERVER= and /FINGERPRINT= values where the Machine app looks for them.
procedure CurStepChanged(CurStep: TSetupStep);
var
  Server, Fingerprint: String;
begin
  if CurStep = ssPostInstall then
  begin
    Server := ExpandConstant('{param:SERVER|}');
    Fingerprint := ExpandConstant('{param:FINGERPRINT|}');
    if Server <> '' then
      SaveStringToFile(ExpandConstant('{commonappdata}\TelloGameRoom\Machine\server.json'),
        '{"serverUrl":"' + Server + '","pinnedCertificates":' + JsonArray(Fingerprint) + '}', False);
  end;
end;
