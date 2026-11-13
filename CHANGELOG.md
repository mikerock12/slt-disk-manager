# Changelog

## 0.1.0-beta.8 — 2026-11-13 — discos do servidor, máquinas virtuais e modo kamikaze

- **Discos do servidor** (nova aba): pendrives, HDs e SSDs ligados ao próprio servidor —
  formatar disco, converter GPT ⇄ MBR (vazio), MBR → GPT sem perder dados (MBR2GPT),
  formatar partição (trocar o sistema de arquivos), redimensionar, verificar (saúde,
  SMART, scan somente leitura), corrigir volume (chkdsk), reparar inicialização (BCDBoot),
  reparar Windows (DISM e SFC, deste servidor ou de outro disco), clonar disco a disco
  (cópia bruta conferida por SHA256, GPT de reserva realocada, destino menor quando as
  partições cabem) e colocar online/offline. Plano assinado, validade de 15 minutos,
  confirmação digitada com disco e capacidade e releitura do disco antes de gravar. Discos
  com o Windows do servidor, paginação ou dados do SLT ficam protegidos.
- **Máquinas virtuais** (nova aba, Hyper-V): UEFI (geração 2) ou Legacy/BIOS (geração 1),
  memória, processadores e limite de uso da CPU em %, VHDX novo na pasta/disco escolhido,
  VHDX existente ou um HD do servidor entregue à VM para instalar o sistema e levá-lo
  pronto ao cliente, ISO, rede, Secure Boot (Windows/Linux) e TPM; ligar, abrir a tela,
  desligar, trocar ISO e excluir.
- **Modo kamikaze** (nova aba): o servidor reinicia uma vez num WinRE/WinPE carregado na
  memória e, no próprio console, clona discos, formata qualquer disco — inclusive o dele —
  ou instala o Windows. Pacote assinado e de uso único, frase digitada por operação,
  `CANCELAR` volta sem gravar, entrada de boot única e resultado coletado ao voltar ao
  Windows (ADR 005).
- **Ícones próprios** no painel, servidor, cliente, modo SLT, instaladores, atalhos e
  Adicionar/Remover Programas.
- **Correção:** `Set-ServerRepository.ps1 -MoveExisting` falhava com repositório vazio
  (“The property 'Sum' cannot be found”).

## 0.1.0-beta.7 — 2026-11-11 — conexão por código, modo SLT, formatação e instalador

- **Conexão sem endereço nem pin de 64 caracteres:** o servidor responde a uma busca na
  rede (UDP 7444) e o cliente mostra um código de 8 dígitos, derivado do certificado que
  ele viu, do CSR que o servidor recebeu e de nonces comprometidos. O operador digita o
  código no painel; o painel não o exibe. Cinco erros rejeitam o pedido. Clientes que
  informam endereço e pin seguem no fluxo anterior.
- **Renovação automática** do certificado do cliente Windows, dez dias antes de vencer.
- **Modo SLT:** tarefa `REBOOT_RESCUE` reinicia um cliente Windows UEFI (Secure Boot
  desativado) no SLT Rescue carregado do servidor, com carregador conferido por hash,
  roteiro validado linha a linha e ticket de uso único. A sessão pareia sozinha e, com
  gravação habilitada, executa o que for aprovado no painel sem nova confirmação no
  teclado do cliente. Pedido pelo painel ou pelo próprio cliente.
- **Formatar disco** (`FORMAT_DISK`): partição única NTFS, exFAT, FAT32 ou ext4 em GPT ou
  MBR, com os mesmos portões de uma restauração.
- **ProxyDHCP opcional** no boot pela rede: responde só a clientes PXE BIOS/UEFI x64 com
  o arquivo de boot e nunca oferece endereço.
- **Painel:** tema com mais contraste, fonte de 15 px, menu lateral, pedido de conexão em
  destaque, botões de modo SLT e formatação com o motivo quando indisponíveis, tarefas e
  estados em português. Conexão ilegível explica o motivo e oferece reabrir como
  administrador.
