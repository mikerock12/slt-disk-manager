# Entrega 0.1.0-beta.5 — 29/10/2026

Versão com instalação de Windows e Xubuntu/Ubuntu pelo servidor, primeiro boot do Windows
sem perguntas, redução no clone em lotes, conversão MBR→GPT/UEFI e instalador com todas as
dependências. Consulte STATUS.md para distinguir implementação, teste automatizado e
homologação: hardware físico continua sem homologação.

## Downloads publicados

[Release e todos os arquivos](https://github.com/mikerock12/slt-disk-manager/releases/tag/v0.1.0-beta.5),
publicada pelo CI a partir do merge `3ebd13c` (PR9).

| Arquivo | Bytes | SHA256 |
|---|---:|---|
| SLT-Disk-Manager-Setup-0.1.0-beta.5.exe | 128037227 | 58de98657ae28bf62251813cb696368c3dab49f9015f10c20ed03a01a702b173 |
| SLT-Disk-Manager-0.1.0-beta.5-win-x64.zip | 181743351 | cfcf0f496afb09bf88309120a6e45839253da5125bbd7cc923317f278d1f8a6a |
| SLT-Rescue-0.1.0-beta.5-amd64.iso | 504365056 | 624a86a04b93aaca45495faa63fd9e7f9ad5a6e2a9f8ce2c4ddee448944471d9 |

Os oito assets públicos foram baixados e conferidos por tamanho e SHA256 contra os digests
da release e o `SHA256SUMS.txt`. O ZIP passou no teste CRC (1149 arquivos), contém 7-Zip
(servidor) e smartctl (cliente) com licenças, e o agente WinPE empacotado informa
`vcs.revision=3ebd13c…` e `vcs.modified=false`. Portal atualizado para esta tag e conferido em
produção (API com tamanho/SHA256 exatos, download 302 para o asset).

## Teste de laboratório completo

Pedido do proprietário: dois clientes em máquinas virtuais, instalados ao mesmo tempo pelo
servidor; clonar o Windows do cliente 2 para o cliente 1, substituindo todas as partições e
reduzindo a principal; converter o cliente 1 para GPT/UEFI e iniciar o Windows.

| Etapa | Resultado | Evidência |
|---|---|---|
| Cliente 1: 64 GB, UEFI/GPT, Xubuntu 26.04 pelo SLT Rescue | COMPLETED: partição EFI + raiz ext4, GRUB/shim da própria ISO, usuário com senha temporária | auditoria do servidor e `docs/screens/lab-xubuntu-instalado-cliente1.png` |
| Cliente 2: 128 GB, BIOS/MBR, Windows 10 Pro pelo WinPE | COMPLETED: download 3,7 GiB em lotes verificados, DiskPart, DISM, BCDBoot/bootsect e `unattend.xml` gravado | resultado do job e cópia do disco pós-instalação |
| Instalações ao mesmo tempo | Aprovadas juntas; despachadas no mesmo segundo; ~12 min com as duas rodando | eventos `job.dispatched`/`job.completed` da auditoria encadeada |
| Primeiro boot Xubuntu | Tela de login `cliente1`/`tecnico` em pt_BR; senha temporária aceita e troca exigida | `docs/screens/lab-xubuntu-*.png` |
| Primeiro boot Windows | Área de trabalho do Windows 10 Pro em pt-BR com a conta `tecnico` e a senha temporária, sem telas de OOBE | `docs/screens/lab-windows-instalado-cliente2.png` |
| Clone em lotes cliente 2 → cliente 1 com redução | COMPLETED em lotes (AJUSTAR): partições do Xubuntu substituídas; Windows 127,46 GB → 63,46 GB; dados e tabela verificados; nenhum dado deixado no servidor | resultado do job e sessão de clone |
| Conversão MBR → GPT/UEFI no cliente 1 | COMPLETED em ~5 min: MBR2GPT da mídia importada (hash no plano assinado), `/validate`, confirmação local e conferência da GPT com partição EFI | resultado do job |
| Boot do Windows clonado em UEFI | Área de trabalho do Windows clonado em UEFI/GPT no cliente 1 com a conta `tecnico`; desligamento limpo pelo botão de energia | `docs/screens/lab-windows-clonado-uefi-cliente1.png` |

O harness está em `tests/integration/lab-vm.py` (fases `install`, `firstboot`, `clone`,
`convert` e `boot`). As mídias Windows/Xubuntu, os discos virtuais, as senhas temporárias e
as evidências brutas ficam só em `runtime/` local, fora do Git.

### Ambiente e limitações do laboratório

- QEMU 11.1 em Windows 10 com WHPX, processador i5-6500 e disco rígido. Neste host o Windows
  convidado não ficou estável sob WHPX (travava no meio do download, dormia sem acordar ou
  dava tela azul); o WinPE e o primeiro boot do Windows rodaram em emulação TCG, muito mais
  lenta (DISM ~30 min, especialização ~50 min). Rescue, Xubuntu e o boot final do Windows
  clonado (já configurado) rodaram sob WHPX. O clone em lotes de 128 GB levou ~3h30 neste
  host com disco rígido (transferência ~2h15 e releitura ~1h20).
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
- O primeiro boot do Windows repetia a tela de recuperação "Por que meu computador foi
  reiniciado?" mesmo sem internet e com o unattend correto: o idioma agora é aplicado na
  etapa `specialize` e as telas de OOBE de máquina e de usuário são puladas; o mesmo disco
  instalado chegou ao logon e à área de trabalho.
- A senha temporária do Windows aparecia nas listagens de tarefas e ficava no banco: agora é
  mascarada e apagada do plano quando a tarefa termina; continua exibida uma única vez.
- O agente WinPE travava sob hypervisor por preempção assíncrona do Go: o kit define
  `GODEBUG=asyncpreemptoff=1`.
- Relógio do cliente em horário local (dual boot) invalidava certificado/plano: tolerância
  aplicada.

## Verificações automáticas

Locais na fonte publicada: Controller 145/145, cliente Windows 42/42, Desktop 37/37, Go
`vet` Windows/Linux e testes do agente Rescue/WinPE (inclui novas tentativas de rede, descarga
dos volumes e unattend). O CI da release repete as suítes, gera instalador, pacote e ISO e
inicia a ISO em BIOS e UEFI.

## Prints

Em `docs/screens/`: painel com todas as tarefas do laboratório concluídas
(`painel-tarefas-laboratorio.png`), Windows instalado no cliente 2, Windows clonado em UEFI no
cliente 1, login do Xubuntu e troca obrigatória de senha. Nenhum print mostra senha ou token.

## Backup

Backup privado no Google Drive concluído e conferido: fonte com histórico Git completo
(bundle verificado) e manifesto de hashes, os oito assets da release e o snapshot do portal.
Releitura com tamanho/SHA256, tamanhos remotos exatos e acesso somente do proprietário.
Referências privadas ficam apenas em recibos locais ignorados pelo Git.
