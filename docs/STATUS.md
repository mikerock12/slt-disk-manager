# Matriz de entrega e homologação

Esta é uma beta 0.1.0, não o aceite integral da seção 25 da especificação.

| Requisito | Estado nesta entrega | Evidência/limite |
|---|---|---|
| Controller Windows + SQLite | Implementado | Build e suite sintética; serviço via script |
| Desktop WPF | Implementado | Build; API local e confirmação digitada; QA visual ainda pendente |
| Windows client | Implementado | Testes de segurança; inventário somente leitura |
| IP manual/pareamento/revogação | Implementado | HTTPS pinado, CSR e mTLS |
| Descoberta mDNS/broadcast | Pendente | Inclusão manual por URL |
| Inventário discos/partições | Implementado | Campos indisponíveis são relatados; drivers/permissões variam |
| SMART/NVMe | Implementado via smartctl | Ferramenta externa opcional; hardware ainda sem matriz homologada |
| Rescue Go | Implementado | Testes fixtures e build Linux amd64 |
| ISO/USB UEFI | Script live-build entregue | ISO ainda não construída nem bootada em VM |
| Reboot Windows→Rescue | Pendente | Boot USB assistido, sem alterações automáticas no firmware |
| CAPTURE/RESTORE raw | Implementado | Testes de chunks, corrupção e revalidação; físico não homologado |
| Clone A→servidor→B | Fluxo captura+restore | Dois planos/duas confirmações; sem assistente único |
| Clone direto/batch | Pendente | Fora do transporte atual |
| Partclone inteligente | Script local candidato | Não integra catálogo/transporte do Controller |
| ddrescue com mapfile | Script local candidato | Retomada local; não integra fila do Controller |
| Explorar/copiar arquivos remotamente | Pendente | Não expõe filesystem no protocolo atual |
| Particionamento/formatação remoto | Pendente | Bloqueado por allowlist de tipos de job |
| Instalação Windows/Linux | Orientações de perfis | Sem automatização nem boot homologado; depende de mídia autorizada |
| PXE/Rescue local | Pendente | USB/boot manual primeiro |
| Contas/RBAC | Pendente | Uma credencial administrativa local, sem multiusuário |
| Auditoria/plano/locks/anti-replay | Implementado | Registro encadeado; não é WORM nem auditoria externa |
| Retomada de imagem pela rede | Pendente | Recibo idempotente; queda encerra sem retomar escrita |
| Criptografia em repouso/retention | Externa/manual | ACLs para chaves; proteger volume e definir política |
| MSI/assinatura de binários | Pendente | ZIP self-contained e scripts de serviço |
| GitHub/CI remoto/PR/merge/destaque | Depende de autenticação | CLI inválido e navegador sem sessão na inspeção inicial |
| Backup Google Drive | Concluído e conferido | Fonte+Git bundle; pacote em 3 partes e reunidor com SHA256; sem dados de clientes |

Homologação restante: Windows 10/11 em VMs UEFI, Linux ext4, SATA/NVMe em discos
descartáveis, perda de rede/energia, filesystem após restore e primeiro boot.
Os scripts locais de ferramentas maduras não devem ser contabilizados como
funcionalidades centralizadas concluídas.
