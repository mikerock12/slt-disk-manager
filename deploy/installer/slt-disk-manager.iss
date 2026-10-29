#ifndef PackageDir
  #error PackageDir is required
#endif
#ifndef AppVersion
  #define AppVersion "0.1.0-beta.5.1"
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
#elif Edition == "client"
  #define EditionName "Cliente"
  #define EditionId "{{6B3F0D52-8E4A-4C71-9A2D-5F1C7E83B904}"
  #define EditionDir "SLT Disk Manager Cliente"
#else
  #error Edition must be server or client
#endif

[Setup]
AppId={#EditionId}
AppName=SLT Disk Manager {#EditionName}
AppVersion={#AppVersion}
VersionInfoVersion=0.1.0.51
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

[Languages]
Name: "brazilianportuguese"; MessagesFile: "compiler:Languages\BrazilianPortuguese.isl"

[Tasks]
#if Edition == "server"
Name: "serverservice"; Description: "Instalar o servidor como serviço do Windows e liberar a porta 7443 só na rede privada (pede permissão de administrador)"; Flags: unchecked
#else
Name: "clientservice"; Description: "Instalar o cliente como serviço do Windows, com inventário e SMART automáticos (pede permissão de administrador)"; Flags: unchecked
#endif

[Files]
#if Edition == "server"
Source: "{#PackageDir}\server\*"; DestDir: "{app}\server"; Flags: ignoreversion recursesubdirs createallsubdirs
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
Source: "{#PackageDir}\Iniciar-Cliente.ps1"; DestDir: "{app}"; Flags: ignoreversion
Source: "{#PackageDir}\install-agent\*"; DestDir: "{app}\install-agent"; Flags: ignoreversion recursesubdirs createallsubdirs
#endif
Source: "{#PackageDir}\docs\*"; DestDir: "{app}\docs"; Flags: ignoreversion recursesubdirs createallsubdirs
Source: "{#PackageDir}\README.md"; DestDir: "{app}"; Flags: ignoreversion
Source: "{#PackageDir}\QUICKSTART.md"; DestDir: "{app}"; Flags: ignoreversion
Source: "{#PackageDir}\LICENSE"; DestDir: "{app}"; Flags: ignoreversion

[Icons]
#if Edition == "server"
Name: "{group}\Servidor e painel"; Filename: "{app}\Servidor.cmd"; WorkingDir: "{app}"
#else
Name: "{group}\Cliente Windows"; Filename: "{app}\Cliente.cmd"; WorkingDir: "{app}"
#endif
Name: "{group}\Guia de uso ({#EditionName})"; Filename: "{app}\QUICKSTART.md"

[Run]
#if Edition == "server"
Filename: "powershell.exe"; Parameters: "-NoProfile -ExecutionPolicy Bypass -File ""{app}\install-server\Install-Server.ps1"" -PackageDirectory ""{app}\server"" -Bind {code:ServerBind} -AllowedSubnet {code:ServerSubnet}"; Verb: runas; Flags: shellexec waituntilterminated; Tasks: serverservice; StatusMsg: "Instalando o serviço do servidor e a regra de firewall da rede privada (o Windows pedirá permissão de administrador)..."
#else
Filename: "powershell.exe"; Parameters: "-NoProfile -ExecutionPolicy Bypass -File ""{app}\install-agent\Install-Agent.ps1"" -PackageDirectory ""{app}\client"" -Server {code:ClientServer} -ServerPin {code:ClientPin}"; Verb: runas; Flags: shellexec waituntilterminated; Tasks: clientservice; StatusMsg: "Instalando o serviço do cliente com SMART (o Windows pedirá permissão de administrador)..."
#endif

; Services and firewall rules are created only by the optional task above, after the
; operator fills the page and accepts the Windows elevation prompt. No boot, network
; adapter or disk operation happens here. Uninstall removes installed files; generated
; state and customer data are retained.

[Code]
var
  ServicePage: TInputQueryWizardPage;

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
  ServicePage := CreateInputQueryPage(wpSelectTasks, 'Serviço do servidor', 'Rede privada da bancada',
    'Informe o IPv4 FIXO deste servidor na rede da assistência e a sub-rede autorizada. Fixe o IP antes (reserva de DHCP no roteador ou IP manual no Windows): o serviço escuta só nesse IP, porta 7443, e os clientes guardam esse endereço.');
  ServicePage.Add('IPv4 fixo do servidor (ex.: 192.168.1.10):', False);
  ServicePage.Add('Sub-rede autorizada em CIDR (ex.: 192.168.1.0/24):', False);
#else
  ServicePage := CreateInputQueryPage(wpSelectTasks, 'Serviço do cliente', 'Servidor da bancada',
    'Informe o endereço e o pin SHA-256 exibidos no servidor. Confira o pin pessoalmente: ele garante que este cliente fala com o servidor certo.');
  ServicePage.Add('Endereço do servidor (ex.: https://192.168.1.10:7443):', False);
  ServicePage.Add('Pin SHA-256 do servidor (64 caracteres):', False);
#endif
end;

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
  end;
#else
  Url := Trim(ServicePage.Values[0]);
  if (Pos('https://', Url) <> 1) or (Pos(' ', Url) > 0) or (Pos('"', Url) > 0) or (not IsHex64(Trim(ServicePage.Values[1]))) then begin
    MsgBox('Informe o endereço https:// do servidor e o pin de 64 caracteres hexadecimais.', mbError, MB_OK); Result := False;
  end;
#endif
end;

#if Edition == "server"
function ServerBind(Param: String): String; begin Result := Trim(ServicePage.Values[0]); end;
function ServerSubnet(Param: String): String; begin Result := Trim(ServicePage.Values[1]); end;
#else
function ClientServer(Param: String): String; begin Result := Trim(ServicePage.Values[0]); end;
function ClientPin(Param: String): String; begin Result := Lowercase(Trim(ServicePage.Values[1])); end;
#endif
