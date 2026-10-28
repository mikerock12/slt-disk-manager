# Entrega 0.1.0-beta.4 — 28/10/2026

Clone em lotes entre clientes Rescue, sem guardar a imagem inteira no servidor.
Consulte STATUS.md para distinguir implementação, teste automatizado e homologação.

## Downloads publicados

[Release e todos os arquivos](https://github.com/mikerock12/slt-disk-manager/releases/tag/v0.1.0-beta.4).

| Arquivo | Bytes | SHA256 |
|---|---:|---|
| SLT-Disk-Manager-Setup-0.1.0-beta.4.exe | 125500522 | a24d42747b4c7773b3769200b6912ce0ea53900f79a10712f20eefcfc8ebaa92 |
| SLT-Disk-Manager-0.1.0-beta.4-win-x64.zip | 178655371 | 8ef0f83fe14980ceed8d4dd77812730d75ab4493044d57ea2cef0edd474b5ea1 |
| SLT-Rescue-0.1.0-beta.4-amd64.iso | 503316480 | b89bbb21fc10f907a484e475be4ab1038531adbb474dbb1b68c5d7a3b62e1495 |

Os oito assets públicos foram baixados e conferidos por tamanho e SHA256 contra os
digests da release. O ZIP passou no teste CRC (1131 arquivos) e o agente empacotado
informa `vcs.revision` igual à fonte da release e `vcs.modified=false`.

## O que muda

Na aba Clonagem, a transferência **Em lotes** é o padrão: origem e destino ficam
ligados ao mesmo tempo, o servidor guarda no máximo 256 MiB (`--stream-window-chunks`)
e apaga cada lote depois que o destino confirma a gravação. Clonar discos de 1 TB não
exige 1 TB livre no servidor nem pasta temporária no Rescue. Vale para EXATO,
PRESERVAR e AJUSTAR para disco igual ou maior; o ajuste acontece no próprio destino
depois de reler e comparar todos os dados. **Imagem completa no servidor** continua
disponível para reduzir para disco menor ou restaurar a mesma origem várias vezes.

Os dois clientes precisam do Rescue 0.1.0-beta.4. Uma queda durante o clone em lotes
deixa o destino parcial: planeje de novo, a origem não é alterada.

## Evidência

Fonte da release: `9c71b9495346ee84dac599bd609c59c88c57bbc6` (PR7 mesclado).
[CI Windows/Linux e publicação](https://github.com/mikerock12/slt-disk-manager/actions/runs/40729165261)
SUCCESS; [construção e boot da ISO](https://github.com/mikerock12/slt-disk-manager/actions/runs/40729165548)
SUCCESS. Controller 121/121, Desktop 31/31, Go vet/testes, E2E real 7/7 com dois agentes
Rescue e janela de um lote (servidor respondeu 429 e a origem aguardou; destino idêntico,
nenhuma imagem guardada) e teste Linux real de EXATO/PRESERVAR/AJUSTAR maior em GPT/MBR
× NTFS/ext4 com destino previamente sujo. Clone físico, boot do sistema clonado e
hardware continuam sem homologação.
