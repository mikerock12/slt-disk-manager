# Network Boot v1

JSON camelCase. Administração usa autenticação atual em `/api/v1/network-boot`.
GET status retorna `{state,config,startedAtUtc,lastError,profiles,events}`; state
STOPPED/STARTING/RUNNING/FAILED. Sem auto-start persistido.

Config: `{schemaVersion:1,interfaceId,bindIPv4,allowedCidr,mode:"EXTERNAL_DHCP",httpPort:8088,maxConcurrentTransfers:8}`.
PUT config salva somente parado. POST validate `{}` retorna `{valid,errors:string[],warnings:string[]}`.
POST start `{confirmIsolatedNetwork:true}` e stop `{}` retornam status.
GET interfaces retorna `[{id,name,description,ipv4,cidr,speedMbps,eligible,reason}]`.
GET profiles retorna BootProfile[]; POST profiles/import `{directoryPath}` importa
netboot-manifest.json local, parado. PUT profiles/{id} `{enabled:bool}`.
GET events retorna `[{timestampUtc,event,message}]` (anel limitado; eventos
administrativos também no audit do Controller).

BootProfile: `{id,displayName,kind,architecture,firmwareSupport:string[],secureBootStatus,
validationStatus,version,enabled,artifacts:BootArtifact[]}`.
BootArtifact: `{id,relativePath,sizeBytes,sha256,source}`. Manifesto de importação
contém schemaVersion=1 e mesmos campos do perfil, sem enabled obrigatório.
Tipos aceitos RESCUE_LINUX (kernel,initrd,rootfs), IPXE_BOOTSTRAP (bios,uefi).
IDs de artefatos são locais ao perfil; HTTP usa `{profileId}-{artifactId}`.
IDs `[a-z0-9][a-z0-9-]{0,63}`. firmwareSupport BIOS e UEFI, architecture X64;
secureBootStatus UNSUPPORTED. Sem argumentos/URLs/comandos fornecidos pelo manifesto.
Importação nunca confia em validationStatus recebido: começa NETWORK_BOOT_DEV.

Porta pública: GET/HEAD `/boot/v1/menu.ipxe`, `/boot/v1/assets/{id}`,
GET `/boot/v1/health` mínimo. Não serve API/agent/webroot. TFTP UDP69 somente
undionly.kpxe, ipxe.efi, autoexec.ipxe; sem WRQ ou caminhos. DHCP externo deve
informar next-server e selecionar arquitetura; user-class iPXE recebe URL do menu.
