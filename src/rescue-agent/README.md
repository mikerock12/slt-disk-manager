# SLT Rescue Agent

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

## Limitações verificáveis

- Sem retomada automática, compressão, partclone, ddrescue integrado, gerenciamento de partições ou deployment de SO.
- O modo raw preserva setores e layout, mas não valida boot, drivers, ativação ou integridade do filesystem.
- Restauração em disco maior mantém o layout original; não expande partições nem corrige a GPT secundária automaticamente.
- Certificado próprio por boot/instalação; a identidade Windows não é reutilizada automaticamente no Rescue.
- Build cruzado não comprova compatibilidade do firmware, Secure Boot ou controlador físico. O script de mídia exige build/teste Linux separado.
