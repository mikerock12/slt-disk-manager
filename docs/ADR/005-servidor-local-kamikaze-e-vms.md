# ADR 005 — Discos do próprio servidor, máquinas virtuais e modo kamikaze

Data: 2026-11-13 • Estado: aceito (beta.8)

## Contexto

O operador queria gerenciar os discos ligados ao próprio servidor (pendrives, HDs, SSDs),
instalar sistemas em um HD pelo Hyper-V do servidor e levá-lo pronto ao cliente, e reiniciar
o próprio servidor em “modo SLT Disk Manager” para clonar, formatar ou reinstalar até o disco
de onde ele roda.

## Decisão

1. **Operações tipadas, sem shell genérico.** O Controller executa scripts fixos
   (`LocalHost/Scripts/*.ps1`) com uma operação nomeada e parâmetros JSON pela entrada padrão.
   Programas externos são chamados por lista de argumentos.
2. **Mesmos portões das gravações remotas:**
   - plano assinado pela chave do servidor, com validade de 15 minutos;
   - confirmação digitada que inclui o disco e a capacidade (`APAGAR DISCO 5 64GB`);
   - inventário relido antes de executar, com identidade que inclui o número, o modelo, o serial,
     o tamanho, o setor e o UniqueId;
   - disco offline durante o clone bruto.
3. **Proteções dos discos locais.** Ficam bloqueados para gravação:
   - os discos com o Windows do servidor, o arquivo de paginação ou as pastas do SLT (estado,
     repositório e programa);
   - as partições EFI, MSR e de recuperação.

   Para esses discos o caminho é o modo kamikaze. O argumento `--local-disks virtual-only` limita
   as gravações a discos VHD/VHDX e serve aos testes.
4. **Máquinas virtuais.** Usamos o Hyper-V do Windows, sem um hipervisor próprio:
   - geração 2 = UEFI/GPT e geração 1 = Legacy/BIOS/MBR;
   - memória fixa, número de vCPUs e limite de CPU (`Set-VMProcessor -Maximum`);
   - VHDX novo em pasta escolhida, VHDX existente ou HD do servidor em pass-through (o HD fica
     offline no host);
   - ISO, rede, Secure Boot (modelos Windows/Linux) e TPM.

   Só máquinas marcadas `SLT-Disk-Manager` em Notes são gerenciadas.
5. **Modo kamikaze.** Funciona assim:
   - **Imagem de boot.** A imagem de boot é o WinRE do servidor (ou um `kamikaze\winpe.wim` do
     operador). Ela recebe, via DISM, o `slt-winpe-agent.exe`, o certificado do servidor e um
     `winpeshl.ini` que inicia `kamikaze --session auto`. O resultado fica em cache, com chave pelos
     hashes da imagem, do agente e do certificado.
   - **Entrada de boot.** Uma entrada `osloader` em ramdisk (`winpe yes`) é criada e ligada com
     `bcdedit /bootsequence`. Ela vale para um único reinício e não altera o boot padrão. O carregador
     é assinado pela Microsoft, então o modo funciona em BIOS e UEFI, inclusive com Secure Boot.
   - **Pacote.** O pacote é assinado (RSA PKCS#1 v1.5, como os planos), vale 24 horas, fica em
     `\SLT-Kamikaze\<sessão>` na unidade do repositório e é de uso único (`started.json` e
     `result.json`). As pastas recebem uma ACL só para SISTEMA e Administradores, porque a raiz do
     C: deixa usuários comuns criarem subpastas.
   - **Identidade dos discos.** No console os discos são identificados por modelo, serial (pode
     estar vazio em discos virtuais), tamanho e setor, sem o número, que muda no WinPE. Identidade
     repetida é recusada.
   - **Confirmação e execução.** Cada operação exige digitar a frase mostrada na tela do servidor.
     `CANCELAR` volta ao Windows sem gravar. Cada destino é revalidado antes da escrita.
   - **Operações:**
     - clone bruto conferido por SHA256 com realocação da GPT, inclusive para um disco menor quando
       as partições cabem;
     - formatação NTFS, exFAT ou FAT32 em GPT ou MBR;
     - instalação do Windows a partir da mídia do repositório. A mídia não pode estar no disco de
       destino nem em um disco apagado antes na lista. Sem conta automática: o Windows abre a
       configuração inicial.
   - **Coleta.** Ao voltar ao Windows, o Controller coleta o resultado e remove as entradas de
     boot. Os casos possíveis são `COMPLETED`, `FAILED`, `CANCELLED`, `INTERRUPTED` e `NOT_RUN` (o
     servidor reiniciou, mas o console não executou).

## Consequências e limites

- Instalar Linux no próprio servidor pelo kamikaze não é oferecido: o WinPE não tem os
  instaladores Linux. Use uma VM com o HD em pass-through ou o Rescue por pendrive.
- Sem WinRE ativo e sem `kamikaze\winpe.wim`, o modo fica indisponível. O painel diz como
  ativar.
- O relógio do WinPE pode diferir em horas, porque o RTC fica em hora local. A validade aceita
  14 horas de tolerância.
- O clone local leva o destino em estado offline: discos idênticos não convivem online no mesmo
  Windows.