- **Instalador:** atalhos na área de trabalho e no Menu Iniciar; atalho do painel do
  serviço com elevação; pasta do repositório na instalação; `Update-Server.ps1` e
  `Update-Agent.ps1` atualizam serviços instalados e corrigem a pasta
  `Program Files (x86)`; `Set-ServerRepository.ps1` muda o repositório movendo o conteúdo
  com conferência SHA256; scripts se reexecutam no PowerShell de 64 bits; erros ficam
  visíveis na janela. Perfis de boot passam a acompanhar o repositório. O portátil abre
  o painel do serviço quando ele existe.
- Laboratório do CI: PXE BIOS/UEFI, modo SLT a partir da partição EFI com formatação de
  disco sintético por ordem do servidor e ProxyDHCP com DHCP sem opções de boot. Sem
  homologação em hardware físico; o passo `bcdedit` do Windows é coberto por testes com
  executor falso.

## 0.1.0-beta.6 — 2026-11-04 — Network Boot de laboratório

- Novo processo isolado opt-in com TFTP bootstrap read-only, HTTP por ID/CIDR,
  catálogo de boot separado com SHA256 e painel administrativo WPF local.
- DHCP externo sem leases ou alteração do roteador; loaders iPXE BIOS/UEFI
  versionados e export kernel/initrd/SquashFS a partir do build Rescue.
- Módulo desligado após instalar/reiniciar; ativação exige Ethernet/CIDR,
  pré-verificação e ação explícita. Firewall opcional por script com UAC.
- Testes sintéticos de segurança/lifecycle e laboratório QEMU em namespace
  isolada. Evidências/pendências em NETWORK-BOOT-HOMOLOGACAO.md; não atribuir
  suporte a hardware, Secure Boot, ProxyDHCP ou WinPE PXE.
- Fluxos USB/ISO, arquivos, clone, implantação e autorizações preservados.

## 0.1.0-beta.5.1 — 2026-10-29 — instaladores separados e IP fixo

- **Dois instaladores:** `SLT-Disk-Manager-Servidor-Setup` para o computador da bancada
  (servidor, painel, 7-Zip, agentes Rescue/WinPE e guias) e `SLT-Disk-Manager-Cliente-Setup`
  para os PCs atendidos (cliente Windows e smartctl). Cada um oferece só o seu serviço do
  Windows. O instalador do servidor atualiza por cima da instalação combinada anterior; o
  do cliente tem identidade e pasta próprias e pode coexistir com o servidor no mesmo PC.
- **IP fixo do servidor:** guia na instalação, no QUICKSTART e no site — reserva de DHCP no
  roteador (recomendado) ou IP manual no Windows, antes de instalar o serviço.
- Mesmo código e agentes da beta.5; pacote portátil e ISO Rescue inalterados em função.

## 0.1.0-beta.5 — 2026-10-29 — instalação Linux, primeiro boot Windows, redução em lotes e MBR→GPT

- **Instalação Ubuntu/Xubuntu pelo Rescue (UEFI/GPT):** importe a ISO oficial no servidor;
  o cliente em SLT Rescue particiona, extrai o sistema da própria ISO, instala GRUB/shim,
  cria o usuário com senha temporária gerada pelo servidor (exibida uma vez) e exige a
  troca no primeiro login. Instalação Linux e Windows em clientes diferentes seguem ao
  mesmo tempo.
- **Windows sem perguntas no primeiro boot:** a implantação WinPE grava `unattend.xml` com
  nome do computador, conta administradora local com senha temporária, idioma, fuso
  e telas de OOBE puladas; hibernação desligada para permitir clone seguro.
- **Clone em lotes para disco menor:** AJUSTAR para destino menor agora funciona sem
  imagem completa. O destino grava direto no disco; os blocos que passam do novo tamanho
  ficam em RAM (só os não nulos, até 1 GiB) até o NTFS/ext4 ser reduzido no próprio destino.
  Lotes só de zeros não trafegam.
