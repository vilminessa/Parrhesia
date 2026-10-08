; ============================================================
; Parrhesia — инсталлятор приложения + виртуального аудио-драйвера (Ф5)
; Собирается: installer\build.bat (publish приложения + ISCC).
; ============================================================
#ifndef MyAppVersion
  #define MyAppVersion "0.1.0"
#endif

[Setup]
AppId={{7E4A1C49-3D2B-4F6E-9A81-PARRHESIA0001}
AppName=Parrhesia
AppVersion={#MyAppVersion}
AppPublisher=Parrhesia project
DefaultDirName={autopf}\Parrhesia
DefaultGroupName=Parrhesia
PrivilegesRequired=admin
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
OutputDir=..\dist
OutputBaseFilename=Parrhesia-Setup-{#MyAppVersion}
Compression=lzma2
SolidCompression=yes
WizardStyle=modern
DisableProgramGroupPage=yes
CloseApplications=yes
UninstallDisplayName=Parrhesia

[Languages]
Name: "russian"; MessagesFile: "compiler:Default.isl"

[Files]
; приложение (self-contained publish)
Source: "..\publish\app\*"; DestDir: "{app}"; Flags: recursesubdirs ignoreversion
; пакет драйвера (inf/sys/cat)
Source: "..\driver\x64\Release\package\*"; DestDir: "{app}\driver-package"; Flags: recursesubdirs ignoreversion
; установщик устройств (SetupAPI, без devcon)
Source: "install-devices.ps1"; DestDir: "{app}"; Flags: ignoreversion

[Icons]
Name: "{group}\Parrhesia"; Filename: "{app}\Parrhesia.App.exe"
Name: "{autodesktop}\Parrhesia"; Filename: "{app}\Parrhesia.App.exe"; Tasks: desktopicon

[Tasks]
Name: "desktopicon"; Description: "Ярлык на рабочем столе"; Flags: unchecked

[Run]
Filename: "powershell.exe"; \
  Parameters: "-NoProfile -ExecutionPolicy Bypass -File ""{app}\install-devices.ps1"""; \
  StatusMsg: "Установка виртуального аудио-драйвера..."; \
  Flags: runhidden waituntilterminated
Filename: "{app}\Parrhesia.App.exe"; Description: "Запустить Parrhesia"; Flags: nowait postinstall skipifsilent

[UninstallRun]
Filename: "powershell.exe"; \
  Parameters: "-NoProfile -ExecutionPolicy Bypass -File ""{app}\install-devices.ps1"" -Uninstall"; \
  RunOnceId: "RemoveParrhesiaDevices"; Flags: runhidden waituntilterminated

[Code]
function IsTestSigningOn: Boolean;
var
  ResultCode: Integer;
  FileName: String;
  Lines: TStringList;
begin
  Result := False;
  FileName := ExpandConstant('{tmp}\bcdedit.txt');
  if Exec(ExpandConstant('{sys}\cmd.exe'),
          '/c bcdedit /enum {{current}} > "' + FileName + '" 2>&1',
          '', SW_HIDE, ewWaitUntilTerminated, ResultCode) then
  begin
    Lines := TStringList.Create;
    try
      Lines.LoadFromFile(FileName);
      Result := Pos('testsigning', LowerCase(Lines.Text)) > 0;
    finally
      Lines.Free;
    end;
  end;
end;

function InitializeSetup: Boolean;
begin
  Result := True;
  if not IsTestSigningOn then
  begin
    MsgBox('Драйвер Parrhesia требует режим тестовой подписи.'#13#10#13#10 +
      'Выполните от администратора:'#13#10'  bcdedit /set testsigning on'#13#10 +
      'и перезагрузите компьютер (Secure Boot должен быть отключён).'#13#10#13#10 +
      'Приложение установится, но виртуальные устройства не заработают до перезагрузки.',
      mbInformation, MB_OK);
  end;
end;
