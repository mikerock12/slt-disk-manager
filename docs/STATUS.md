# Matriz de entrega e homologação

Esta é a beta 0.1.0-beta.4, não o aceite integral da seção 25 da especificação.
Downloads e evidências atuais em ENTREGA-2026-10-28-beta4.md; registros anteriores são históricos.

| Requisito | Estado nesta entrega | Evidência/limite |
|---|---|---|
| Controller Windows + SQLite | Implementado | Build e suite sintética; serviço via script |
| Desktop WPF | Implementado | Build; API local e confirmação digitada; 27 checks de UI e renderizações XAML sintéticas; hardware pendente |
| Windows client | Implementado | 42 verificações; inventário/SMART e arquivos com escopos |
| IP manual/pareamento/revogação | Implementado | HTTPS pinado, CSR e mTLS |
| Descoberta mDNS/broadcast | Pendente | Inclusão manual por URL |
| Inventário discos/partições | Implementado | Campos indisponíveis são relatados; drivers/permissões variam |
| SMART/NVMe | Implementado via smartctl | Ferramenta externa opcional; hardware ainda sem matriz homologada |
| Rescue Go | Implementado | Testes fixtures, build Linux amd64 e ISO real9/9 rede/pin/CSR/mTLS/INVENTORY/revogação emVM |
| ISO/USB BIOS/UEFI | Construída e iniciada em QEMU | CI40613155072; firmware Ethernet/Wi-Fi, NetworkManager, CLI e motores presentes; SecureBoot/placas físicas ainda não homologados |
| Reboot Windows→Rescue | Pendente | Boot USB assistido, sem alterações automáticas no firmware |
| CAPTURE/RESTORE raw | Implementado | Testes de chunks, corrupção e revalidação; físico não homologado |
| Clone A→servidor→B | Assistente integrado | Captura validada + plano destino; GPT/MBR, NTFS/ext4 menor/maior em CI Linux |
| Clone em lotes A→B | Implementado na beta.4 | Janela limitada no servidor e descarte após confirmação; EXATO/PRESERVAR/AJUSTAR maior; testes Controller, Go, E2E dois agentes e Linux real em CI; redução ainda só com imagem; sem retomada após falha; físico não homologado |
| Clone em lote para vários destinos | Pendente | Um destino por clone |
| Partclone inteligente | Script local candidato | Não integra catálogo/transporte do Controller |
| ddrescue com mapfile | Script local candidato | Retomada local; não integra fila do Controller |
| Explorar/copiar arquivos remotamente | Implementado | Escopos locais; listagem paginada, backup/envio e importação local; E2E5/5 |
| Particionamento/formatação remoto | Limitado ao deployment WinPE | Somente perfil Windows WinPE tipado; sem particionador arbitrário |
| Instalação Windows/Linux | Windows WinPE implementado; Linux pendente | Correção DiskPart na beta.3.3; VM BIOS/MBR completou DiskPart/DISM/BCDBoot e iniciou o Windows; OOBE, UEFI e hardware não homologados |
| PXE/Rescue local | Pendente | USB/boot manual primeiro |
| Contas/RBAC | Pendente | Uma credencial administrativa local, sem multiusuário |
| Auditoria/plano/locks/anti-replay | Implementado | Registro encadeado; não é WORM nem auditoria externa |
| Retomada de imagem pela rede | Pendente | Recibo idempotente; queda encerra sem retomar escrita |
| Criptografia em repouso/retention | Externa/manual | ACLs para chaves; proteger volume e definir política |
| Instalador EXE Windows | Implementado e testado | Componentes servidor/cliente/offline; per-user; desinstalação preserva estado gerado |
| MSI/assinatura de binários | Pendente | ZIP e instalador Inno disponíveis; sem certificado de assinatura do proprietário |
| GitHub/CI remoto/PR/merge/destaque | Publicado e fixado | PR1–PR7 mesclados; beta.4 publicada com CI40729165261 Windows/Linux e ISO40729165548 SUCCESS |
| Backup Google Drive | Concluído e conferido (beta.3.3) | Drive privado: fonte+Git bundle e os oito assets da release com manifesto SHA256; tamanhos/hashes e permissão só do proprietário conferidos; sem dados de clientes nem mídia Microsoft |

Homologação restante: Windows 10/11 em VMs UEFI, Linux ext4, SATA/NVMe em discos
descartáveis, perda de rede/energia, filesystem após restore e primeiro boot.
Os scripts locais de ferramentas maduras não devem ser contabilizados como
funcionalidades centralizadas concluídas.
