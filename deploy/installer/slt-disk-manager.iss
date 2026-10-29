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

[Tasks]
Name: "serverservice"; Description: "Instalar o servidor como serviço do Windows e liberar a porta 7443 só na rede privada (pede permissão de administrador)"; Components: server; Flags: unchecked
Name: "clientservice"; Description: "Instalar o cliente como serviço do Windows, com inventário e SMART automáticos (pede permissão de administrador)"; Components: client; Flags: unchecked

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

[Run]
Filename: "powershell.exe"; Parameters: "-NoProfile -ExecutionPolicy Bypass -File ""{app}\install-server\Install-Server.ps1"" -PackageDirectory ""{app}\server"" -Bind {code:ServerBind} -AllowedSubnet {code:ServerSubnet}"; Verb: runas; Flags: shellexec waituntilterminated; Tasks: serverservice; StatusMsg: "Instalando o serviço do servidor e a regra de firewall da rede privada (o Windows pedirá permissão de administrador)..."
Filename: "powershell.exe"; Parameters: "-NoProfile -ExecutionPolicy Bypass -File ""{app}\install-agent\Install-Agent.ps1"" -PackageDirectory ""{app}\client"" -Server {code:ClientServer} -ServerPin {code:ClientPin}"; Verb: runas; Flags: shellexec waituntilterminated; Tasks: clientservice; StatusMsg: "Instalando o serviço do cliente com SMART (o Windows pedirá permissão de administrador)..."

; Services and firewall rules are created only by the optional tasks above, after the
; operator fills the pages and accepts the Windows elevation prompt. No boot, network
; adapter or disk operation happens here. Uninstall removes installed files; generated
; state and customer data are retained.

[Code]
var
  ServerPage, ClientPage: TInputQueryWizardPage;

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
  ServerPage := CreateInputQueryPage(wpSelectTasks, 'Serviço do servidor', 'Rede privada da bancada',
    'Informe o IPv4 deste servidor na rede da assistência e a sub-rede autorizada. O serviço escuta só nesse IP, porta 7443, e o firewall libera apenas a rede privada.');
  ServerPage.Add('IPv4 do servidor (ex.: 192.168.1.10):', False);
  ServerPage.Add('Sub-rede autorizada em CIDR (ex.: 192.168.1.0/24):', False);
  ClientPage := CreateInputQueryPage(ServerPage.ID, 'Serviço do cliente', 'Servidor da bancada',
    'Informe o endereço e o pin SHA-256 exibidos no servidor. Confira o pin pessoalmente: ele garante que este cliente fala com o servidor certo.');
  ClientPage.Add('Endereço do servidor (ex.: https://192.168.1.10:7443):', False);
  ClientPage.Add('Pin SHA-256 do servidor (64 caracteres):', False);
end;

function ShouldSkipPage(PageID: Integer): Boolean;
begin
  Result := ((PageID = ServerPage.ID) and not WizardIsTaskSelected('serverservice')) or
            ((PageID = ClientPage.ID) and not WizardIsTaskSelected('clientservice'));
end;

function NextButtonClick(CurPageID: Integer): Boolean;
var Cidr, Url: String; Slash: Integer;
begin
  Result := True;
  if CurPageID = ServerPage.ID then begin
    Cidr := Trim(ServerPage.Values[1]); Slash := Pos('/', Cidr);
    if (not IsIPv4(Trim(ServerPage.Values[0]))) or (Slash = 0) or (not IsIPv4(Copy(Cidr, 1, Slash - 1))) or (not IsDigits(Copy(Cidr, Slash + 1, 2))) then begin
      MsgBox('Informe um IPv4 e uma sub-rede CIDR válidos.', mbError, MB_OK); Result := False;
    end;
  end;
  if CurPageID = ClientPage.ID then begin
    Url := Trim(ClientPage.Values[0]);
    if (Pos('https://', Url) <> 1) or (Pos(' ', Url) > 0) or (Pos('"', Url) > 0) or (not IsHex64(Trim(ClientPage.Values[1]))) then begin
      MsgBox('Informe o endereço https:// do servidor e o pin de 64 caracteres hexadecimais.', mbError, MB_OK); Result := False;
    end;
  end;
end;

function ServerBind(Param: String): String; begin Result := Trim(ServerPage.Values[0]); end;
function ServerSubnet(Param: String): String; begin Result := Trim(ServerPage.Values[1]); end;
function ClientServer(Param: String): String; begin Result := Trim(ClientPage.Values[0]); end;
function ClientPin(Param: String): String; begin Result := Lowercase(Trim(ClientPage.Values[1])); end;
