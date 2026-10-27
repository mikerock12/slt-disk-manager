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
6. Para clonar A → B, capture o disco de A, verifique a imagem e restaure no disco de B. As operações de bloco exigem Rescue; restauração física exige ainda confirmação local do Rescue.
7. Consulte progresso e resultado em Tarefas e logs; relatórios JSON podem ser exportados. Auditoria também pode ser exportada.

O painel expõe somente recursos implementados pelo controlador. Instalação de SO, particionamento, ddrescue e PXE não são apresentados como homologados. Dados, resultados e status nunca são simulados. A integridade de uma imagem não atesta que Windows/Linux iniciará em outro hardware.

## Verificações

Compilar com `dotnet build src/desktop/SLT.Desktop.Wpf/SLT.Desktop.Wpf.csproj`. O painel não realiza I/O de disco bruto e não contém motor de operações destrutivas: ele solicita planos ao servidor. A validação de identidade e autorização deve permanecer no servidor e no agente, mesmo que a interface já valide campos.
