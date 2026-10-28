# Instalação Windows em paralelo aos clones

Mídia indicada pelo proprietário: ISOs/Windows 10.iso (ESD interno). Nunca publicar
ISO/ESD/WIM nem enviá-los a CI público. Backend extrai/importa localmente, lê metadados
WIM/ESD e calcula hashes; não presume autenticidade Microsoft só pelo nome.

## Agente WinPE

Executável Go novo `src/rescue-agent/cmd/slt-winpe-agent`, usando TLS/CSR/mTLS/assinatura
do pacote interno atual. NÃO usar .NET no WinPE. Reusar transporte, enrollment por
mode WINPE (helper EnrollMode novo), claim job e reporter. Refusar executar fora de
WindowsPE (registro MiniNT + wpeutil) antes de anunciar capacidade destrutiva.
Modo separado WINPE, certificado próprio; aprovar fisicamente pareamento.

Inventory mesmas DTOs, agentVersion0.1.0, mode WINPE, capabilities INVENTORY e DEPLOY_WINDOWS.
DiskInfo físico via IOCTL Windows somente leitura: size,logical/physical sectors,model,
serial e fingerprint idêntico ao algoritmo comum. Não inferir identidade de Disk0.
Nenhum disco sem serial/identidade ou protegido é elegível. Proteger disco que contém
executável/state/mídia WinPE persistente; RAM X: não é um destino físico.
Fixtures de testes devem ser explicitamente sintéticas e nunca abrir physicaldrives.

JobPlan acrescenta `deployment:DeploymentOptions?`:
`DeploymentOptions` {mediaId:string,imageIndex:int,firmware:"GPT_UEFI"|"MBR_BIOS",rebootAfterInstall:bool}.
Kind DEPLOY_WINDOWS tem diskPath/fingerprint/size, consent e challenge APAGAR... normais.
ROOTBACKEND só agenda para modeWINPE com cap apropriada e mídiaAVAILABLE, índice existente,
arquiteturaAMD64, espaço>=mínimo. Revalida fingerprint imediatamente antes de particionar.
Agente pede novamente a confirmação digitada LOCALMENTE; --allow-deployment obrigatório.

GET `/agent/v1/jobs/{id}/media` -> DeploymentMedia
GET `/agent/v1/jobs/{id}/media/chunks/{seq}` -> bytes4MiB com hash emmanifest.
`DeploymentMedia` {id,name,fileName,sizeBytes:long,sha256,chunks:ImageChunk[],images:WindowsImage[],state,createdAtUtc}.
`WindowsImage` {index:int,name,description,architecture:string,version:string,minimumSizeBytes:long}.
Media importada deve ter headerMSWIM e metadadoslimitados/validados; formatosWIM/ESD.

Download mídia para --scratch-dir em armazenamento saudável FORA do disco destino,
confere cada chunk e SHA256 completo, sem reutilizar parcial não validado. Não
usar RAM X: para ESD de4GiB por padrão; requer USB/segundo disco/NAS autorizado com espaço.
Native Storage deve confirmar que scratch/state/exe não pertencem ao destino.
Gerar script DiskPart somente com número físico revalidado e letras livres calculadas;
não aceitar scripts/comandos vindos do servidor. GPT: EFI260MiB FAT32, MSR16MiB,
WindowsNTFS no restante. MBR: sistemaNTFS550MiB ativo e WindowsNTFS restante.
Aplicar por DISM /Apply-Image /ImageFile:<staging> /Index:N /ApplyDir:<WindowsVolume>.
Criar boot via BCDBoot /s <SystemVolume> /f UEFI|BIOS. BIOS requer bootsect /nt60 /mbr
da mídia legítima; bloquear se ferramenta ausente. Nunca contornar ativação.
Erros e cancelamento interrompem entre etapas e NÃO marcam boot validado.
DISM pode não ser cancelável com destino íntegro; informar partial disk em erro.
Reboot opcional só após reportar resultado ao servidor; primeiroboot/OOBE ainda local.

## Bootstrap sem redistribuir Windows/ADK

Preparar kit para pendrive do instalador oficial já criado pelo operador, com pasta
SLT contendo agente e configuração públicaURL/pin. Autounattend.xml inicia rede e
agente no passewindowsPE; nenhuma senha/chave embutida. O processo de Setup deve
aguardar agente e nunca prosseguir instalação implícita após falha. Incluir fallback
manual documentado em cmdWinPE. Nenhum artefato Microsoft versionado/publicado.

