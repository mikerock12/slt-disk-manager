# Mídia SLT Rescue

Para instalação Windows pela mídia oficial do operador, consulte
[`windows-pe/README.md`](windows-pe/README.md). O agente Go WinPE é separado do
Rescue Linux e exige novo pareamento, plano assinado e confirmação local.
O gerador de kit não inclui Windows/ADK nem modifica USB ou boot do computador.

`scripts/build-debian-live.sh` prepara uma ISO Debian Live amd64 usando `live-build`. Execute em uma máquina/VM Debian descartável como root, com rede para os repositórios Debian. O ambiente Windows do desenvolvimento não construiu nem homologou esta ISO.

```sh
# No Debian/Ubuntu de build:
sudo apt-get update
sudo apt-get install live-build debootstrap xorriso squashfs-tools
cd src/rescue-agent
CGO_ENABLED=0 GOOS=linux GOARCH=amd64 go build -trimpath -o ../../rescue/slt-rescue-agent ./cmd/slt-rescue-agent
cd ../../rescue
sudo ./scripts/build-debian-live.sh
```

O script cria diretório de build novo; não apaga diretórios existentes. O hash da ISO é emitido ao finalizar. Grave a ISO com ferramenta de sua escolha apenas em USB de laboratório e valide hash e boot UEFI em VM antes de usar. Não muda bootloader, firmware, Secure Boot ou partições do Windows.

Após boot, abra terminal local e use:

```sh
sudo slt-rescue-agent --server https://IP_DO_SERVIDOR:7443 --server-pin HASH_CONFERIDO_NO_SERVIDOR
```

Pareamento é separado por agente, conferido fisicamente. A imagem não embute servidor, token, chave privada global ou credenciais de cliente. Em Live sem persistência, a credencial e o ledger desaparecem no desligamento: revogue a identidade anterior no servidor e pareie novamente. Para uso continuado, mantenha o diretório de estado em mídia privada persistente.

Restaurar precisa de `--allow-physical-write` e confirmação no teclado do cliente. Inicialmente use apenas inventário/SMART em hardware real; captura/restauração devem ser homologadas em VM/discos descartáveis. Firmware UEFI, Secure Boot, drivers VMD/RAID e conectividade permanecem dependentes do hardware.

`smartmontools`, `gddrescue` e `partclone` constam na mídia para uso autorizado local, mas apenas SMART de leitura e captura/restauração RAW estão integrados ao protocolo. Revisar licenças do Debian e de terceiros antes da redistribuição comercial.

O agente também implementa `PRESERVE_LAYOUT` e `FIT` quando iniciado com `--scratch-dir` em armazenamento persistente saudável, separado do destino. São necessários `sfdisk` (pacote `fdisk`), `blkid`, `ntfsresize`, `ntfs-3g.probe`, `resize2fs` e `e2fsck`. A adaptação ocorre numa cópia privada antes de abrir o disco destino; o guia detalhado e as restrições estão em `src/rescue-agent/README.md`. A presença do código não comprova boot ou homologação de hardware.

Para executar a suíte de integração de motores num runner Linux dedicado:

```sh
sudo apt-get install fdisk util-linux ntfs-3g e2fsprogs dosfstools
sudo env PATH="$PATH" bash rescue/scripts/test-adaptive-linux.sh
```

Todos os discos da suíte são arquivos temporários; o teste de loop só desconecta o dispositivo que ele criou após comprovar seu backing_file. Não usa `losetup -D` nem enumera discos reais como destinos.

## Manutenção local avançada

`scripts/acquire-ddrescue.sh` adquire disco offline identificado por `/dev/disk/by-id` em **arquivo regular** `.img`, com mapfile persistente e fingerprint para impedir retomada com outra origem. Usa primeira passagem sem scraping; não executa tentativas agressivas ou restauração física. O relatório do mapfile diferencia áreas recuperadas e pendentes. Execute novamente com a mesma origem/imagem para retomar; não apague o mapfile. Não promete recuperar dados irrecuperáveis.

`scripts/capture-partclone.sh` captura partição offline ext4/NTFS em **arquivo novo** `.partclone`, verifica com `partclone.chkimg` e gera SHA256. O formato é específico do Partclone e ainda não integra catálogo RAW v1; não renomeie para `.raw`. Em disco com falhas, priorize aquisição ddrescue em vez de Partclone.

Referências: [manual GNU ddrescue](https://www.gnu.org/software/ddrescue/manual/ddrescue_manual.html), [opções Partclone](https://partclone.org/usage/partclone.restore.php), [Debian Live build](https://live-team.pages.debian.net/live-manual/html/live-manual/the-basics.en.html).

Os scripts são ferramentas locais candidatas, sem testes de execução física neste ambiente. Não estão disponíveis como jobs remotos nem anunciam sucesso antes de os motores concluírem. Os perfis em `profiles/` documentam critérios e procedimentos manuais Windows/Linux UEFI que ainda precisam de instalação/boot em VM; não são instaladores implementados.
