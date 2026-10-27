# Contratos da entrega de clonagem e arquivos

Branch feat/adaptive-clone-files, após merge PR1/CI verde. JSON camelCase, protocolos
anteriores mantidos; novos campos opcionais nos DTOs existentes. Nenhum shell remoto.

## Clonagem integral

DiskInfo acrescenta `layout: DiskLayout?`; ImageManifest acrescenta `layout: DiskLayout?`.
`DiskLayout`: {tableType:"GPT"|"MBR",sectorSize:int,diskIdentifier:string,firstUsableLba:long,lastUsableLba:long,partitions:LayoutPartition[]}.
`LayoutPartition`: {number:int,startLba:long,lengthLba:long,type:string,uuid:string,name:string,attributes:string,bootable:bool,fileSystem:string,minimumSizeBytes:long?}.
minimumSizeBytes é estimativa para preview; não autoriza truncamento. Partições desconhecidas são copiadas integralmente.

PlanRequest e JobPlan acrescentam `clone: CloneOptions?` e `file: FileRequest?`.
`CloneOptions`: {strategy:"EXACT_RAW"|"PRESERVE_LAYOUT"|"FIT",resizePartitionNumber:int}.
CAPTURE mantém RAW de disco inteiro. RESTORE usa opções clone assinadas e layout no manifesto.
POST capture-complete admite {layout:DiskLayout?} coletado/revalidado durante captura;
backend guarda esse layout real e recalcula preview antes de planejar destino.
EXACT_RAW exige mesma capacidade. PRESERVE_LAYOUT permite maior e corrige GPT secundária.
FIT preserva todas as partições/identificadores/boot/tipo de tabela; redimensiona somente
a partição NTFS/ext4 selecionada no preview, deslocando partições posteriores sem descartá-las.
Executa adaptação SOMENTE em cópia de trabalho local, valida tabela/filesystems/hashes,
e só então abre destino. Origem e imagem do servidor são imutáveis. Requer --scratch-dir
em armazenamento saudável e capacidade verificada. Não alterar disco origem para shrink.
MBR primário/GPT convencional inicialmente; bloquear EBR/híbrido/LVM/RAID/criptografia,
NTFS hibernado/inconsistente, setor diferente e tamanho mínimo não comprovado.
RESTORE físico continua exigindo --allow-physical-write e confirmação local contextual.
Plan deve reprovar adaptação sem capability RESTORE_FIT/RESTORE_LAYOUT do destino.

POST `/api/v1/clones/plan` {sourceDeviceId,sourceDiskFingerprint,targetDeviceId,targetDiskFingerprint,strategy,consent} -> {session:CloneSession,captureJob:Job,preview:ClonePreview}.
`ClonePreview`: {blockers:string[],warnings:string[],actions:string[],sourceLayout:DiskLayout?,targetLayout:DiskLayout?,resizePartitionNumber:int,scratchBytesRequired:long}.
`CloneSession`: {id,sourceDeviceId,sourceDiskFingerprint,targetDeviceId,targetDiskFingerprint,strategy,captureJobId,targetJobId:string?,imageId,state,createdAtUtc,preview}.
GET `/api/v1/clones` -> CloneSession[] com estado derivado dos jobs.
POST `/api/v1/clones/{id}/prepare-target` {} -> Job de RESTORE, somente após captura COMPLETED e imagem AVAILABLE. Aprovação target pelo endpoint normal exige nova confirmação digitada.
Estados: AWAITING_SOURCE_APPROVAL, CAPTURING, IMAGE_READY, AWAITING_TARGET_APPROVAL, RESTORING, COMPLETED, FAILED.
Vários clones/instalações em PCs distintos são permitidos; locks por máquina/disco e não globais.

## Escopos de arquivos e jobs

Agente Windows: `--file-roots C:\caminho\scopes.json` com array local
`[{id,label,path,allowRead,allowWrite}]`; nenhum acesso a arquivos anunciado sem scopes.
Inventory acrescenta `fileRoots:FileRootInfo[]?`, onde FileRootInfo={id,label,canRead,canWrite}.
Não transmitir paths absolutos dos scopes. Excluir estado/credenciais/binários do agente.
Capabilities: FILE_LIST, FILE_BACKUP, FILE_RESTORE conforme scope e permissões.

