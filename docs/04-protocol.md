# Protocolo de bancada v1

JSON camelCase, datas UTC ISO8601, enums como strings. Base HTTPS `https://IP:7443`.
Certificado servidor RSA fixado por SHA256 DER (`--server-pin`, hex). TLS nunca ignora pin.
API administrativa somente loopback e Bearer token local. Agentes mTLS após pareamento.

## DTOs (contrato de integração)
`DiskInfo`: {id, path, model, serial, sizeBytes:long, logicalSectorBytes:int, physicalSectorBytes:int, fingerprint, isSystem:bool, isReadOnly:bool, mounted:bool, health, partitions:PartitionInfo[]}.
`PartitionInfo`: {name, path, sizeBytes:long, fileSystem, mountPoint}.
Fingerprint SHA256 UTF8 de `model|serial|sizeBytes|logicalSectorBytes|path` (valores exatos, lowercase hex).
`Inventory`: {deviceId, hostname, os, mode:"WINDOWS"|"RESCUE", agentVersion:"0.1.0", capabilities:string[], disks:DiskInfo[], capturedAtUtc, notes:string[]}.

## Pareamento
POST `/agent/v1/enroll` {hostname, mode, csrPem, secret} -> {id, code, expiresAtUtc}.
secret: 32 bytes aleatórios hex, nunca mostrado na UI. CSR RSA assinado com chave individual; CN ignorado pelo servidor.
GET `/agent/v1/enroll/{id}` header `X-Enrollment-Secret` -> {status:"PENDING"|"APPROVED"|"EXPIRED", deviceId?, certificatePem?, caPem?}.
Operador confere código de 8 dígitos localmente; POST `/api/v1/enrollments/{id}/approve` {code}. Certificado cliente CN=deviceId, validade curta. Agente persiste chave/cert em diretório privado.

## Sessão (mTLS)
POST `/agent/v1/inventory` Inventory -> {ok:true}; deviceId deve corresponder ao certificado.
GET `/agent/v1/jobs/next` -> 204 ou `SignedAssignment` {payload:base64(UTF8 JSON JobPlan), signature:base64(RSA PKCS1 SHA256)}. Verificar assinatura usando pubkey do certificado servidor fixado.
`JobPlan`: {id, kind:"INVENTORY"|"SMART"|"CAPTURE"|"RESTORE", deviceId, diskFingerprint, diskPath, diskSizeBytes:long, logicalSectorBytes:int, imageId:string, challenge:string, expiresAtUtc, consent:bool}.
Payload preservado byte a byte na verificação. Jobs CAPTURE/RESTORE somente RESCUE, não montado, identidade inequívoca. RESTORE exige confirmação local adicional antes de escrita em bloco físico. Evitar replay: registrar job local ANTES de executar, nunca repetir automaticamente.
POST `/agent/v1/jobs/{id}/progress` {phase, bytesProcessed:long, bytesTotal:long} -> {cancelRequested:bool}.
POST `/agent/v1/jobs/{id}/result` {success:bool, message, details:string} -> {ok:true}. details pode conter JSON do smartctl como string.

## Imagens RAW v1
Chunks até 4 MiB sem compressão, sequência zero-based, contíguos. SHA256 lowercase.
PUT `/agent/v1/jobs/{id}/chunks/{seq}` bytes, header `X-Content-SHA256` -> {sequence, sha256, sizeBytes} (persistido e hash verificado).
POST `/agent/v1/jobs/{id}/capture-complete` {} -> ImageManifest após validação completa.
GET `/agent/v1/jobs/{id}/manifest` -> ImageManifest (RESTORE autorizado somente).
GET `/agent/v1/jobs/{id}/chunks/{seq}` -> bytes, hash via manifest, checar ANTES da escrita.
`ImageManifest`: {schemaVersion:1, id, name, state:"CREATING"|"VERIFYING"|"AVAILABLE"|"FAILED", sourceDeviceId, sourceFingerprint, logicalSizeBytes:long, logicalSectorBytes:int, createdAtUtc, chunks:ImageChunk[]}.
`ImageChunk`: {sequence:int, offsetBytes:long, sizeBytes:int, sha256}.

## Administrativo
GET `/api/v1/dashboard` -> {devices:int, online:int, activeJobs:int, images:int, repositoryFreeBytes:long}.
GET `/api/v1/devices` -> Device[] {id, hostname, mode, trustStatus, lastSeenAtUtc, inventory:Inventory?}.
GET `/api/v1/enrollments` -> [{id, hostname, mode, code, expiresAtUtc, status}].
POST `/api/v1/devices/{id}/revoke` {}.
GET `/api/v1/jobs` -> Job[] {id, plan:JobPlan, state, createdAtUtc, approvedAtUtc?, progress?, result?}.
POST `/api/v1/jobs/plan` {kind, deviceId, diskFingerprint, imageId?, consent:bool} -> Job.
POST `/api/v1/jobs/{id}/approve` {challenge} -> Job (queued). Challenge deve ser digitado, não automaticamente preenchido.
POST `/api/v1/jobs/{id}/cancel` {} -> Job.
GET `/api/v1/images` -> ImageManifest[]. POST `/api/v1/images/{id}/verify` {} -> ImageManifest.
Verificação profunda inicia em background e retorna estado VERIFYING; consultar catálogo até AVAILABLE ou FAILED. Evita bloquear heartbeat durante releitura de imagens grandes. Cada chunk de captura é relido e validado antes do recibo, e cada download é hash-validado antes da transmissão.
GET `/api/v1/audit` -> [{id, timestampUtc, actor, action, details, previousHash, eventHash}].
Erros JSON {error} e status HTTP apropriado. Reinício marca trabalhos ativos INTERRUPTED sem reenviar escrita.
