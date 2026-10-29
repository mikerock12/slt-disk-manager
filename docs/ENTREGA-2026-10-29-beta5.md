# Entrega 0.1.0-beta.5 — 29/10/2026

Versão com instalação de Windows e Xubuntu/Ubuntu pelo servidor, primeiro boot do Windows
sem perguntas, redução no clone em lotes, conversão MBR→GPT/UEFI e instalador com todas as
dependências. Consulte STATUS.md para distinguir implementação, teste automatizado e
homologação: hardware físico continua sem homologação.

## Downloads publicados

<!-- RELEASE -->

## Teste de laboratório completo

Pedido do proprietário: dois clientes em máquinas virtuais, instalados ao mesmo tempo pelo
servidor; clonar o Windows do cliente 2 para o cliente 1, substituindo todas as partições e
reduzindo a principal; converter o cliente 1 para GPT/UEFI e iniciar o Windows.

| Etapa | Resultado | Evidência |
|---|---|---|
| Cliente 1: 64 GB, UEFI/GPT, Xubuntu 26.04 pelo SLT Rescue | <!-- R1 --> | |
| Cliente 2: 128 GB, BIOS/MBR, Windows 10 Pro pelo WinPE | <!-- R2 --> | |
| Instalações ao mesmo tempo | <!-- R3 --> | |
| Primeiro boot Xubuntu | <!-- R4 --> | |
| Primeiro boot Windows | <!-- R5 --> | |
| Clone em lotes cliente 2 → cliente 1 com redução | <!-- R6 --> | |
| Conversão MBR → GPT/UEFI no cliente 1 | <!-- R7 --> | |
| Boot do Windows clonado em UEFI | <!-- R8 --> | |

O harness está em `tests/integration/lab-vm.py` (fases `install`, `firstboot`, `clone`,
`convert` e `boot`). As mídias Windows/Xubuntu, os discos virtuais, as senhas temporárias e
as evidências brutas ficam só em `runtime/` local, fora do Git.

### Ambiente e limitações do laboratório

- QEMU 11.1 em Windows 10 com WHPX, processador i5-6500 e disco rígido. Neste host o Windows
  convidado não ficou estável sob WHPX (travava no meio do download, dormia sem acordar ou
  dava tela azul); o WinPE e o primeiro boot do Windows rodaram em emulação TCG, muito mais
  lenta (DISM ~30 min, especialização ~50 min). Rescue e Xubuntu rodaram sob WHPX.
- Com internet, a configuração inicial do Windows tenta baixar a atualização ZDP; na VM o
  download falhou e o Windows repetiu a tela "Por que meu computador foi reiniciado?". O
  primeiro boot do laboratório roda sem internet; o guia orienta o técnico a desconectar o
  cabo se essa tela aparecer.
- Tempos do laboratório não representam o uso real: em hardware com SSD e rede gigabit a
  aplicação da imagem leva minutos.

## Problemas reais encontrados pelo teste e corrigidos

- Uma conexão TCP interrompida abortava a instalação aos 300 MB de 4 GB: downloads de lotes
  e relatórios de progresso agora tentam de novo por cerca de 5 minutos, abrem conexão nova a
  cada tentativa e o agente fecha conexões ociosas antes do servidor.
- O `unattend.xml` e os arquivos de boot podiam ficar no cache do WinPE se o computador fosse
  desligado logo após o "Concluído": o agente agora descarrega os volumes de sistema e do
  Windows antes de informar o resultado.
- A senha temporária do Windows aparecia nas listagens de tarefas e ficava no banco: agora é
  mascarada e apagada do plano quando a tarefa termina; continua exibida uma única vez.
- O agente WinPE travava sob hypervisor por preempção assíncrona do Go: o kit define
  `GODEBUG=asyncpreemptoff=1`.
- Relógio do cliente em horário local (dual boot) invalidava certificado/plano: tolerância
  aplicada.

## Verificações automáticas

<!-- CHECKS -->

## Prints

<!-- PRINTS -->

## Backup

<!-- BACKUP -->
