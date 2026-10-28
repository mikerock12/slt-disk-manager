# Painel SLT Desktop

Aplicativo WPF para Windows x64, em português, com conexão administrativa local HTTPS e certificado fixado por SHA-256.

```powershell
dotnet run --project src/desktop/SLT.Desktop.Wpf -- --connection "CAMINHO\connection.json"
```

O arquivo de conexão aceita `url` (ou `serverUrl`/`adminUrl`), `token` (ou `adminToken`) e `serverPin` (ou `pin`/`certificatePin`). Também é possível abrir o arquivo pela interface ou informar os campos manualmente. Tokens permanecem na memória do painel e não são persistidos por ele. O endereço administrativo precisa ser HTTPS de loopback (`localhost`, `127.0.0.1`, `::1`); execute no próprio servidor.

## Uso

1. Conecte ao controlador. O painel atualiza dados reais a cada 10 segundos.
2. Em Pareamento, selecione a máquina e digite o código conferido no console do agente.
3. Em Computadores, selecione cliente e disco para consultar partições e identidade.
4. Para diagnóstico, use Atualizar inventário ou Consultar SMART. Para captura/restauração, use Operações.
5. Para cada tarefa, examine o plano, digite o identificador contextual e marque a autorização. A confirmação não é preenchida automaticamente, nem acionada por Enter.
6. Em Clonagem, escolha origem e destino independentemente, selecione a estratégia e gere o plano. Examine todas as partições e as ações de adaptação retornadas pelo servidor. Autorize a captura e aguarde `IMAGE_READY`; então prepare e autorize o destino com uma nova confirmação. As operações de bloco exigem Rescue; restauração física exige ainda confirmação local do Rescue.
7. Consulte progresso e resultado em Tarefas e logs; relatórios JSON podem ser exportados. Auditoria também pode ser exportada.

O painel expõe somente recursos implementados pelo controlador. A instalação Windows usa agente WinPE separado; boot, particionamento, clonagem, ddrescue e PXE não são apresentados como homologados. Dados, resultados e status nunca são simulados. A integridade de uma imagem não atesta que Windows/Linux iniciará em outro hardware.

## Clonagem integral

- `EXACT_RAW`: mesma capacidade, disco inteiro copiado exatamente.
- `PRESERVE_LAYOUT`: destino igual ou maior, mantendo partições e corrigindo a tabela GPT secundária quando aplicável. Exige `RESTORE_LAYOUT` no agente destino.
- `FIT`: preserva todas as partições e adapta somente a partição suportada escolhida pelo servidor. Exige `RESTORE_FIT` e espaço temporário saudável no Rescue; a adaptação é feita em cópia de trabalho, sem alterar a origem ou imagem do servidor.

O painel não calcula redimensionamento nem presume que um layout seja suportado. A pré-verificação do servidor fornece bloqueios, avisos, ações, partição selecionada e armazenamento temporário requerido. O modo FIT não permite truncar partições ou descartar conteúdo. Captura e destino possuem aprovações independentes; a aprovação do destino só é preparada depois da captura concluída e imagem validada. Progresso por etapa aparece na sessão e na aba Tarefas e logs.

## Arquivos e backups específicos

1. Configure as raízes autorizadas localmente no agente Windows. A interface recebe apenas ID, rótulo e permissões, sem o caminho absoluto do escopo.
2. Na aba Arquivos, escolha computador e raiz de leitura. Confirme autorização e solicite a listagem. Pastas abrem com duplo clique; Subir retorna ao pai, e Próxima página usa o cursor retornado pelo agente. Cada página contém até 200 entradas.
3. Adicione arquivos ou pastas à cesta e dê nome ao backup. Links/reparse não entram na seleção. Pastas incluem seu conteúdo e diretórios vazios. Gere e aprove o plano para enviar os dados ao repositório do servidor.
4. No catálogo, escolha um bundle `AVAILABLE`. Escolha independentemente o computador e a raiz de gravação, informe uma pasta **nova** e autorize a restauração. Se a pasta já existir, o agente bloqueia a operação; não existe sobrescrita ou mesclagem implícita.

Listagens usam jobs assinados e polling; a interface continua navegável durante a execução. Backups e restaurações aparecem em Tarefas e logs. A verificação de bundles inicia em segundo plano; acompanhe `VERIFYING` até `AVAILABLE` ou `FAILED`. Cópia de arquivos não promete snapshot VSS: alterações durante leitura fazem a operação falhar em vez de anunciar consistência inexistente.

## Verificações

Compilar com `dotnet build src/desktop/SLT.Desktop.Wpf/SLT.Desktop.Wpf.csproj`. O painel não realiza I/O de disco bruto e não contém motor de operações destrutivas: ele solicita planos ao servidor. A validação de identidade e autorização deve permanecer no servidor e no agente, mesmo que a interface já valide campos.

O harness `dotnet run --project src/desktop/tests/SLT.Desktop.SmokeTests` verifica construção XAML e barreiras de autorização/escopo sem rede nem discos físicos. Não substitui revisão visual em resolução/DPI reais nem homologação de clonagem em VMs/hardware.

## Arquivos locais do servidor e instalação Windows

Na aba Arquivos, **Importar arquivo local** e **Importar pasta local** copiam uma seleção do próprio servidor para um bundle no repositório, após autorização explícita. Aguarde `AVAILABLE` no catálogo antes de enviar para uma pasta nova no cliente. A seleção não é executada e não é enviada ao GitHub.

Na aba **Instalar sistema**, escolha uma ISO/WIM/ESD local, nomeie e importe. A importação e verificação são assíncronas. Depois de `AVAILABLE`, escolha o cliente em modo `WINPE`, o disco, a edição AMD64 e o firmware UEFI/GPT ou BIOS/MBR compatível. Confira a identidade do disco e a autorização de apagamento antes de gerar o plano. O diálogo mostra mídia, edição, firmware, destino, todas as partições atuais e a confirmação contextual vazia. Uma segunda confirmação local no WinPE é obrigatória. O reinício após instalação é opcional e começa desmarcado.

Os filtros da interface não substituem as validações do servidor e do agente. Um disco montado pode ser destino apenas no ambiente WinPE e após suas verificações locais; discos protegidos ou somente leitura permanecem bloqueados. Instalações e clones em computadores diferentes podem progredir simultaneamente. A conclusão da aplicação da imagem não comprova primeiro boot, drivers ou OOBE.

O harness Desktop passou 26 verificações sintéticas, incluindo modo/capacidade WinPE, mídia ainda importando, disco protegido, contrato de implantação e aprovação destrutiva. `--render artifacts/ui` produz PNGs claramente marcados como fixtures para revisão visual; não representam instalação real nem homologação.
