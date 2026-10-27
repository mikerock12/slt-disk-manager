# SLT Rescue Agent

O executável separado `cmd/slt-winpe-agent` implanta Windows exclusivamente em
WinPE AMD64. Usa o mesmo transporte/assinaturas com identidade WINPE própria,
download WIM/ESD verificado, confirmação local e ferramentas oficiais da mídia.
Kit e critérios de homologação: [`rescue/windows-pe`](../../rescue/windows-pe/README.md).
Os testes de instalação usam executores falsos; não comprovam boot/VM/hardware.

Agente Go sem dependências externas. Faz pareamento individual por CSR RSA, fixa o SHA-256 DER do servidor, usa mTLS e executa planos assinados. Inventário, SMART de leitura, captura RAW em chunks de 4 MiB e restauração com hash antes de cada escrita e verificação por releitura.

```sh
go test ./...
CGO_ENABLED=0 GOOS=linux GOARCH=amd64 go build -trimpath -o slt-rescue-agent ./cmd/slt-rescue-agent
sudo ./slt-rescue-agent --server https://192.168.1.10:7443 --server-pin HEX_DO_SERVIDOR
```

Confira o código de pareamento de oito dígitos com o operador e aprove no painel. A identidade fica em `/var/lib/slt-rescue-agent` com diretório 0700 e arquivos 0600. Não copie credenciais entre computadores ou inclua-as em uma ISO. Validade do certificado exige novo pareamento quando expirar.

A restauração física fica desativada até iniciar com `--allow-physical-write`. Mesmo assim, o agente exige confirmação digitada no teclado do cliente, disco offline, serial presente, fingerprint e geometria iguais ao plano e abertura exclusiva Linux. Nenhuma restauração é repetida automaticamente depois de falha: o ledger local bloqueia replay e é preciso reconciliar/criar novo plano. Falha no meio de uma restauração deixa o destino incompleto.

Inventário, captura e restauração físicos precisam de Linux e ferramentas `lsblk`/`smartctl` instaladas. Não há shell remoto. Discos montados, swap ou sistema são bloqueados para imagem. SMART não inicia testes de estresse. Em mídia com erros, não repetir captura raw: usar aquisição especializada ddrescue ainda não integrada neste agente.

## Laboratório com arquivos

```sh
mkdir fixtures
truncate -s 16M fixtures/source.raw
truncate -s 16M fixtures/target.raw
./slt-rescue-agent --server https://127.0.0.1:7443 --server-pin HEX --state-dir ./lab-state --fixture-dir ./fixtures --allow-file-write
```

Somente arquivos regulares `.raw`, sem symlinks, dentro da pasta autorizada são elegíveis. A origem e o destino aparecem claramente como `SLT SYNTHETIC FILE`. `--once` envia inventário e consulta uma única tarefa. Os testes automatizados usam apenas diretórios temporários.

## Clone integral e adaptação de capacidade

A captura continua RAW integral e imutável. Quando `sfdisk`/`blkid` estão disponíveis, o inventário informa GPT/MBR, partições, IDs, tipos, atributos e filesystem; a captura revalida esse layout sob lock e envia o layout realmente capturado ao controlador.

`EXACT_RAW` exige capacidade idêntica. `PRESERVE_LAYOUT` aceita destino maior, preserva todas as partições em seus offsets e regenera a GPT secundária no final correto. `FIT` redimensiona somente a partição NTFS/ext4 selecionada no plano assinado, mantendo seu início e deslocando integralmente o sufixo — inclusive WinRE/OEM/partições desconhecidas — com tamanhos, IDs, tipos e atributos preservados. Diferenças são alinhadas a 1 MiB, podendo restar espaço livre pequeno no final.

Para anunciar `RESTORE_LAYOUT`/`RESTORE_FIT`, iniciar no Linux com scratch persistente e ferramentas:

```sh
sudo apt-get install fdisk util-linux ntfs-3g e2fsprogs
sudo slt-rescue-agent --server https://IP:7443 --server-pin HASH \
  --scratch-dir /mnt/disco-saudavel/slt-scratch --allow-physical-write
```

O scratch deve existir, não conter symlinks, estar fora do disco de destino e ter espaço livre para **imagem original + imagem destino inteira + máximo(maior partição original, capacidade do destino) + 64 MiB**. Tmpfs, ramfs e overlay são recusados. A reserva é conservadora mesmo em arquivos esparsos; imagens de centenas de GB precisam de armazenamento de bancada adequado.

Antes de abrir o destino, o agente baixa/verifica todos os chunks, inspeciona o layout real da imagem, prepara uma cópia privada, executa `ntfsresize`/`resize2fs` e `sfdisk`, verifica filesystems, compara hashes das partições não alteradas e confere CRCs GPT. Nenhum resizer escreve na origem ou no arquivo do servidor. Somente depois abre o destino, pede confirmação local e grava/verifica a cópia preparada.

NTFS hibernado/dirty/inconsistente, BitLocker/LUKS, RAID/LVM, MBR estendido/EBR, discos dinâmicos, GPT híbrida, setor diferente, destino insuficiente e movimentação de partição de boot posterior são bloqueados. O mínimo estimado no preview não autoriza reduzir: o motor precisa concluir a operação real na cópia. Não há opção `force` para ignorar problemas NTFS; a marca CHKDSK que o próprio ntfsresize deixa após resize é preservada.

O relatório separa `dataVerified` (bytes do destino iguais à imagem preparada), `tableVerified`, `bootPayloadPreserved` e `bootVerified`. Este último permanece **false**: copiar ESP/boot/Windows/arquivos não valida NVRAM, drivers, WinRE registrado, ativação, Secure Boot nem inicialização no computador destino. NTFS de recuperação movido é preservado byte a byte, mas sua integração ao Windows precisa de validação real. GUIDs/UUIDs são mantidos para preservar referências; não conecte simultaneamente original e clone ao mesmo SO sem um procedimento específico.

Referências dos motores: [sfdisk](https://man7.org/linux/man-pages/man8/sfdisk.8.html), [ntfsresize](https://manpages.debian.org/trixie/ntfs-3g/ntfsresize.8.en.html), [ntfs-3g.probe](https://manpages.debian.org/trixie/ntfs-3g/ntfs-3g.probe.8.en.html), [resize2fs](https://manpages.debian.org/trixie/e2fsprogs/resize2fs.8.en.html).

## Testes e limitações verificáveis

- O executável Linux Rescue não oferece retomada automática, compressão, partclone, ddrescue integrado, gerenciamento de partições ou deployment de SO. A implantação Windows pertence ao executável WinPE separado.
- RAW exato preserva setores; adaptação preserva conteúdo ativo e layout aprovado, mas o disco inteiro deixa de ter hash idêntico ao original.
- Os testes básicos usam arquivos sintéticos. A suíte Linux com motores reais está em `rescue/scripts/test-adaptive-linux.sh`: GPT/MBR × NTFS/ext4 × destino menor/maior, sentinelas de dados/boot/recuperação, origem imutável, bloqueios e loop com backing_file comprovado. Compilar esses testes em Windows não comprova sua execução no Linux.
- Os fixtures não contêm Windows licenciado nem uma instalação Linux inicializável; sucesso de hash/filesystem não deve ser apresentado como teste de boot.
- Certificado próprio por boot/instalação; a identidade Windows não é reutilizada automaticamente no Rescue.
- Build cruzado não comprova compatibilidade do firmware, Secure Boot ou controlador físico. O script de mídia exige build/teste Linux separado.
