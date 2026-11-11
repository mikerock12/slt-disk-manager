# ADR 004 — Modo SLT, formatação e ProxyDHCP

Data: 2026-11-11. Base: beta.6 (`c83855c`).

## Modo SLT: reiniciar um cliente Windows no SLT Rescue sem pendrive

### Problema

Clonar, formatar ou instalar sistema exige o disco fora de uso. Até a beta.6 isso
significava preparar um pendrive (ou configurar PXE no roteador) e, no Rescue, digitar
endereço, pin e confirmações no teclado do cliente.

### Decisão

Um cliente Windows já pareado pode ser reiniciado **uma vez** no SLT Rescue, carregado
do servidor pela rede. O pedido nasce no painel (tarefa `REBOOT_RESCUE`, com confirmação
digitada `REINICIAR <computador> MODO SLT`) ou no próprio cliente (atalho “Reiniciar em
modo SLT Disk Manager”, tecla R no cliente aberto).

1. O servidor emite um plano assinado com: hash e tamanho do carregador `ipxe-local.efi`,
   o roteiro de boot e um **ticket de uso único** (32 bytes aleatórios, 30 minutos).
2. O cliente confere a assinatura, baixa o carregador por mTLS, confere o hash e valida
   o roteiro linha a linha: só o vocabulário fixo necessário (`dhcp`, `kernel`, `initrd`,
   `boot`…), endereços privados, caminho `/boot/v1/assets/` e argumentos de kernel
   conhecidos. Um servidor comprometido não consegue fazer o carregador executar outro
   comando.
3. O cliente grava `\EFI\SLT\ipxe.efi` e `\EFI\SLT\autoexec.ipxe` na partição EFI,
   cria uma entrada de firmware e a seleciona **somente para o próximo boot**
   (`bootsequence`, isto é, `BootNext`). A ordem permanente continua com o Windows em
   primeiro. Qualquer falha desfaz a entrada e apaga os arquivos.
4. O computador reinicia, o carregador busca kernel, initrd e sistema no serviço de
   boot pela rede e o Rescue inicia em RAM. O agente lê servidor, pin e ticket da linha
   de comando do kernel e pareia sozinho. O ticket só é aceito uma vez e só para uma
   sessão Rescue ligada ao cliente que o pediu.
5. Ao voltar ao Windows, o cliente remove a entrada temporária e os arquivos.

O ticket nunca aparece em listagens administrativas nem na auditoria, e é apagado da
tarefa ao terminar. Ele trafega do servidor ao cliente por mTLS e do cliente ao Rescue
pelo disco e pela memória da própria máquina, não pela rede.

### Confirmação de gravações em modo SLT

Fora do modo SLT o Rescue continua pedindo a confirmação no teclado do cliente. Em modo
SLT com gravação habilitada, a confirmação digitada no painel
(`APAGAR…`, `FORMATAR…`) é a confirmação humana; o cliente mostra o que está sendo
gravado e por qual autorização, sem pedir de novo. É uma escolha explícita na hora de
reiniciar (caixa marcada por padrão) e fica registrada no dispositivo e na auditoria.
Plano assinado, validade, disco offline e revalidação local do fingerprint não mudam.

### Limites

- **UEFI com Secure Boot desativado.** O carregador iPXE não é assinado. Com Secure
  Boot ativo o firmware recusa o carregador e volta ao Windows; o cliente informa isso
  antes, no inventário, e o painel não oferece o botão.
- **BIOS/Legacy não é atendido** por este caminho: use o boot pela rede (F12) ou o
  pendrive.
- Exige cabo de rede e o Boot pela rede em execução no servidor com o pacote netboot
  da mesma versão.
- O passo do Windows (`bcdedit`, partição EFI) é coberto por testes com executor
  falso e pasta temporária. O restante da cadeia (carregador local, boot, ticket,
  inventário e formatação por ordem do servidor) roda em VM UEFI no laboratório do CI.
  Não há homologação em hardware físico.

## FORMAT_DISK

Tarefa tipada do Rescue: apaga o disco e cria uma partição única (GPT ou MBR) em NTFS,
exFAT, FAT32 ou ext4, com rótulo opcional. Usa os mesmos portões de uma restauração:
plano assinado com confirmação `FORMATAR <computador> <disco>`, disco não montado, não
protegido e com identidade conferida sob bloqueio exclusivo. Não é um particionador
livre: layouts arbitrários continuam fora do escopo.

## ProxyDHCP

Modo opcional do boot pela rede, desligado por padrão, para redes cujo roteador não
permite configurar `next-server` e arquivo de boot. O serviço escuta UDP 67 e 4011 e:

- responde **somente** a clientes PXE (`PXEClient`) BIOS ou UEFI x64;
- **nunca** oferece endereço (`yiaddr` 0.0.0.0), máscara, gateway, DNS ou prazo;
- ignora pedidos encaminhados por relay e limita respostas por segundo.

O DHCP da rede continua entregando os endereços. O início falha se outra aplicação já
usa a porta 67 no servidor. Não use em rede que já tenha outro servidor PXE.
