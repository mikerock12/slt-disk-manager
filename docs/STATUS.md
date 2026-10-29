# Matriz de entrega e homologação

Esta é a beta 0.1.0-beta.5.1, não o aceite integral da seção 25 da especificação.
Downloads e evidências atuais em ENTREGA-2026-10-29-beta5.md; registros anteriores são históricos.

| Requisito | Estado nesta entrega | Evidência/limite |
|---|---|---|
| Controller Windows + SQLite | Implementado | Build e suite sintética; serviço via script |
| Desktop WPF | Implementado | Build; API local e confirmação digitada; 27 checks de UI e renderizações XAML sintéticas; hardware pendente |
| Windows client | Implementado | 42 verificações; inventário/SMART e arquivos com escopos |
| IP manual/pareamento/revogação | Implementado | HTTPS pinado, CSR e mTLS |
| Descoberta mDNS/broadcast | Pendente | Inclusão manual por URL |
| Inventário discos/partições | Implementado | Campos indisponíveis são relatados; drivers/permissões variam |
| SMART/NVMe | Implementado via smartctl | smartctl 7.5 embutido no cliente desde a beta.5; hardware ainda sem matriz homologada |
| Rescue Go | Implementado | Testes fixtures, build Linux amd64 e ISO real9/9 rede/pin/CSR/mTLS/INVENTORY/revogação emVM |
| ISO/USB BIOS/UEFI | Construída e iniciada em QEMU | CI40613155072; firmware Ethernet/Wi-Fi, NetworkManager, CLI e motores presentes; SecureBoot/placas físicas ainda não homologados |
| Reboot Windows→Rescue | Pendente | Boot USB assistido, sem alterações automáticas no firmware |
| CAPTURE/RESTORE raw | Implementado | Testes de chunks, corrupção e revalidação; físico não homologado |
| Clone A→servidor→B | Assistente integrado | Captura validada + plano destino; GPT/MBR, NTFS/ext4 menor/maior em CI Linux |
| Clone em lotes A→B | Implementado (beta.4) e redução (beta.5) | Janela limitada no servidor e descarte após confirmação; EXATO/PRESERVAR/AJUSTAR maior e menor (redução com até 1 GiB de blocos não nulos em RAM); lotes de zeros não trafegam; testes Controller, Go, E2E dois agentes e Linux real em CI; sem retomada após falha; físico não homologado |
| Clone em lote para vários destinos | Pendente | Um destino por clone |
| Partclone inteligente | Script local candidato | Não integra catálogo/transporte do Controller |
| ddrescue com mapfile | Script local candidato | Retomada local; não integra fila do Controller |
| Explorar/copiar arquivos remotamente | Implementado | Escopos locais; listagem paginada, backup/envio e importação local; E2E5/5 |
| Particionamento/formatação remoto | Limitado aos perfis de instalação | Perfis tipados Windows (WinPE) e Linux (Rescue); sem particionador arbitrário |
| Conversão MBR→GPT/UEFI | Implementado na beta.5 | MBR2GPT da mídia importada, hash no plano assinado, /validate, confirmação local e conferência do layout; laboratório VM: Windows clonado iniciou em UEFI até a área de trabalho; hardware não homologado |
| Instalação Windows/Linux | Windows WinPE com unattend e Ubuntu/Xubuntu pelo Rescue (UEFI) implementados na beta.5 | Laboratório VM: instalados ao mesmo tempo, Windows até a área de trabalho sem OOBE e Xubuntu com troca de senha obrigatória (ENTREGA-2026-10-29-beta5.md); hardware não homologado |
| PXE/Rescue local | Pendente | USB/boot manual primeiro |
| Contas/RBAC | Pendente | Uma credencial administrativa local, sem multiusuário |
| Auditoria/plano/locks/anti-replay | Implementado | Registro encadeado; não é WORM nem auditoria externa |
| Retomada de imagem pela rede | Pendente | Recibo idempotente; queda encerra sem retomar escrita |
| Criptografia em repouso/retention | Externa/manual | ACLs para chaves; proteger volume e definir política |
| Instalador EXE Windows | Implementado e testado | Instaladores separados de servidor e cliente (beta.5.1); runtime .NET/C++, 7-Zip (servidor) e smartctl (cliente) embutidos; serviço opcional de cada um com UAC; desinstalação preserva estado gerado |
| MSI/assinatura de binários | Pendente | ZIP e instalador Inno disponíveis; sem certificado de assinatura do proprietário |
| GitHub/CI remoto/PR/merge/destaque | Publicado e fixado | PR1–PR9 mesclados; beta.5 publicada pelo CI a partir do merge 3ebd13c (Windows/Linux, ISO BIOS/UEFI e release) |
| Backup Google Drive | Concluído e conferido (beta.5) | Drive privado: fonte+Git bundle, os oito assets da release com manifesto SHA256 e snapshot do portal; releitura de tamanho/SHA256, tamanhos remotos e permissão só do proprietário conferidos; sem dados de clientes nem mídias |

Homologação restante: hardware físico (placas de rede/Wi-Fi, Secure Boot, SATA/NVMe) com
discos descartáveis, Windows 11, perda de rede/energia durante gravação e primeiro boot em
equipamentos reais. O ciclo instalar → clonar com redução → converter → iniciar já passou
em máquinas virtuais.
Os scripts locais de ferramentas maduras não devem ser contabilizados como
funcionalidades centralizadas concluídas.
