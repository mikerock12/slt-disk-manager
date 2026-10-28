# Primeiro teste na bancada

## Servidor Windows 10/11 x64

Use Ethernet e uma rede privada da assistência. Escolha o IPv4 do servidor em
`ipconfig`, por exemplo `192.168.1.10`, e a sub-rede real, por exemplo
`192.168.1.0/24`. Não copie os exemplos se sua rede for diferente.

Extraia o pacote em uma pasta própria. Execute `Servidor.cmd` e preencha IP/CIDR.
O painel abre usando `state/server/connection.json` (segredo administrativo local).
O arquivo `state/server/server-info.txt` mostra URL e pin que podem ser conferidos
nos clientes. Nunca copie a pasta `state/server` para um cliente.

Para serviço e firewall, abra PowerShell **como Administrador**:

```powershell
./install-server/Install-Server.ps1 -PackageDirectory "$PWD/server" -Bind 192.168.1.10 -AllowedSubnet 192.168.1.0/24
./desktop/SLT.Desktop.exe --connection "$env:ProgramData/SLT Disk Manager/Controller/connection.json"
```

O serviço usa a porta TCP 7443 só no IP selecionado e perfil de firewall privado.
Pare o servidor portátil antes de instalar o serviço na mesma porta. No modo
portátil, o processo continua em segundo plano; para encerrá-lo use Gerenciador
de Tarefas e identifique `SLT.Controller.Host`, ou pare o serviço quando instalado.
Não mantenha dois controladores usando o mesmo diretório de estado.

## Dois computadores clientes

Copie apenas a pasta `client`, `Cliente.cmd` e `Iniciar-Cliente.ps1` para cada PC,
mantendo a estrutura. Execute `Cliente.cmd`, informe a URL e confira o pin com o
servidor. Não aceite um pin recebido de uma máquina desconhecida.

O client exibe um código temporário. No painel do servidor, abra Pareamento,
selecione a solicitação correspondente e digite o código conferido no PC.
Depois do pareamento, o inventário aparece e é atualizado periodicamente.
Repita no segundo PC; cada um recebe seu próprio certificado.

O primeiro teste pode ser feito em console. Para instalar como serviço, consulte
`install-agent/Install-Agent.ps1 -?`. Execute como Administrador para coletar
mais dados de partições/SMART; falta de permissão aparece nas observações.

## Diagnóstico

Selecione computador e disco, marque autorização do responsável, crie plano
SMART e digite a confirmação exibida. SMART exige `smartctl` disponível conforme
README do agente; na ausência da ferramenta, a operação retorna erro explícito.
Não são executados CHKDSK de reparo nem testes de escrita no Windows.

## Captura, restauração e clone

1. Primeiro homologue em VM/discos descartáveis. No checkout de desenvolvimento,
   `scripts/test-all.ps1 -Integration` executa os testes sintéticos; esse comando
   exige SDK/fonte e não está incluído no pacote do usuário.
2. Baixe a ISO SLT Rescue em Releases do repositório, confira seu SHA256 e prepare
   uma mídia de boot independente usando sua ferramenta de gravação de ISO.
   Inicie por ela e execute `sudo slt-start`; confira pin e faça novo pareamento.
   O binário na pasta `rescue` serve para um Linux já preparado, conforme seu README.
3. O disco de origem deve estar offline, sem partições montadas, com serial e
   fingerprint confiáveis.
4. Na aba Clonagem, a transferência **Em lotes** (padrão) liga origem e destino ao
   mesmo tempo: o servidor guarda só uma janela de 256 MiB e apaga cada lote depois
   que o destino o grava, sem disco extra. Serve para EXATO, PRESERVAR e AJUSTAR para
   disco igual ou maior. Autorize a origem e depois o destino.
5. **Imagem completa no servidor** fica para reduzir para disco menor, restaurar a
   mesma origem várias vezes ou guardar a imagem: exige espaço igual à capacidade
   inteira da origem; crie e aprove CAPTURE e aguarde imagem AVAILABLE. RESTORE RAW
   exato exige a mesma capacidade e setor lógico. Consulte WORKFLOWS.md.
6. Digite o identificador `APAGAR ...` no painel. O Rescue precisa ter sido iniciado
   com `--allow-physical-write` e exige a mesma confirmação no teclado local.
7. Aguarde a releitura/verificação. Igualdade de bytes não comprova boot em outro
   hardware; controladora, UEFI e ativação podem exigir trabalho posterior.

O USB que hospeda o Rescue e os discos montados/sistema são bloqueados. Não há
transição automática de boot do Windows nesta versão. Não desative Secure Boot
como procedimento padrão; use mídia compatível validada no laboratório.

## Arquivos e Windows em outro cliente

Configure as pastas autorizadas no cliente conforme WORKFLOWS.md e use a aba
Arquivos para backups selecionados e envios. Importe arquivos/pastas do próprio
servidor no catálogo e escolha uma pasta nova no cliente para recebê-los.

A aba Instalar Windows importa sua ISO/WIM/ESD e permite escolher edição e destino
WinPE. O kit em windows-pe prepara a inicialização do agente no pendrive oficial,
sem incluir mídia Microsoft. Clone nos clientes1/2 e instalação no cliente3 podem
andar em paralelo. Instalação/primeiro boot Windows ainda exigem homologação em VM;
consulte WORKFLOWS.md e windows-pe/README.md para preparar o ambiente.

## Falhas, consentimento e dados

Cancelar durante RESTORE pode deixar destino parcial. O agente para no próximo
checkpoint e registra erro/cancelamento; não recomeça silenciosamente. Reinício
do controlador marca tarefas ativas INTERRUPTED. Confira fisicamente o destino e
replaneje. Não apague o ledger do agente para repetir uma tarefa antiga.

Registre autorização do responsável na ordem de serviço. Guarde imagens e
diretórios de estado em armazenamento protegido (por exemplo, volume BitLocker
sob sua gestão); esta versão não implementa criptografia própria de imagens,
retenção automática nem cofre de chaves. Nunca envie dados de clientes ao GitHub.

Certificados cliente expiram em 30 dias. Para substituir credenciais, revogue a
identidade anterior, pare o agente e faça novo pareamento em diretório privado
novo. Não publique chave/CSR secret/token em chamados de suporte.
