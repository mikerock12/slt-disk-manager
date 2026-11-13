#ifndef PackageDir
  #error PackageDir is required
#endif
#ifndef AppVersion
  #define AppVersion "0.1.0-beta.8"
#endif
#ifndef Edition
  #error Edition is required: server or client
#endif
; Two installers from one script. The server keeps the AppId and folder of the former
; combined installer, so it upgrades in place; the client has its own, so a bench PC can
; carry both.
#if Edition == "server"
  #define EditionName "Servidor"
  #define EditionId "{{A21348C4-4BD9-4533-9F63-B99F3F80D0F1}"
  #define EditionDir "SLT Disk Manager"
  #define EditionIcon "slt-disk-manager.ico"
#elif Edition == "client"
  #define EditionName "Cliente"
  #define EditionId "{{6B3F0D52-8E4A-4C71-9A2D-5F1C7E83B904}"
  #define EditionDir "SLT Disk Manager Cliente"
  #define EditionIcon "slt-client.ico"
#else
  #error Edition must be server or client
#endif

[Setup]
AppId={#EditionId}
AppName=SLT Disk Manager {#EditionName}
AppVersion={#AppVersion}
VersionInfoVersion=0.1.0.80
AppPublisher=Smells Like Tech
AppPublisherURL=https://softwares.smellsliketech.com.br
AppSupportURL=https://github.com/mikerock12/slt-disk-manager
DefaultDirName={localappdata}\Programs\{#EditionDir}
DefaultGroupName=SLT Disk Manager
PrivilegesRequired=lowest
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
MinVersion=10.0.19041
WizardStyle=modern
Compression=lzma2
SolidCompression=yes
OutputDir=..\..\artifacts
OutputBaseFilename=SLT-Disk-Manager-{#EditionName}-Setup-{#AppVersion}
#if Edition == "server"
InfoBeforeFile=ANTES-DE-INSTALAR-SERVIDOR.txt
#else
InfoBeforeFile=ANTES-DE-INSTALAR-CLIENTE.txt
#endif
CloseApplications=no
RestartApplications=no
DisableProgramGroupPage=yes
UninstallDisplayName=SLT Disk Manager {#EditionName} {#AppVersion}
; Custom icons (scripts/generate-icons.py): the setup itself, Add/Remove Programs and every shortcut.
SetupIconFile=..\branding\slt-setup.ico
UninstallDisplayIcon={app}\branding\{#EditionIcon}

[Languages]
Name: "brazilianportuguese"; MessagesFile: "compiler:Languages\BrazilianPortuguese.isl"

[Tasks]
Name: "desktopicon"; Description: "Criar atalhos na área de trabalho"
#if Edition == "server"
Name: "serverservice"; Description: "Instalar o servidor como serviço do Windows e liberar as portas 7443 (TCP) e 7444 (UDP, busca automática) só na rede privada (pede permissão de administrador)"; Flags: unchecked; Check: not ServiceExists
Name: "serverupdate"; Description: "Atualizar o serviço do servidor já instalado neste computador (pede permissão de administrador; certificados, computadores conectados e imagens são preservados)"; Check: ServiceExists
#else
Name: "clientservice"; Description: "Instalar o cliente como serviço do Windows: inventário e SMART automáticos e reinício em modo SLT pelo servidor (pede permissão de administrador)"; Flags: unchecked; Check: not ServiceExists
Name: "clientupdate"; Description: "Atualizar o serviço do cliente já instalado neste computador (pede permissão de administrador; a conexão com o servidor é preservada)"; Check: ServiceExists
#endif

[Files]
#if Edition == "server"
Source: "{#PackageDir}\server\*"; DestDir: "{app}\server"; Flags: ignoreversion recursesubdirs createallsubdirs
Source: "{#PackageDir}\network-boot\*"; DestDir: "{app}\network-boot"; Flags: ignoreversion recursesubdirs createallsubdirs
Source: "{#PackageDir}\desktop\*"; DestDir: "{app}\desktop"; Flags: ignoreversion recursesubdirs createallsubdirs
Source: "{#PackageDir}\Servidor.cmd"; DestDir: "{app}"; Flags: ignoreversion
Source: "{#PackageDir}\Iniciar-Servidor.ps1"; DestDir: "{app}"; Flags: ignoreversion
Source: "{#PackageDir}\install-server\*"; DestDir: "{app}\install-server"; Flags: ignoreversion recursesubdirs createallsubdirs
; Rescue and WinPE agents with their guides: the technician prepares the boot media here.
Source: "{#PackageDir}\rescue\*"; DestDir: "{app}\rescue"; Flags: ignoreversion recursesubdirs createallsubdirs
Source: "{#PackageDir}\windows-pe\*"; DestDir: "{app}\windows-pe"; Flags: ignoreversion recursesubdirs createallsubdirs
#else
Source: "{#PackageDir}\client\*"; DestDir: "{app}\client"; Flags: ignoreversion recursesubdirs createallsubdirs
Source: "{#PackageDir}\Cliente.cmd"; DestDir: "{app}"; Flags: ignoreversion
Source: "{#PackageDir}\Modo-SLT.cmd"; DestDir: "{app}"; Flags: ignoreversion
Source: "{#PackageDir}\Iniciar-Cliente.ps1"; DestDir: "{app}"; Flags: ignoreversion
Source: "{#PackageDir}\install-agent\*"; DestDir: "{app}\install-agent"; Flags: ignoreversion recursesubdirs createallsubdirs
#endif
Source: "{#PackageDir}\Executar-Instalacao.ps1"; DestDir: "{app}"; Flags: ignoreversion
Source: "{#PackageDir}\branding\*.ico"; DestDir: "{app}\branding"; Flags: ignoreversion
Source: "{#PackageDir}\docs\*"; DestDir: "{app}\docs"; Flags: ignoreversion recursesubdirs createallsubdirs
Source: "{#PackageDir}\README.md"; DestDir: "{app}"; Flags: ignoreversion
Source: "{#PackageDir}\QUICKSTART.md"; DestDir: "{app}"; Flags: ignoreversion
Source: "{#PackageDir}\LICENSE"; DestDir: "{app}"; Flags: ignoreversion

[InstallDelete]
; Shortcuts of earlier versions that were renamed or no longer apply.
#if Edition == "server"
Type: files; Name: "{group}\Servidor e painel.lnk"; Check: UsesService
Type: files; Name: "{group}\SLT Disk Manager - Painel.lnk"; Check: not UsesService
#else
Type: files; Name: "{group}\Cliente Windows.lnk"
#endif

[Icons]
#if Edition == "server"
; With the Windows service: the panel talks to the service and must read connection.json, which only
; Administrators and SYSTEM may read. The shortcut asks for elevation (flag set in [Code]).
Name: "{group}\SLT Disk Manager - Painel"; Filename: "{app}\desktop\SLT.Desktop.exe"; Parameters: "--connection ""{code:ServiceConnection}"""; WorkingDir: "{app}\desktop"; IconFilename: "{app}\branding\slt-disk-manager.ico"; Check: UsesService
Name: "{userdesktop}\SLT Disk Manager - Painel"; Filename: "{app}\desktop\SLT.Desktop.exe"; Parameters: "--connection ""{code:ServiceConnection}"""; WorkingDir: "{app}\desktop"; IconFilename: "{app}\branding\slt-disk-manager.ico"; Tasks: desktopicon; Check: UsesService
; Without the service: portable server plus panel. Servidor.cmd itself opens the service panel if a service appears later.
Name: "{group}\Servidor e painel"; Filename: "{app}\Servidor.cmd"; WorkingDir: "{app}"; IconFilename: "{app}\branding\slt-disk-manager.ico"; Check: not UsesService
Name: "{userdesktop}\SLT Disk Manager - Servidor e painel"; Filename: "{app}\Servidor.cmd"; WorkingDir: "{app}"; IconFilename: "{app}\branding\slt-disk-manager.ico"; Tasks: desktopicon; Check: not UsesService
#else
Name: "{group}\SLT Disk Manager - Cliente"; Filename: "{app}\Cliente.cmd"; WorkingDir: "{app}"; IconFilename: "{app}\branding\slt-client.ico"
Name: "{userdesktop}\SLT Disk Manager - Cliente"; Filename: "{app}\Cliente.cmd"; WorkingDir: "{app}"; IconFilename: "{app}\branding\slt-client.ico"; Tasks: desktopicon
Name: "{group}\Reiniciar em modo SLT Disk Manager"; Filename: "{app}\Modo-SLT.cmd"; WorkingDir: "{app}"; IconFilename: "{app}\branding\slt-mode.ico"
#endif
Name: "{group}\Guia de uso ({#EditionName})"; Filename: "{app}\QUICKSTART.md"; IconFilename: "{app}\branding\{#EditionIcon}"

[Run]
; The service scripts re-run themselves in the 64-bit PowerShell (this setup is a 32-bit process),
; so the service lands in "Program Files" and not in "Program Files (x86)".
#if Edition == "server"
Filename: "powershell.exe"; Parameters: "-NoProfile -ExecutionPolicy Bypass -File ""{app}\Executar-Instalacao.ps1"" -Script ""install-server\Install-Server.ps1"" -PackageDirectory ""{app}\server"" -Bind {code:ServerBind} -AllowedSubnet {code:ServerSubnet} -RepositoryDirectory ""{code:ServerRepository}"""; Verb: runas; Flags: shellexec waituntilterminated; Tasks: serverservice; StatusMsg: "Instalando o serviço do servidor e as regras de firewall da rede privada (o Windows pedirá permissão de administrador)..."
Filename: "powershell.exe"; Parameters: "-NoProfile -ExecutionPolicy Bypass -File ""{app}\Executar-Instalacao.ps1"" -Script ""install-server\Update-Server.ps1"" -PackageDirectory ""{app}\server"""; Verb: runas; Flags: shellexec waituntilterminated; Tasks: serverupdate; StatusMsg: "Atualizando o serviço do servidor (o Windows pedirá permissão de administrador)..."
#else
Filename: "powershell.exe"; Parameters: "-NoProfile -ExecutionPolicy Bypass -File ""{app}\Executar-Instalacao.ps1"" -Script ""install-agent\Install-Agent.ps1"" -PackageDirectory ""{app}\client""{code:ClientArguments}"; Verb: runas; Flags: shellexec waituntilterminated; Tasks: clientservice; StatusMsg: "Conectando ao servidor e instalando o serviço do cliente (o Windows pedirá permissão de administrador)..."
Filename: "powershell.exe"; Parameters: "-NoProfile -ExecutionPolicy Bypass -File ""{app}\Executar-Instalacao.ps1"" -Script ""install-agent\Update-Agent.ps1"" -PackageDirectory ""{app}\client"""; Verb: runas; Flags: shellexec waituntilterminated; Tasks: clientupdate; StatusMsg: "Atualizando o serviço do cliente (o Windows pedirá permissão de administrador)..."
#endif

; Services and firewall rules are created only by the optional task above, after the
; operator fills the page and accepts the Windows elevation prompt. No boot, network
; adapter or disk operation happens here. Uninstall removes installed files; generated
; state and customer data are retained.

[Code]
var
  ServicePage: TInputQueryWizardPage;

const
#if Edition == "server"
  ServiceKey = 'SYSTEM\CurrentControlSet\Services\SLTDiskManagerController';
#else
  ServiceKey = 'SYSTEM\CurrentControlSet\Services\SLTWindowsAgent';
#endif

// Test hook for the installer smoke test only (/SLTTESTSERVICESTATE="folder"): behaves as if the
// service were installed with that state folder, without creating or touching any service.
function TestServiceState(): String;
begin
  Result := ExpandConstant('{param:SLTTESTSERVICESTATE|}');
end;

function ServiceExists(): Boolean;
begin
  Result := RegKeyExists(HKEY_LOCAL_MACHINE, ServiceKey);
end;

#if Edition == "server"
function UsesService(): Boolean;
begin
  Result := (TestServiceState() <> '') or ServiceExists() or WizardIsTaskSelected('serverservice');
end;

// connection.json of the service: the state folder named in the service command line, or the default.
function ServiceConnection(Param: String): String;
var ImagePath, Rest: String; P: Integer;
begin
  Result := ExpandConstant('{commonappdata}\SLT Disk Manager\Controller');
  if TestServiceState() <> '' then Result := TestServiceState()
  else if RegQueryStringValue(HKEY_LOCAL_MACHINE, ServiceKey, 'ImagePath', ImagePath) then begin
    P := Pos('--state-dir "', ImagePath);
    if P > 0 then begin
      Rest := Copy(ImagePath, P + Length('--state-dir "'), MaxInt);
      P := Pos('"', Rest);
      if P > 1 then Result := Copy(Rest, 1, P - 1);
    end;
  end;
  Result := RemoveBackslash(Result) + '\connection.json';
end;

// Marks a shortcut "Run as administrator" (SLDF_RUNAS_USER, bit 0x20 of byte 0x15 of the .lnk).
// Chosen over weakening the ACL of connection.json: the administrative token stays readable
// only by Administrators and SYSTEM, and Windows shows its usual consent prompt once per start.
procedure RequireElevation(const Shortcut: String);
var Data: AnsiString;
begin
  if not LoadStringFromFile(Shortcut, Data) then Exit;
  if Length(Data) < $16 then Exit;
  Data[$16] := Chr(Ord(Data[$16]) or $20);
  SaveStringToFile(Shortcut, Data, False);
end;

procedure CurStepChanged(CurStep: TSetupStep);
begin
  if (CurStep = ssPostInstall) and UsesService() then begin
    RequireElevation(ExpandConstant('{group}\SLT Disk Manager - Painel.lnk'));
    RequireElevation(ExpandConstant('{userdesktop}\SLT Disk Manager - Painel.lnk'));
  end;
end;
#endif

function IsDigits(const S: String): Boolean;
var I: Integer;
begin
  Result := Length(S) > 0;
  for I := 1 to Length(S) do
    if (S[I] < '0') or (S[I] > '9') then Result := False;
end;

function IsIPv4(const S: String): Boolean;
var Rest, Part: String; Count, P: Integer;
begin
  Result := False; Rest := S; Count := 0;
  while Rest <> '' do begin
    P := Pos('.', Rest);
    if P > 0 then begin Part := Copy(Rest, 1, P - 1); Delete(Rest, 1, P); if Rest = '' then Exit; end
    else begin Part := Rest; Rest := ''; end;
    if (not IsDigits(Part)) or (Length(Part) > 3) or (StrToInt(Part) > 255) then Exit;
    Count := Count + 1;
  end;
  Result := Count = 4;
end;

function IsHex64(const S: String): Boolean;
var I: Integer;
begin
  Result := Length(S) = 64;
  for I := 1 to Length(S) do
    if Pos(Lowercase(S[I]), '0123456789abcdef') = 0 then Result := False;
end;

function InitializeSetup(): Boolean;
begin
  // .NET runtime and the VC runtime it needs are inside the package; Windows 10/11 provide the UCRT.
  Result := FileExists(ExpandConstant('{sys}\ucrtbase.dll'));
  if not Result then
    MsgBox('Este Windows não tem o Universal C Runtime (UCRT). Atualize o Windows 10/11 pelo Windows Update e execute o instalador novamente.', mbCriticalError, MB_OK);
end;

procedure InitializeWizard();
begin
#if Edition == "server"
  ServicePage := CreateInputQueryPage(wpSelectTasks, 'Serviço do servidor', 'Rede privada da bancada e pasta do repositório',
    'Informe o IPv4 FIXO deste servidor e a sub-rede autorizada. Fixe o IP antes (reserva de DHCP no roteador ou IP manual no Windows): o serviço escuta só nesse IP.' + #13#10 +
    'No repositório ficam as imagens de disco, as mídias ISO/WIM/ESD importadas e os perfis de boot: escolha uma pasta em um disco com bastante espaço livre.');
  ServicePage.Add('IPv4 fixo do servidor (ex.: 192.168.1.10):', False);
  ServicePage.Add('Sub-rede autorizada em CIDR (ex.: 192.168.1.0/24):', False);
  ServicePage.Add('Pasta do repositório (imagens e mídias):', False);
#else
  ServicePage := CreateInputQueryPage(wpSelectTasks, 'Serviço do cliente', 'Servidor da bancada',
    'Deixe os dois campos em branco: o cliente procura o servidor na rede e mostra um código de 8 dígitos para você digitar no painel do servidor.' + #13#10 +
    'Preencha só se a rede bloquear a busca automática (endereço) ou se o servidor for de uma versão anterior (endereço e pin).');
  ServicePage.Add('Endereço do servidor — opcional (ex.: https://192.168.1.10:7443):', False);
  ServicePage.Add('Pin SHA-256 do servidor — opcional (64 caracteres):', False);
#endif
end;

#if Edition == "server"
// The default repository sits on the same disk chosen for the program, never on its folder.
procedure CurPageChanged(CurPageID: Integer);
begin
  if (CurPageID = ServicePage.ID) and (Trim(ServicePage.Values[2]) = '') then
    ServicePage.Values[2] := AddBackslash(ExtractFileDrive(WizardDirValue())) + 'SLT Disk Manager Repositorio';
end;

function ValidRepository(const Path: String): Boolean;
var Full, App: String;
begin
  Full := RemoveBackslash(Path); App := RemoveBackslash(WizardDirValue());
  Result := (Length(Full) > 3) and (Pos(':\', Full) = 2) and (Pos('"', Full) = 0) and
    (CompareText(Full, App) <> 0) and (CompareText(Copy(Full, 1, Length(App) + 1), App + '\') <> 0);
end;
#endif

function ShouldSkipPage(PageID: Integer): Boolean;
begin
#if Edition == "server"
  Result := (PageID = ServicePage.ID) and not WizardIsTaskSelected('serverservice');
#else
  Result := (PageID = ServicePage.ID) and not WizardIsTaskSelected('clientservice');
#endif
end;

function NextButtonClick(CurPageID: Integer): Boolean;
var Cidr, Url: String; Slash: Integer;
begin
  Result := True;
  if CurPageID <> ServicePage.ID then Exit;
#if Edition == "server"
  Cidr := Trim(ServicePage.Values[1]); Slash := Pos('/', Cidr);
  if (not IsIPv4(Trim(ServicePage.Values[0]))) or (Slash = 0) or (not IsIPv4(Copy(Cidr, 1, Slash - 1))) or (not IsDigits(Copy(Cidr, Slash + 1, 2))) then begin
    MsgBox('Informe um IPv4 e uma sub-rede CIDR válidos.', mbError, MB_OK); Result := False;
  end
  else if not ValidRepository(Trim(ServicePage.Values[2])) then begin
    MsgBox('Informe uma pasta para o repositório com caminho completo (por exemplo D:\SLT Disk Manager Repositorio), que não seja a raiz da unidade nem fique dentro da pasta do programa.', mbError, MB_OK); Result := False;
  end;
#else
  Url := Trim(ServicePage.Values[0]);
  if (Url = '') and (Trim(ServicePage.Values[1]) <> '') then begin
    MsgBox('O pin só é usado junto com o endereço do servidor.', mbError, MB_OK); Result := False;
  end
  else if (Url <> '') and ((Pos('https://', Url) <> 1) or (Pos(' ', Url) > 0) or (Pos('"', Url) > 0)) then begin
    MsgBox('O endereço do servidor deve começar com https://, sem espaços.', mbError, MB_OK); Result := False;
  end
  else if (Trim(ServicePage.Values[1]) <> '') and (not IsHex64(Trim(ServicePage.Values[1]))) then begin
    MsgBox('O pin tem 64 caracteres hexadecimais. Deixe em branco para confirmar pelo código de 8 dígitos.', mbError, MB_OK); Result := False;
  end;
#endif
end;

#if Edition == "server"
function ServerBind(Param: String): String; begin Result := Trim(ServicePage.Values[0]); end;
function ServerSubnet(Param: String): String; begin Result := Trim(ServicePage.Values[1]); end;
function ServerRepository(Param: String): String; begin Result := RemoveBackslash(Trim(ServicePage.Values[2])); end;
#else
// Empty when the client should look for the server by itself.
function ClientArguments(Param: String): String;
begin
  Result := '';
  if Trim(ServicePage.Values[0]) <> '' then Result := ' -Server "' + Trim(ServicePage.Values[0]) + '"';
  if Trim(ServicePage.Values[1]) <> '' then Result := Result + ' -ServerPin ' + Lowercase(Trim(ServicePage.Values[1]));
end;
#endif