- **Converter MBR para GPT/UEFI:** nova tarefa no WinPE usando o MBR2GPT.EXE da mídia
  Windows importada pelo operador (hash amarrado ao plano assinado), com validação antes,
  confirmação local e conferência do layout depois.
- **Instalador completo:** o pacote traz 7-Zip para ler ISOs no servidor e smartctl 7.5
  para SMART no cliente; runtime .NET e C++ continuam embutidos. Servidor e cliente podem
  ser instalados como serviço do Windows pelo próprio instalador (com aviso e UAC).
- **Robustez de rede:** download de lotes e relatórios de progresso tentam de novo em
  quedas de conexão, em vez de abortar instalações longas.
- **Relógio local:** certificados e planos toleram computadores com o relógio em horário
  local (comum em dual boot com Windows).
- Painel mostra quando o cliente aguarda confirmação no teclado local.

## 0.1.0-beta.4 — 2026-10-28 — clone em lotes

Clone cliente → cliente em lotes, agora padrão no painel: origem e destino ficam ligados
ao mesmo tempo, o servidor guarda no máximo 256 MiB (ajustável) e apaga cada lote
depois que o destino confirma a gravação. Clonar discos de 1 TB não exige 1 TB livre
no servidor nem pasta temporária no Rescue. Vale para EXATO, PRESERVAR e AJUSTAR para
disco igual ou maior; o ajuste acontece no próprio destino depois de reler e comparar
todos os dados. Reduzir para disco menor e repetir restaurações continuam no modo com
imagem completa, mantido como opção. Novo agente Rescue necessário nos dois clientes.

## 0.1.0-beta.3.3 — 2026-10-28

Corrige a implantação Windows pelo WinPE: o script DiskPart executava
`online disk` sempre, e o DiskPart abortava (0x80042595) em disco já online,
o caso comum, antes de particionar. Agora o comando só é usado quando o disco
está offline. Em VM BIOS/MBR sintética, DiskPart, DISM, BCDBoot e bootsect
concluíram e o Windows iniciou pelo destino; OOBE, UEFI/GPT e hardware ainda
não foram comprovados. Não use a implantação Windows da beta.3.2.


## 0.1.0-beta.3.2 — 2026-10-27

Distribuição com instalador EXE por componentes e ISO Rescue BIOS/UEFI com
firmware Ethernet/Wi-Fi, NetworkManager e assistente de conexão/pareamento.
Corrige exemplos do WinPE para os caminhos do pacote instalado e documenta
montagem do armazenamento temporário separado para clone adaptável.
Mantém os motores e testes da beta.3.1; documentos públicos omitem referências
de backups privados. A beta.3.1 já publicada permanece preservada.


## 0.1.0-beta.3 — 2026-10-27

Clone integral cliente→servidor→cliente com GPT/MBR e ajuste NTFS/ext4 em cópia
de trabalho; motores Linux testados em discos sintéticos menores/maiores.
Gerenciador de arquivos com raízes autorizadas, backup e envio bidirecional,
importação local e proteção contra links/substituição de destino existente.
Catálogo ISO/WIM/ESD e agente Go WinPE com plano assinado, confirmação local,
DISM/BCDBoot e execução paralela entre máquinas distintas.

Windows10 ISO local lida com quatro edições AMD64; instalação/primeiro boot
Windows e hardware permanecem sem homologação. Nova proteção na limpeza de
testes evita erro de aplicação por arquivo temporário bloqueado.


## 0.1.0-beta.2 — 2026-10-26

Primeira beta local: Controller, Desktop, Windows client e Rescue Go; TLS/mTLS,
inventário, SMART, planos assinados, imagens RAW e auditoria. Testes em arquivos
sintéticos e inventário Windows real somente leitura.

Correções de integração: containers de chave compatíveis com Windows SChannel,
lock de instância antes de abrir o banco, bloqueio de verificação concorrente a
restore, ACLs do instalador e readiness do launcher. Sem homologação de boot ou
operações destrutivas em hardware. Consulte docs/STATUS.md para pendências.
