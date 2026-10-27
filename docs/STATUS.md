# Matriz de entrega e homologação

Esta é uma beta 0.1.0, não o aceite integral da seção 25 da especificação.

| Requisito | Estado nesta entrega | Evidência/limite |
|---|---|---|
| Controller Windows + SQLite | Implementado | Build e suite sintética; serviço via script |
| Desktop WPF | Implementado | Build; API local e confirmação digitada; 26 checks de UI e renderizações XAML sintéticas; hardware pendente |
| Windows client | Implementado | 42 verificações; inventário/SMART e arquivos com escopos |
| IP manual/pareamento/revogação | Implementado | HTTPS pinado, CSR e mTLS |
| Descoberta mDNS/broadcast | Pendente | Inclusão manual por URL |
| Inventário discos/partições | Implementado | Campos indisponíveis são relatados; drivers/permissões variam |
| SMART/NVMe | Implementado via smartctl | Ferramenta externa opcional; hardware ainda sem matriz homologada |
| Rescue Go | Implementado | Testes fixtures e build Linux amd64 |
| ISO/USB BIOS/UEFI | Construída e iniciada em QEMU | CI40456449172; CLI e motores presentes; SecureBoot/hardware ainda não homologados |
| Reboot Windows→Rescue | Pendente | Boot USB assistido, sem alterações automáticas no firmware |
| CAPTURE/RESTORE raw | Implementado | Testes de chunks, corrupção e revalidação; físico não homologado |
| Clone A→servidor→B | Assistente integrado | Captura validada + plano destino; GPT/MBR, NTFS/ext4 menor/maior em CI Linux |
| Clone direto/batch | Pendente | Fora do transporte atual |
| Partclone inteligente | Script local candidato | Não integra catálogo/transporte do Controller |
| ddrescue com mapfile | Script local candidato | Retomada local; não integra fila do Controller |
| Explorar/copiar arquivos remotamente | Implementado | Escopos locais; listagem paginada, backup/envio e importação local; E2E5/5 |
| Particionamento/formatação remoto | Limitado ao deployment WinPE | Somente perfil Windows WinPE tipado; sem particionador arbitrário |
| Instalação Windows/Linux | Windows WinPE implementado; Linux pendente | ISO/ESD local importado e quatro edições lidas; aplicação/boot real ainda não homologados |
| PXE/Rescue local | Pendente | USB/boot manual primeiro |
| Contas/RBAC | Pendente | Uma credencial administrativa local, sem multiusuário |
| Auditoria/plano/locks/anti-replay | Implementado | Registro encadeado; não é WORM nem auditoria externa |
| Retomada de imagem pela rede | Pendente | Recibo idempotente; queda encerra sem retomar escrita |
| Criptografia em repouso/retention | Externa/manual | ACLs para chaves; proteger volume e definir política |
| MSI/assinatura de binários | Pendente | ZIP self-contained e scripts de serviço |
| GitHub/CI remoto/PR/merge/destaque | Publicado e fixado | PR1/CI Windows+Linux mesclado; PR2 integra novos fluxos |
| Backup Google Drive | Concluído e conferido | Fonte+Git bundle; pacote em 3 partes e reunidor com SHA256; sem dados de clientes |

Homologação restante: Windows 10/11 em VMs UEFI, Linux ext4, SATA/NVMe em discos
descartáveis, perda de rede/energia, filesystem após restore e primeiro boot.
Os scripts locais de ferramentas maduras não devem ser contabilizados como
funcionalidades centralizadas concluídas.
