#define MyAppName "PDFReader"
#define MyAppVersion "0.1.0"
#define MyAppPublisher "PDFReader"
#define PublishDir "..\publish\win-x64"

[Setup]
AppId={{8F0D39EF-0F85-4A1E-9B0A-5B98C3B4D8D2}
AppName={#MyAppName}
AppVersion={#MyAppVersion}
AppPublisher={#MyAppPublisher}
DefaultDirName={localappdata}\Programs\PDFReader
DefaultGroupName={#MyAppName}
OutputDir=..\publish\installer
OutputBaseFilename=PDFReader-{#MyAppVersion}-Setup
SetupIconFile=..\PDFReader.ico
UninstallDisplayIcon={app}\PDFReader.exe
Compression=lzma2
SolidCompression=yes
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
PrivilegesRequired=lowest
WizardStyle=modern

[Files]
Source: "{#PublishDir}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{autoprograms}\{#MyAppName}"; Filename: "{app}\PDFReader.exe"; WorkingDir: "{app}"
Name: "{autodesktop}\{#MyAppName}"; Filename: "{app}\PDFReader.exe"; WorkingDir: "{app}"

[Run]
Filename: "{app}\PDFReader.exe"; Description: "Launch {#MyAppName}"; WorkingDir: "{app}"; Flags: postinstall nowait skipifsilent

[UninstallDelete]
Type: filesandordirs; Name: "{app}\user_data\cache"
Type: filesandordirs; Name: "{app}\user_data"; Check: ShouldDeleteUserData

[Code]
var
  DeleteUserData: Boolean;

function InitializeUninstall(): Boolean;
begin
  DeleteUserData := False;
  if not WizardSilent then
    DeleteUserData := MsgBox(
      'Delete the user_data folder, including the database, settings, screenshots and audio files?'#13#10#13#10 +
      'Choose No to keep user data for a future installation.',
      mbConfirmation, MB_YESNO) = IDYES;
  Result := True;
end;

function ShouldDeleteUserData(): Boolean;
begin
  Result := DeleteUserData;
end;
