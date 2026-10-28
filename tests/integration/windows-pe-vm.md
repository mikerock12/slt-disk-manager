# Teste Windows PE em VM local

Este teste opcional usa QEMU TCG sem instalação, driver, ADK ou alteração de boot
do host. Os únicos discos graváveis são dois arquivos qcow2 novos, com nomes fixos,
num diretório `runtime/winpe-vm-<nonce>` criado pelo teste. Não recebe dispositivos
físicos como parâmetros. A ISO do operador e o USB virtual de bootstrap são leitura.

O teste cria controlador privado loopback com certificados próprios. A rede usa
NAT privado QEMU, sem bridge/TAP ou encaminhamento de portas de entrada. No fixture
PE, `WaitForNetwork` aguarda a configuração e `route delete 0.0.0.0` remove a rota
padrão antes de iniciar o agente. O endereço virtual `10.0.2.2` alcança o controlador
loopback; a tabela IPv4 é impressa no console para revisão. O backend IPv6 do QEMU
fica desabilitado. Após implantação concluída, o harness desconecta a NIC virtual
antes de acompanhar o primeiro boot. O transporte mantém TLS fixado e mTLS.
Nenhum componente Microsoft é publicado, enviado a CI ou incluído no backup.

## Recursos e execução

No host de referência: 15,9 GiB RAM, aproximadamente 7,5 GiB disponíveis e quatro
processadores lógicos. A VM reserva 3 GiB e duas CPUs por software. Target virtual
64 GiB e scratch 12 GiB são esparsos; instalação usa espaço real conforme cresce.
Reserve ao menos 30 GiB livres para o teste e a importação da mídia.