`FileRequest`: {rootId:string,relativePath:string,paths:string[],bundleId:string,name:string,cursor:string?,pageSize:int}.
Todos os campos fazem parte do payload assinado. Relativos em '/' sem '..', '.', raiz,
drive, ADS, UNC, NUL ou nomes Windows reservados. Não seguir reparse/junction/symlink.
FILE_BACKUP preserva caminhos relativos à raiz; aceita seleção de arquivos/pastas,
inclui diretórios vazios, aborta arquivo instável e não promete VSS de arquivos abertos.
FILE_RESTORE restaura sob uma pasta NOVA indicada em relativePath (não vazia); staging
privado no mesmo volume, valida manifesto e todos hashes antes de promover. Destino
existente é bloqueado; sem merge/sobrescrita implícita. Não executar conteúdo recebido.

POST `/api/v1/files/list` {deviceId,rootId,relativePath,cursor?,pageSize:int,consent:bool} -> Job automaticamente QUEUED (leitura explicitamente solicitada pelo operador); pageSize<=200.
GET `/api/v1/jobs/{id}` -> Job, polling sem bloquear UI.
Resultado FILE_LIST em JobResult.details = JSON de
`FilePage` {rootId,relativePath,entries:[{name,relativePath,isDirectory,sizeBytes,lastWriteTimeUtc,isLink}],nextCursor:string?,directoryStamp:string}.
Cursor vinculado à raiz, caminho e stamp; alteração exige nova listagem.
Backup/restore são criados via `/api/v1/jobs/plan` com kind FILE_BACKUP|FILE_RESTORE,
diskFingerprint vazio, file definido, consent e aprovação contextual normal.
Servidor escolhe novo bundleId no planejamento de BACKUP. Bundle AVAILABLE e root
com canWrite obrigatórios para RESTORE. Contratos de progresso/result anteriores mantidos.

## Bundles e transporte

`FileBundle`: {schemaVersion:1,id,name,state:"CREATING"|"AVAILABLE"|"VERIFYING"|"FAILED",sourceDeviceId,rootId,createdAtUtc,totalBytes:long,entries:BundleEntry[]}.
`BundleEntry`: {id:string UUID,relativePath,isDirectory,sizeBytes:long,lastWriteTimeUtc,chunks:ImageChunk[]}.
Chunks zero-based até4MiB, offset por arquivo, SHA256. Diretórios/arquivos vazios sem chunks.
Servidor controla caminhos físicos exclusivamente por IDs UUID; nunca usa relativos do
cliente como caminho no repositório. Limites:100.000 entradas, profundidade64, paths<=2048.
Rejeitar duplicatas/case-collisions e qualquer caminho impróprio ANTES de criar staging.

POST `/agent/v1/jobs/{id}/bundle` {entries:BundleEntry[],totalBytes:long} -> FileBundle CREATING; entrada sem chunks no primeiro envio.
PUT `/agent/v1/jobs/{id}/bundle/{fileId}/chunks/{seq}` binário + X-Content-SHA256 -> recibo {sequence,sha256,sizeBytes} após flush e releitura.
POST `/agent/v1/jobs/{id}/bundle-complete` {} -> FileBundle AVAILABLE somente após validação.
GET `/agent/v1/jobs/{id}/bundle` -> FileBundle (somente job FILE_RESTORE atribuído).
GET `/agent/v1/jobs/{id}/bundle/{fileId}/chunks/{seq}` -> bytes verificados.
GET `/api/v1/file-bundles` -> FileBundle[] com entries:[] e entryCount (catálogo leve).
GET `/api/v1/file-bundles/{id}` -> manifesto completo sob demanda.
POST `/api/v1/file-bundles/import` {path,name,consent} -> CREATING; importação local assíncrona com leases no-follow.
POST `/api/v1/file-bundles/{id}/verify` {} -> bundle VERIFYING, validação background.

O transporte Go distingue JSON (máximo256MiB) dos chunks binários (4MiB+64KiB).
Manifestos de discos>=128GiB ultrapassam4MiB e não podem usar o limite binário.
capture-complete retorna metadados sem repetir chunks; GETmanifest de restore
continua completo e limitado. Preflights verificam estrutura/tamanhos/hashes dos
recibos já persistidos e relidos, sem abrir todos os chunks sob o lock global.
Verificação profunda manual roda fora do lock; cada download também confere hash.

## Responsáveis

- Coordenador: Models/Controller/Files/Cloning, API, testes integração, CI, instaladores/root docs.
- Windows agent: apenas src/windows-agent e testes Windows.
- Rescue: apenas src/rescue-agent, rescue e testes Linux específicos; adaptações/relatórios não simulam boot validado.
- Desktop: apenas src/desktop; UX de clone, escopos/listagem/bundles/jobs assíncronos.
- Mudanças no contrato devem ser comunicadas ao coordenador antes de implementar.
