#ifndef PackageDir
  #error PackageDir is required
#endif
#ifndef AppVersion
  #define AppVersion "0.1.0-beta.3.1"
#endif

[Setup]
AppId={{A21348C4-4BD9-4533-9F63-B99F3F80D0F1}
AppName=SLT Disk Manager
AppVersion={#AppVersion}
VersionInfoVersion=0.1.0.31
AppPublisher=Smells Like Tech
AppPublisherURL=https://softwares.smellsliketech.com.br
AppSupportURL=https://github.com/mikerock12/slt-disk-manager
DefaultDirName={localappdata}\Programs\SLT Disk Manager
DefaultGroupName=SLT Disk Manager
PrivilegesRequired=lowest
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
MinVersion=10.0.19041
WizardStyle=modern
Compression=lzma2
SolidCompression=yes
OutputDir=..\..\artifacts
OutputBaseFilename=SLT-Disk-Manager-Setup-{#AppVersion}
InfoBeforeFile=ANTES-DE-INSTALAR.txt
CloseApplications=no
RestartApplications=no
DisableProgramGroupPage=yes
UninstallDisplayName=SLT Disk Manager {#AppVersion}

[Languages]
Name: "brazilianportuguese"; MessagesFile: "compiler:Languages\BrazilianPortuguese.isl"

[Types]
Name: "workshop"; Description: "Servidor, painel e cliente (bancada completa)"
Name: "client"; Description: "Somente cliente Windows"
Name: "custom"; Description: "Escolher componentes"; Flags: iscustom

[Components]
Name: "server"; Description: "Servidor e painel da bancada"; Types: workshop
Name: "client"; Description: "Cliente Windows"; Types: workshop client
Name: "offline"; Description: "Agentes Rescue/WinPE e guias de preparação"; Types: workshop

[Files]
Source: "{#PackageDir}\server\*"; DestDir: "{app}\server"; Components: server; Flags: ignoreversion recursesubdirs createallsubdirs
Source: "{#PackageDir}\desktop\*"; DestDir: "{app}\desktop"; Components: server; Flags: ignoreversion recursesubdirs createallsubdirs
Source: "{#PackageDir}\client\*"; DestDir: "{app}\client"; Components: client; Flags: ignoreversion recursesubdirs createallsubdirs
Source: "{#PackageDir}\Servidor.cmd"; DestDir: "{app}"; Components: server; Flags: ignoreversion
Source: "{#PackageDir}\Iniciar-Servidor.ps1"; DestDir: "{app}"; Components: server; Flags: ignoreversion
Source: "{#PackageDir}\Cliente.cmd"; DestDir: "{app}"; Components: client; Flags: ignoreversion
Source: "{#PackageDir}\Iniciar-Cliente.ps1"; DestDir: "{app}"; Components: client; Flags: ignoreversion
Source: "{#PackageDir}\rescue\*"; DestDir: "{app}\rescue"; Components: offline; Flags: ignoreversion recursesubdirs createallsubdirs
Source: "{#PackageDir}\windows-pe\*"; DestDir: "{app}\windows-pe"; Components: offline; Flags: ignoreversion recursesubdirs createallsubdirs
Source: "{#PackageDir}\install-agent\*"; DestDir: "{app}\install-agent"; Components: client; Flags: ignoreversion recursesubdirs createallsubdirs
Source: "{#PackageDir}\install-server\*"; DestDir: "{app}\install-server"; Components: server; Flags: ignoreversion recursesubdirs createallsubdirs
Source: "{#PackageDir}\docs\*"; DestDir: "{app}\docs"; Flags: ignoreversion recursesubdirs createallsubdirs
Source: "{#PackageDir}\README.md"; DestDir: "{app}"; Flags: ignoreversion
Source: "{#PackageDir}\QUICKSTART.md"; DestDir: "{app}"; Flags: ignoreversion
Source: "{#PackageDir}\LICENSE"; DestDir: "{app}"; Flags: ignoreversion

[Icons]
Name: "{group}\Servidor e painel"; Filename: "{app}\Servidor.cmd"; WorkingDir: "{app}"; Components: server
Name: "{group}\Cliente Windows"; Filename: "{app}\Cliente.cmd"; WorkingDir: "{app}"; Components: client
Name: "{group}\Guia de uso"; Filename: "{app}\QUICKSTART.md"

; No automatic service, firewall, boot, network configuration or disk operation.
; Inno only removes installed files; generated state and customer data are retained.