## APIs administrativas e painel

POST `/api/v1/media/import` {path:string,name:string} -> DeploymentMedia estadoIMPORTING.
Somente adminloopback. Path local ISO/WIM/ESD escolhido porfilepicker. ISO usa7z
opcional/caminho configurado, sem montagem sistema; retorna erroexplicito se ausente.
GET `/api/v1/media` -> DeploymentMedia[] (índices/ediçõesquandoAVAILABLE).
POST `/api/v1/jobs/plan` {kind:"DEPLOY_WINDOWS",deviceId,diskFingerprint,consent,
deployment:{mediaId,imageIndex,firmware,rebootAfterInstall}} ->Job comchallenge.
Painel escolhe cliente3WinPE e disco, edição, firmware, autorização e confirmaapagar.
Jobs em clientes1/2 continuam; locks só por máquina e dispositivo, nunca global.

## Responsáveis

- Desktop agent agora: NOVOS arquivos Go winpe_common.go,winpe_windows.go,winpe_other.go,
winpe_test.go, cmd/slt-winpe-agent/main.go; rescue/windows-pe/ bootstrapkit e aba Deployment WPF.
- Rescue agent mantém contratos/client/security: acrescenta DeploymentOptions emPlan,
EnrollMode com Enroll delegandoRESCUE e VerifyAssignment aceitando DEPLOY_WINDOWS validado.
- Coordenador: media/model/controller/API/import/CI/embalagem e testesdeintegração.
- Não tocar arquivosdecolegas semcoordenação. Não declararbootWindows/hardwarehomologado
sem execuçãoreal. TestesWinPEcomponentes viafixtures/mocks; ISOmídialegal deusuário sólocal.

## Conta inicial, Linux e conversão GPT (versão completa)

`DeploymentOptions.account` opcional: {computerName (1–15, letras/números/hífen),
userName (1–20 minúsculas, número, _ ou -; nomes reservados bloqueados), locale}. O
servidor gera a senha temporária (14 caracteres alfanuméricos com maiúscula, minúscula e
dígito) e a devolve uma única vez em `Job.temporaryPassword`; ela nunca vai para o log.
Windows: o plano assinado leva a senha, o WinPE grava `%WINDIR%\Panther\unattend.xml`
(usuário local em Administradores/Administradores conforme o idioma da edição, OOBE
pulado, ABNT2/pt-BR, `powercfg /hibernate off` no specialize) e o Setup a mascara após uso.
Linux: o plano leva só o hash SHA-512-crypt; o agente usa `chpasswd -e` e `chage -d 0`.

DEPLOY_LINUX: mídia ISO Ubuntu/Xubuntu em camadas (casper) importada com `kind:"LINUX"`
(a ISO inteira é o payload, conferida por chunks e SHA256). Cliente SLT Rescue com
gravação habilitada anuncia DEPLOY_LINUX. O agente exige o firmware do boot igual ao do
plano, confirmação local `APAGAR`, cria GPT (ESP 512 MiB + ext4) ou MBR (ext4 ativa),
guarda a ISO na própria raiz nova, extrai a camada padrão de `install-sources.yaml`,
instala grub-efi/shim do repositório da própria ISO (sem internet), grava fstab por UUID,
hostname, fuso America/Sao_Paulo, teclado ABNT2, cria a conta, gera initramfs, instala o
bootloader (NVRAM e caminho removível) e apaga a ISO da raiz antes de concluir.

CONVERT_GPT: cliente WinPE iniciado em UEFI anuncia CONVERT_GPT. O plano exige
`deployment:{mediaId,imageIndex,firmware:"GPT_UEFI"}` de mídia Windows; o servidor extrai
`MBR2GPT.EXE` dessa edição e assina seu SHA256 em `deployment.toolSha256`
(GET `/agent/v1/jobs/{id}/tool`). O agente valida (`/validate`) antes de pedir a
confirmação local `CONVERTER`, converte (`/convert`), relê a tabela e exige GPT com ESP e
volume Windows. Dados e partições são preservados; o boot UEFI deve ser conferido.

Os agentes reportam a fase `AWAITING_LOCAL_CONFIRMATION` enquanto aguardam o teclado do cliente.
