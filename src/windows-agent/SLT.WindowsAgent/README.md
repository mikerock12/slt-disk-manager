# Agente Windows

Windows 10/11 x64; .NET 10, ou pacote publicado self-contained. Nenhuma operação deste agente grava setores ou altera boot/partições. O inventário usa Get-Disk/Get-Partition; o diagnóstico SMART opcional usa smartctl instalado separadamente, sem redistribuir terceiros.

```powershell
.\SLT.WindowsAgent.exe --server https://192.168.1.20:7443 --server-pin HASH_SHA256_EXIBIDO_NO_SERVIDOR
```

Copie o pin diretamente do computador servidor. Confira o código de pareamento de oito dígitos no cliente antes de aprovar no painel. O agente inicia todas as conexões; nenhuma porta de entrada é aberta no cliente. Cada instalação cria chave RSA individual e certificado com ACL restrita a usuário atual/SYSTEM/Administradores. Não copie o diretório de estado entre computadores.

Para inventário completo, inicie como administrador. `--state-dir` define estado privado (padrão `%ProgramData%\SLT\WindowsAgent`). `--smartctl` exige caminho absoluto para smartctl.exe legítimo, por exemplo `C:\Program Files\smartmontools\bin\smartctl.exe`. Sem essa opção a capacidade SMART não é anunciada. `--collect-only` imprime inventário local JSON sem conexão; `--once` realiza pareamento se necessário e um ciclo de inventário/job antes de sair.

Sem escopos de arquivos, os jobs aceitos são INVENTORY e SMART. Com escopos explícitos, também há FILE_LIST, FILE_BACKUP e FILE_RESTORE. Planos exigem assinatura RSA do servidor fixado, autorização, UUID, deviceId correto e validade. Diário persistente impede replay antes da execução. Falhas de rede não repetem jobs. Um resultado local fica em `job-<id>.result.json`; se sua confirmação ao servidor se perder, o operador deve reconciliar o estado e planejar uma nova tarefa.

## Pastas autorizadas e transferência de arquivos

Adicione `--file-roots C:\ProgramData\SLT\scopes.json` com um arquivo JSON local preparado pelo responsável. Não há acesso a arquivos habilitado por padrão. As pastas precisam existir. Exemplo de configuração (ajuste os caminhos antes de usar):

```json
[
  {"id":"documentos","label":"Documentos autorizados","path":"C:\\Users\\Cliente\\Documents","allowRead":true,"allowWrite":false},
  {"id":"recebidos","label":"Recebidos","path":"C:\\SLTRecebidos","allowRead":true,"allowWrite":true}
]
```

O servidor recebe apenas IDs, rótulos e permissões dos escopos. Não recebe seus caminhos absolutos. O arquivo de configuração deve ficar protegido contra alteração por usuários sem autorização, especialmente quando o agente roda como SYSTEM. Reinicie o agente após modificar a configuração. Escopos que incluem estado, credenciais, binários ou o arquivo de configuração do agente são rejeitados.

O painel pode listar páginas de até 200 itens e selecionar arquivos/pastas para backup. Caminhos permanecem relativos ao escopo, incluindo diretórios vazios e arquivos vazios. O agente usa chunks de até 4 MiB com SHA256, bloqueia escritores concorrentes durante a leitura e aborta se os metadados mudarem. Arquivos bloqueados, em uso ou instáveis podem exigir fechamento do programa antes do backup; não há snapshot VSS nem garantia de consistência de bancos de dados ativos.

Para enviar do servidor ao cliente, escolha um bundle e informe **uma pasta nova** no escopo que permite escrita. O agente prepara uma árvore privada no mesmo volume, valida todos os hashes por releitura e promove a pasta atomicamente sem substituir conteúdo existente. Não há merge nem overwrite implícito. Conteúdo recebido nunca é executado. Permissões do destino são herdadas do escopo após a promoção; ACLs, alternate data streams e ownership da origem não são reproduzidos.

Junctions, symlinks, reparse points e arquivos com hard links não são seguidos. Traversal, ADS, nomes Windows reservados, colisões de maiúsculas/minúsculas e manifestos inconsistentes são bloqueados antes de criar o staging. Os caminhos são abertos por handles com ancestrais fixados contra renomeação/substituição; o staging nasce com ACL privada. Limites locais: 32 escopos, 100.000 entradas por bundle, profundidade 64, caminho relativo de até 2.048 caracteres e até 1 TiB lógico por seleção. A restauração requer espaço livre adicional no volume.

Teste de arquivos com fixtures e mocks: `dotnet run --project tests/SLT.WindowsAgent.Tests`. E2E real de transporte, usando **somente arquivos sintéticos** sob `runtime/`: compile Controller/agente em Debug e execute `dotnet run --project tests/SLT.WindowsAgent.Tests -- --e2e-files`. Usa HTTPS loopback 17445, certificados individuais e logs privados ignorados pelo Git.

O adaptador SMART converte somente caminhos físicos locais `PhysicalDrive0..255` para `/dev/pd0..255`, conforme o [manual oficial do smartctl para Windows](https://github.com/smartmontools/smartmontools/blob/main/src/smartctl.8.in). Usa exclusivamente `--json --all`; testes que alteram estado do dispositivo não são executados.

Teste de integração reproduzível: após compilar Controller e agente em Debug, execute `pwsh -NoProfile -File tests/integration/windows-agent-e2e.ps1` na raiz. Ele usa porta loopback 17444 e estado único sob `runtime/` ignorado pelo Git, verifica TLS fixado, pareamento, mTLS, job INVENTORY assinado e revogação. Faz apenas inventário local em leitura; não valida restauração, boot ou compatibilidade de hardware. A importação TLS do certificado no Windows usa o provedor protegido da conta atual para compatibilidade com SChannel; nenhum PFX temporário é gravado pelo agente.

Certificado expirado/revogado exige ação local e novo pareamento; não apague credenciais automaticamente. Revogue primeiro a identidade antiga no painel e use um novo diretório de estado. Credenciais são sensíveis e nunca devem entrar em commit ou backup público. Instalação como serviço exige os scripts de implantação do projeto. SMART ausente, serial não informado por adaptador e permissões insuficientes são apresentados como indisponíveis; não como saúde garantida.
