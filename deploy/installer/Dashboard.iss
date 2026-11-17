; TelloGameRoom Dashboard installer (Inno Setup 6).
;
; Silent install:
;   TelloGameRoom-Dashboard-Setup.exe /VERYSILENT /SUPPRESSMSGBOXES /NORESTART /SERVER=203.0.113.5 /FINGERPRINT=AB12...
; /SERVER and /FINGERPRINT are optional; they pre-fill the "connect to your server" screen for every Windows user.

#define AppVersion GetEnv("TELLO_VERSION")
#if AppVersion == ""
  #define AppVersion "0.1.0"
#endif

[Setup]
AppId={{B2A4F9D3-58C1-4B77-A0C2-91E6C7D4F322}
AppName=TelloGameRoom Dashboard
AppVersion={#AppVersion}
AppPublisher=TelloGameRoom
DefaultDirName={autopf}\TelloGameRoom\Dashboard
DefaultGroupName=TelloGameRoom
DisableProgramGroupPage=yes
PrivilegesRequired=admin
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
OutputDir=Output
OutputBaseFilename=TelloGameRoom-Dashboard-Setup-{#AppVersion}
Compression=lzma2
SolidCompression=yes
WizardStyle=modern
UninstallDisplayName=TelloGameRoom Dashboard

[Languages]
Name: "en"; MessagesFile: "compiler:Default.isl"
Name: "es"; MessagesFile: "compiler:Languages\Spanish.isl"

[Tasks]
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"

[Dirs]
Name: "{commonappdata}\TelloGameRoom\Dashboard"

[Files]
Source: "publish\Dashboard\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{autoprograms}\TelloGameRoom Dashboard"; Filename: "{app}\TelloGameRoom.Dashboard.exe"
Name: "{autodesktop}\TelloGameRoom Dashboard"; Filename: "{app}\TelloGameRoom.Dashboard.exe"; Tasks: desktopicon

[Run]
Filename: "{app}\TelloGameRoom.Dashboard.exe"; Description: "{cm:LaunchProgram,TelloGameRoom Dashboard}"; Flags: nowait postinstall skipifsilent

[Code]
function JsonArray(const Value: String): String;
begin
  if Value = '' then
    Result := '[]'
  else
    Result := '["' + Value + '"]';
end;

procedure CurStepChanged(CurStep: TSetupStep);
var
  Server, Fingerprint: String;
begin
  if CurStep = ssPostInstall then
  begin
    Server := ExpandConstant('{param:SERVER|}');
    Fingerprint := ExpandConstant('{param:FINGERPRINT|}');
    if Server <> '' then
      SaveStringToFile(ExpandConstant('{commonappdata}\TelloGameRoom\Dashboard\server.json'),
        '{"ServerUrl":"' + Server + '","PinnedCertificates":' + JsonArray(Fingerprint) + '}', False);
  end;
end;
