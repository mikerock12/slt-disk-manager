# Pendências do SLT Disk Manager após a beta.8

Levantamento de 13/11/2026. Nada aqui, na beta.7 ou na beta.8 foi homologado em hardware
físico.

## Entregue na beta.8 (com evidência e limite)

| Item | Evidência | Limite |
|---|---|---|
| Discos do servidor | 277 verificações do Controller (executor falso e discos em arquivo); 17/17 operações reais com VHDX sintéticos dentro de uma VM; verificar disco e BCDBoot reais no Windows da VM, que voltou a iniciar | MBR2GPT e SFC/DISM offline só com executor falso; pendrives e HDs físicos não testados |
| Máquinas virtuais (Hyper-V) | 11/11 reais: VMs geração 1 e 2 criadas, ligadas, desligadas e excluídas com o VHDX | Entrega de HD físico à VM (pass-through) não exercitada; Hyper-V aninhado não testado |
| Modo kamikaze | VM Hyper-V UEFI com Secure Boot: plano pela API real, imagem do WinRE via DISM, boot único em ramdisk, frases digitadas no console, clone 1 GB → 2 GB conferido (arquivo de 64 MB idêntico, GPT realocada), formatação MBR/exFAT, volta ao Windows e resultado coletado sem entradas de boot restantes | Instalar Windows pelo kamikaze só com testes de validação/fake; BIOS/Legacy não exercitado; hardware físico pendente |
| Ícones e correção do Set-ServerRepository | Ícones nos executáveis, atalhos e instaladores; regressão do repositório vazio em PowerShell 5.1 e 7 | — |

## Novas pendências da beta.8

- **Kamikaze: instalar Linux no próprio servidor.** O WinPE não traz os instaladores Linux.
  Hoje o caminho é uma VM com o HD ou o Rescue por pendrive. Critério: boot local do Rescue
  a partir do disco (GRUB + live-boot) validado em VM.
- **Kamikaze: instalação do Windows ponta a ponta** em VM (UEFI e BIOS), com o primeiro boot
  do sistema instalado.
- **Kamikaze sem WinRE:** servidores com WinRE desativado precisam de `reagentc /enable` ou
  de um `kamikaze\winpe.wim`. Avaliar extrair o WinRE da mídia importada automaticamente.
- **Pass-through de HD físico na VM** e matriz com pendrives/HDs reais.
- **Falso positivo de antivírus:** o Malwarebytes classificou `SLT.Desktop.dll` como
  `MachineLearning/Anomalous`. Reforça a necessidade de assinar os binários.

## Concluído na beta.7 (com a evidência e o limite de cada item)

| Item da lista anterior | O que foi entregue | Evidência | Limite |
|---|---|---|---|
| ProxyDHCP | Modo opcional, desligado por padrão | Laboratório do CI: DHCP sem opções de boot, menu em BIOS e UEFI, endereço vindo do DHCP da rede antes, durante e depois; SLT sem oferta de IP | VMs. No laboratório o DHCP e o SLT ficam no mesmo host; em rede real estão em máquinas diferentes |
| Descoberta do servidor | Broadcast UDP e código de 8 dígitos | E2E real cliente Windows ↔ servidor; testes de servidor, cliente e Rescue com o mesmo vetor | Só na sub-rede do servidor; não é mDNS |
| Reboot Windows → Rescue | Modo SLT | Laboratório do CI em VM UEFI, da partição EFI até a formatação por ordem do servidor | UEFI sem Secure Boot. O passo `bcdedit` do Windows não foi exercitado em Windows real ou VM Windows |
| Formatação fora dos perfis de instalação | `FORMAT_DISK` com partição única | sfdisk/mkfs reais em arquivos sintéticos e em VM | Sem particionador livre (várias partições, redimensionar) |

## Network Boot

| Pendência | O que falta | Critério de conclusão |
|---|---|---|
| P3 — WinPE por PXE | Preparar o perfil WinPE com mídia de boot separada da imagem de instalação. Validar a cadeia iPXE/wimboot, os arquivos de boot e os drivers. | Inicializar em VM sem USB, parear por CSR/mTLS e executar inventário com disco sintético somente leitura. Conferir a ausência de credenciais administrativas nos arquivos servidos. |
| P4 — Windows sem disco auxiliar | Após P3, validar a aplicação de WIM remoto com credenciais restritas e espaço temporário em RAM. Testar falta de RAM, compartilhamento indisponível e implantações simultâneas. | Instalar Windows 10/11 em VMs descartáveis com apenas o disco de destino e comprovar boot local, configuração inicial, login e drivers. Se o modo falhar, manter o requisito de armazenamento auxiliar. |
| P4 — Perfis Linux adicionais | Versionar e testar novos perfis de implantação. | Comprovar boot, instalação e primeiro boot de cada perfil em VM descartável, com as confirmações de gravação previstas. |
| P5 — Secure Boot | Validar separadamente as cadeias assinadas do Rescue e do WinPE, incluindo revogações de firmware. O modo SLT precisa de uma cadeia assinada (shim/GRUB) para funcionar com Secure Boot ativo. | Demonstrar boot com Secure Boot ativo e registrar as versões e combinações aceitas. |
| P5 — Hardware físico | Montar uma matriz BIOS/UEFI, Ethernet, Wi-Fi após o boot do Rescue e discos SATA/NVMe. Testar primeiro boot e recuperação após perda de rede ou energia. Incluir o modo SLT (entrada de firmware criada pelo Windows) e o ProxyDHCP com roteadores reais. | Registrar equipamento, firmware e resultado de cada cenário. Usar discos descartáveis com autorização presencial. |
| Modo SLT em BIOS/Legacy | Encadear um carregador a partir do gerenciador de boot do Windows em MBR. | Reinício em modo SLT comprovado em VM BIOS, com retorno ao Windows. |
| Modo SLT: passo do Windows | Exercitar `bcdedit`/partição EFI em Windows real. | VM Windows UEFI reinicia em modo SLT, volta ao Windows e a entrada temporária é removida. |

VLAN/relay DHCP, Wake-on-LAN e integridade verificada dos arquivos de boot servidos por
HTTP continuam como evoluções a avaliar.

Nos testes automatizados, usar arquivos e discos sintéticos. Gravações exigem plano
assinado e válido, confirmação humana contextual, disco offline e revalidação local do
fingerprint.

## Demais pendências do produto

- **Operação.** Clonagem para vários destinos no mesmo lote. Instalar Windows a partir
  do modo SLT (hoje exige WinPE em pendrive).
- **Imagens e recuperação.** Retomada de transferência de imagens, integração
  centralizada de Partclone e ddrescue com mapfile, particionador com várias partições.
- **Administração e distribuição.** Contas e permissões por função, política de
  criptografia em repouso e retenção, instalador MSI e assinatura dos binários (depende
  de certificado de assinatura do proprietário).
- **Validação visual do portal** em produção, quando o acesso por navegador estiver
  disponível; a conferência por HTTP é feita a cada publicação.

## Referências

- [Mapa de implementação do Network Boot](../SLT_Disk_Manager_Mapa_Implementacao_Network_Boot_PXE_iPXE.md)
- [Evidências do laboratório e limites por fase](NETWORK-BOOT-HOMOLOGACAO.md)
- [ADR 003 — descoberta e pareamento](ADR/003-descoberta-e-pareamento-curto.md)
- [ADR 004 — modo SLT, formatação e ProxyDHCP](ADR/004-modo-slt-e-proxydhcp.md)
- [Matriz de requisitos do produto](STATUS.md)
