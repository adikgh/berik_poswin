; Berik POS офлайн касса — Inno Setup 6 орнатқышы
; Жинау: iscc /DAppVer=1.0.0 installer\BerikPOS.iss  (алдымен build\prepare.ps1)

#ifndef AppVer
  #define AppVer "1.0.0"
#endif

[Setup]
AppId={{6F1B2C8E-4E7A-4B7E-9C1E-4A1D2E3F5B6C}
AppName=Berik POS
AppVersion={#AppVer}
AppPublisher=Berik
DefaultDirName={sd}\BerikPOS
DisableDirPage=yes
DefaultGroupName=Berik POS
DisableProgramGroupPage=yes
OutputDir=..\out
OutputBaseFilename=BerikPOS-Setup-{#AppVer}
Compression=lzma2/ultra64
SolidCompression=yes
; 32-бит программа: Windows 7/8/10/11, 32-бит те 64-бит те
MinVersion=6.1sp1
PrivilegesRequired=admin
SetupIconFile=..\src\BerikPOS\app.ico
UninstallDisplayIcon={app}\BerikPOS.exe
CloseApplications=yes
RestartApplications=no
WizardStyle=modern

[Languages]
Name: "ru"; MessagesFile: "compiler:Languages\Russian.isl"

[Tasks]
Name: "desktopicon"; Description: "Жұмыс үстеліне белгіше"; GroupDescription: "Қосымша:"

[Dirs]
; Программа өз папкасына жаза алуы керек (db.php, иконкалар)
Name: "{app}"; Permissions: users-modify
Name: "{commonappdata}\BerikPOS"; Permissions: users-modify

[Files]
Source: "..\dist\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs
Source: "MicrosoftEdgeWebview2Setup.exe"; DestDir: "{tmp}"; Flags: deleteafterinstall; Check: NeedWebView2
Source: "vc_redist.x86.exe"; DestDir: "{tmp}"; Flags: deleteafterinstall; Check: NeedVC
Source: "ndp48-web.exe"; DestDir: "{tmp}"; Flags: deleteafterinstall; Check: NeedNet48

[Icons]
Name: "{group}\Berik POS"; Filename: "{app}\BerikPOS.exe"
Name: "{autodesktop}\Berik POS"; Filename: "{app}\BerikPOS.exe"; Tasks: desktopicon

[Run]
Filename: "{tmp}\ndp48-web.exe"; Parameters: "/q /norestart"; StatusMsg: ".NET Framework 4.8 орнатылуда (интернет керек, бірнеше минут)..."; Check: NeedNet48; Flags: waituntilterminated
Filename: "{tmp}\vc_redist.x86.exe"; Parameters: "/install /quiet /norestart"; StatusMsg: "Visual C++ runtime орнатылуда..."; Check: NeedVC; Flags: waituntilterminated
Filename: "{tmp}\MicrosoftEdgeWebview2Setup.exe"; Parameters: "/silent /install"; StatusMsg: "WebView2 орнатылуда..."; Check: NeedWebView2; Flags: waituntilterminated
Filename: "{app}\BerikPOS.exe"; Description: "Berik POS ашу"; Flags: nowait postinstall skipifsilent
; Жаңартудан кейін (/SILENT) программа өзі ашылады
Filename: "{app}\BerikPOS.exe"; Flags: nowait; Check: WizardSilent

[UninstallDelete]
Type: files; Name: "{app}\www\site\config\db.php"

[Code]
function NeedWebView2: Boolean;
var v: String;
begin
  // 32-бит орнатқыш: 64-бит Windows-та HKLM\SOFTWARE өзі WOW6432Node-қа бағытталады
  Result := not (RegQueryStringValue(HKLM, 'SOFTWARE\Microsoft\EdgeUpdate\Clients\{F3017226-FE2A-4295-8BDF-00C3A9A7E4C5}', 'pv', v) and (v <> '') and (v <> '0.0.0.0'))
        and not (RegQueryStringValue(HKCU, 'Software\Microsoft\EdgeUpdate\Clients\{F3017226-FE2A-4295-8BDF-00C3A9A7E4C5}', 'pv', v) and (v <> '') and (v <> '0.0.0.0'));
end;

function NeedVC: Boolean;
var n: Cardinal;
begin
  Result := not (RegQueryDWordValue(HKLM, 'SOFTWARE\Microsoft\VisualStudio\14.0\VC\Runtimes\x86', 'Installed', n) and (n = 1));
end;

function NeedNet48: Boolean;
var n: Cardinal;
begin
  Result := not (RegQueryDWordValue(HKLM, 'SOFTWARE\Microsoft\NET Framework Setup\NDP\v4\Full', 'Release', n) and (n >= 528040));
end;

// Жаңартар алдында ескі программаны жабу (база дұрыс жабылуы үшін ол өзі тоқтатады)
function PrepareToInstall(var NeedsRestart: Boolean): String;
var rc: Integer;
begin
  Exec('taskkill', '/IM BerikPOS.exe', '', SW_HIDE, ewWaitUntilTerminated, rc);
  Sleep(4000);
  Result := '';
end;
