# Matriz de entrega e homologação

Esta é a beta 0.1.0-beta.8 em validação, não o aceite integral da seção 25 da especificação.
Evidências desta entrega em ENTREGA-2026-11-13-beta8.md; registros anteriores são históricos.

| Requisito | Estado nesta entrega | Evidência/limite |
|---|---|---|
| Controller Windows + SQLite | Implementado | Build e suite sintética; serviço via script |
| Desktop WPF | Implementado | Build; API local e confirmação digitada; 27 checks de UI e renderizações XAML sintéticas; hardware pendente |
| Windows client | Implementado | 42 verificações; inventário/SMART e arquivos com escopos |
| IP manual/pareamento/revogação | Implementado | HTTPS pinado, CSR e mTLS; certificado do cliente Windows renovado automaticamente |
| Descoberta na LAN e pareamento por código curto | Implementado (beta.7) | Broadcast UDP 7444 na sub-rede e código de 8 dígitos ligado ao certificado (ADR 003); E2E real cliente↔servidor em loopback; não é mDNS e não atravessa roteadores |
| Inventário discos/partições | Implementado | Campos indisponíveis são relatados; drivers/permissões variam |
| SMART/NVMe | Implementado via smartctl | smartctl 7.5 embutido no cliente desde a beta.5; hardware ainda sem matriz homologada |
| Rescue Go | Implementado | Testes fixtures, build Linux amd64 e ISO real9/9 rede/pin/CSR/mTLS/INVENTORY/revogação emVM |
| ISO/USB BIOS/UEFI | Construída e iniciada em QEMU | CI40613155072; firmware Ethernet/Wi-Fi, NetworkManager, CLI e motores presentes; SecureBoot/placas físicas ainda não homologados |
| Reboot Windows→Rescue (modo SLT) | Implementado para UEFI sem Secure Boot (beta.7) | Entrada de firmware só para o próximo boot, carregador conferido por hash e ticket de uso único (ADR 004). Laboratório VM UEFI: boot pela partição EFI, pareamento por ticket e formatação por ordem do servidor. Passo `bcdedit` do Windows só com executor falso; BIOS/Legacy e Secure Boot não atendidos; hardware não homologado |
| Discos do próprio servidor | Implementado (beta.8) | Formatar, converter, MBR2GPT, partição, redimensionar, verificar, chkdsk, BCDBoot, SFC/DISM, clone bruto conferido e online/offline, com plano assinado e confirmação digitada (ADR 005). Testes com executor falso e arquivos; teste real com VHDX sintéticos; discos do Windows/paginação/SLT protegidos |
| Máquinas virtuais (Hyper-V) | Implementado (beta.8) | Geração 1 (BIOS) e 2 (UEFI), memória, vCPU, limite de CPU, VHDX ou HD do servidor em pass-through, ISO, rede, Secure Boot e TPM; validação com executor falso; pass-through de HD físico não exercitado em hardware |
| Modo kamikaze (servidor em modo SLT) | Implementado (beta.8) | WinRE em ramdisk com entrada de boot única; pacote assinado de uso único; clone, formatação e instalação do Windows nos discos do próprio servidor (ADR 005). Ver ENTREGA-2026-11-13-beta8.md para o laboratório em VM |
| CAPTURE/RESTORE raw | Implementado | Testes de chunks, corrupção e revalidação; físico não homologado |
| Clone A→servidor→B | Assistente integrado | Captura validada + plano destino; GPT/MBR, NTFS/ext4 menor/maior em CI Linux |
| Clone em lotes A→B | Implementado (beta.4) e redução (beta.5) | Janela limitada no servidor e descarte após confirmação; EXATO/PRESERVAR/AJUSTAR maior e menor (redução com até 1 GiB de blocos não nulos em RAM); lotes de zeros não trafegam; testes Controller, Go, E2E dois agentes e Linux real em CI; sem retomada após falha; físico não homologado |
| Clone em lote para vários destinos | Pendente | Um destino por clone |
| Partclone inteligente | Script local candidato | Não integra catálogo/transporte do Controller |
| ddrescue com mapfile | Script local candidato | Retomada local; não integra fila do Controller |
| Explorar/copiar arquivos remotamente | Implementado | Escopos locais; listagem paginada, backup/envio e importação local; E2E5/5 |
| Particionamento/formatação remoto | Formatação em partição única (beta.7) e perfis de instalação | `FORMAT_DISK`: NTFS, exFAT, FAT32 ou ext4 em GPT/MBR, testado com sfdisk/mkfs reais em arquivos sintéticos e em VM; sem particionador arbitrário |
| Conversão MBR→GPT/UEFI | Implementado na beta.5 | MBR2GPT da mídia importada, hash no plano assinado, /validate, confirmação local e conferência do layout; laboratório VM: Windows clonado iniciou em UEFI até a área de trabalho; hardware não homologado |
| Instalação Windows/Linux | Windows WinPE com unattend e Ubuntu/Xubuntu pelo Rescue (UEFI) implementados na beta.5 | Laboratório VM: instalados ao mesmo tempo, Windows até a área de trabalho sem OOBE e Xubuntu com troca de senha obrigatória (ENTREGA-2026-10-29-beta5.md); hardware não homologado |
| Network Boot / Rescue PXE | P0–P2 publicados na beta.6, validados em VMs BIOS/UEFI | Processo isolado opt-in, DHCP externo, TFTP/HTTP e perfis Rescue/iPXE. Consulte [a matriz de homologação](NETWORK-BOOT-HOMOLOGACAO.md). ProxyDHCP opcional validado em VMs BIOS/UEFI na beta.7. WinPE PXE, Windows sem scratch, Secure Boot e hardware físico pendentes |
| Contas/RBAC | Pendente | Uma credencial administrativa local, sem multiusuário |
| Auditoria/plano/locks/anti-replay | Implementado | Registro encadeado; não é WORM nem auditoria externa |
| Retomada de imagem pela rede | Pendente | Recibo idempotente; queda encerra sem retomar escrita |
| Criptografia em repouso/retention | Externa/manual | ACLs para chaves; proteger volume e definir política |
| Instalador EXE Windows | Implementado e testado | Instaladores separados de servidor e cliente; atalhos na área de trabalho, pasta do repositório, atualização de serviço instalado e migração do repositório com SHA256 (beta.7); desinstalação preserva estado e repositório |
| MSI/assinatura de binários | Pendente | ZIP e instalador Inno disponíveis; sem certificado de assinatura do proprietário |
| GitHub/CI remoto/PR/merge/destaque | Publicado e fixado | PR1–PR11 mesclados; beta.5.1 (instaladores separados) publicada pelo CI a partir do merge 1200e08 |
| Backup Google Drive | Concluído e conferido (beta.5.1) | Drive privado: fonte+Git bundle, os nove assets da release com manifesto SHA256 e snapshot do portal; releitura de tamanho/SHA256, tamanhos remotos e permissão só do proprietário conferidos; sem dados de clientes nem mídias |

Homologação restante: hardware físico (placas de rede/Wi-Fi, Secure Boot, SATA/NVMe) com
discos descartáveis, Windows 11, perda de rede/energia durante gravação e primeiro boot em
equipamentos reais. O ciclo instalar → clonar com redução → converter → iniciar já passou
em máquinas virtuais.
Os scripts locais de ferramentas maduras não devem ser contabilizados como
funcionalidades centralizadas concluídas.