QEMU para Windows está referenciado pelo [site oficial](https://www.qemu.org/download/).
O teste de referência usa o pacote Weil de 11/08/2026, QEMU 11.1.0, extraído por
7-Zip em `.tools/qemu-20260811/bin` após comparar SHA-512 com o checksum do fornecedor.
Não execute o instalador. Os binários extraídos permanecem privados e ignorados.

```powershell
python ./tests/integration/windows-pe-vm.py `
  --qemu-dir ./.tools/qemu-20260811/bin `
  --iso './ISOs/Windows 10.iso' --timeout-minutes 25
```

Sem `--deploy`, para após boot PE, pareamento CSR/mTLS e inventário real. Com
`--deploy`, importa a mídia, escolhe sua primeira edição AMD64, planeja instalação
**BIOS/MBR** no disco virtual cujo serial é `SLT-VM-TARGET`, aprova e digita a
confirmação pelo teclado virtual. O scratch separado tem serial `SLT-VM-SCRATCH`.

```powershell
python ./tests/integration/windows-pe-vm.py `
  --qemu-dir ./.tools/qemu-20260811/bin `
  --iso './ISOs/Windows 10.iso' --timeout-minutes 120 `
  --deploy --first-boot-minutes 15
```

O preparo de scratch usa DiskPart **dentro da VM** sobre o arquivo virtual recém
criado; não é um script de instalação para máquinas físicas. O seed de teste e
esse script não devem ser copiados para um pendrive de produção. O agente é o
mesmo binário de release, sem bypass de TLS, plano, confirmação ou fingerprint.

O console é mantido visível nos screenshots. Quando a mídia PE não disponibiliza
COM1, revise `latest-screen.png`; somente após aparecer o pedido nativo de
confirmação local, crie `confirm-vm-only.txt` no runtime com o texto exato
`SLT-VM-TARGET`. Esse marcador permite ao harness digitar a confirmação pelo teclado
virtual. Não é um bypass do agente e não pode autorizar disco físico.

Antes do pareamento, `control.json` no runtime aceita ações de diagnóstico fixas:
`capture`, `open-console`, `show-network`, `show-clock`, `start-fixture` e `stop`.
Após pareamento, somente `capture` e `stop`. Nenhum comando arbitrário é recebido.
O teclado en-US configurado no answer file do fixture permite digitação QMP
reprodutível. Comandos de diagnóstico devem ser solicitados após conferir a tela.

## Evidência e limites

`evidence.json` separa boot PE, inventário mTLS, implantação concluída e primeiro
boot. Logs e screenshots QMP ficam no mesmo runtime. O teste não marca primeiro
boot automaticamente pelo tempo transcorrido ou pelo sucesso do DISM: uma pessoa
deve conferir a imagem de OOBE/Windows e registrar o resultado. Não substitui
homologação UEFI/Secure Boot, drivers físicos ou dois computadores reais.

TCG pode levar vários minutos para PE e dezenas de minutos ou mais para DISM e
primeiro boot. Essas estimativas não são medições de desempenho. Timeout ou falha
preservam evidências e encerram apenas processos iniciados pelo próprio teste;
jamais são apresentados como instalação aprovada. As imagens privadas permanecem
no runtime para inspeção e não são apagadas automaticamente.

## Experimento registrado em 27/10/2026

Execução BIOS/TCG entre 02:46:19 e 03:11:19 (America/Sao_Paulo), limite 25 minutos,
runtime privado `runtime/winpe-vm-b8eb13913c554e689964743535c55aa0`:

- A mídia abriu a tela real do Setup/WinPE em português e um console.
- O console exibiu três mensagens de arquivo não encontrado; serial ficou vazio.
  A dependência obrigatória de COM1 no bootstrap do fixture é uma causa provável:
  redireção para dispositivo ausente pode impedir o próprio comando de executar.
- Não houve pareamento, inventário mTLS, aplicação DISM, BCDBoot ou primeiro boot
  comprovado. `evidence.json` registra timeout de enrollment e mantém esses campos
  false. O critério `peBoot` do harness exige enrollment, além da tela do Setup.
- `failure-screen.png`, `latest-screen.png`, argumentos QEMU e logs preservados
  localmente. A VM e o controlador privado foram encerrados pelo próprio harness.
- O fixture foi ajustado para usar console sem exigir COM1 e revisão do prompt por
  marcador de teste. Esse ajuste compilou como Python, mas não foi reexecutado em
  nova VM. A instalação Windows continua sem homologação.

Durante a revisão foi corrigido separadamente o inventário nativo WinPE para
reconhecer mapeamentos de CD-ROM somente leitura: esses volumes não oferecem
`VOLUME_DISK_EXTENTS` nem representam `PhysicalDrive` elegível a escrita. O teste
sintético distingue CD/RAM de volumes físicos, preservando a proteção de USB,
scratch e estado. Essa correção nativa não foi homologada em PE neste experimento.

Nova tentativa com console em `runtime/winpe-vm-e67d74becd864ceebbf6fb9f7ed0f921`
executou o preparo NTFS do scratch virtual e iniciou o binário Go de release. O
POST de pareamento foi interrompido porque o QEMU Windows não encontrou seu helper
GLib de spawn para `guestfwd-cmd`; `qemu.log` registra `fork_exec: Failed to execute
helper program`. A tentativa foi encerrada para corrigir o transporte do fixture.
O relay Python isolado passou teste binário bidirecional de 2 MiB, confirmando que
o bloqueio era anterior à execução dele. A versão atual usa NAT e remoção de rota,
sem depender desse helper. Nenhuma alteração produtiva foi necessária.

Em `runtime/winpe-vm-a10ed72ee16c43ef97ba211c256732fb` (27/10, 22:16–23:15) a VM
comprovou boot PE, mTLS, inventário e transferência da mídia, mas expirou aguardando
o marcador de confirmação, que não foi criado. Nenhuma escrita no destino.

Em `runtime/winpe-vm-4ebd721969524c47be265a4875033d85` (27/10, 23:41–23:58) o marcador
foi criado após revisar o pedido nativo para `SLT-VM-TARGET`. A implantação falhou na
fase PARTITIONING: o script sempre emitia `online disk`, e o DiskPart `/s` aborta com
`0x80042595` ("Este disco já está online") quando o disco já está online, o caso
comum também em hardware. O agente parou corretamente, sem repetir. Correção do
produto: `BuildWinPEDiskPart` só inclui `online disk` quando o atributo IOCTL
`DISK_ATTRIBUTE_OFFLINE` da revalidação após a confirmação local indica disco
offline; continua sem `noerr`. Testes Go cobrem os dois casos.

Com o agente corrigido (SHA256 `756940475091501984b4eaef8409ee3c676a20e6a5f30574dfc74e29c45970a7`,
ainda não publicado em release), `runtime/winpe-vm-2b5582e8cd494a52b19b92d704bc4857`
(28/10, 00:02–01:44 UTC−3 aprox.) passou: boot PE, mTLS/inventário, mídia verificada,
confirmação local, DiskPart MBR, DISM `/CheckIntegrity /Verify`, BCDBoot e bootsect;
job COMPLETED e NIC desconectada. O Windows aplicado iniciou pelo disco de destino e
permaneceu em "Preparando-se" durante os 40 minutos observados sob TCG; OOBE não foi
alcançado, portanto `firstBootVerified` continua false. UEFI/GPT e hardware seguem pendentes.

Referências: [QEMU discos virtuais](https://www.qemu.org/docs/master/system/images.html),
[rede restrita e guestfwd](https://www.qemu.org/docs/master/system/invocation.html),
[QMP](https://www.qemu.org/docs/master/interop/qmp-spec.html),
[WinPE e aplicativos](https://learn.microsoft.com/en-us/windows-hardware/manufacture/desktop/winpe-create-apps).
