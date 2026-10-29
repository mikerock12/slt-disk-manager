# Primeiro teste na bancada

## Servidor Windows 10/11 x64

Use Ethernet e uma rede privada da assistência.

### 1. Fixe o IP do servidor

O servidor escuta só no IPv4 escolhido (porta 7443) e cada cliente guarda esse
endereço. Se o roteador trocar o IP, os clientes deixam de conectar. Antes de instalar:

- **Recomendado — reserva de DHCP no roteador:** no painel do roteador, procure
  "Reserva de DHCP" ou "IP estático por MAC" e associe o endereço MAC da placa de rede
  do servidor (`ipconfig /all`, "Endereço Físico") a um IP, por exemplo `192.168.1.10`.
  O Windows continua em "Obter IP automaticamente" e recebe sempre o mesmo endereço.
- **Ou IP manual no Windows:** Configurações → Rede e Internet → Ethernet → Atribuição de
  IP → Editar → Manual. Informe IP, máscara, gateway e DNS, escolhendo um IP fora da faixa
  que o roteador distribui.

Confirme com `ipconfig` e anote também a sub-rede real, por exemplo `192.168.1.0/24`.
Não copie os exemplos se sua rede for diferente. Só o servidor precisa de IP fixo.

### 2. Instale o servidor

Use **SLT-Disk-Manager-Servidor-Setup** na bancada. Ele traz servidor, painel, 7-Zip,
agentes Rescue/WinPE e guias; não instala o cliente. Para rodar como serviço do Windows,
marque a opção na instalação e informe o IP fixo e a sub-rede: o instalador pede
permissão de administrador, cria o serviço e libera a porta 7443 só nessa rede.

Sem serviço (ou no pacote portátil), execute `Servidor.cmd` e preencha IP/CIDR. O painel
abre usando `state/server/connection.json` (segredo administrativo local). O arquivo
`state/server/server-info.txt` mostra URL e pin que podem ser conferidos nos clientes.
Nunca copie a pasta `state/server` para um cliente.

Para instalar o serviço manualmente, abra PowerShell **como Administrador**:

```powershell
./install-server/Install-Server.ps1 -PackageDirectory "$PWD/server" -Bind 192.168.1.10 -AllowedSubnet 192.168.1.0/24
./desktop/SLT.Desktop.exe --connection "$env:ProgramData/SLT Disk Manager/Controller/connection.json"
```

O serviço usa a porta TCP 7443 só no IP selecionado e perfil de firewall privado.
Pare o servidor portátil antes de instalar o serviço na mesma porta. No modo
portátil, o processo continua em segundo plano; para encerrá-lo use Gerenciador
de Tarefas e identifique `SLT.Controller.Host`, ou pare o serviço quando instalado.
Não mantenha dois controladores usando o mesmo diretório de estado.

Se o IP do servidor precisar mudar, reinstale o serviço com o novo `-Bind` e aponte
cada cliente para a nova URL; o certificado e o pin do servidor são mantidos.

## Dois computadores clientes

Use **SLT-Disk-Manager-Cliente-Setup** em cada PC atendido. Ele traz só o cliente
Windows e o smartctl; pode coexistir com o servidor no mesmo PC. Execute o atalho
"Cliente Windows" (ou marque o serviço na instalação), informe a URL do servidor com o
IP fixo e confira o pin com o servidor. Não aceite um pin recebido de uma máquina
desconhecida. No pacote portátil, copie apenas a pasta `client`, `Cliente.cmd` e
`Iniciar-Cliente.ps1`, mantendo a estrutura.

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

## Arquivos e sistemas em outros clientes

Configure as pastas autorizadas no cliente conforme WORKFLOWS.md e use a aba
Arquivos para backups selecionados e envios. Importe arquivos/pastas do próprio
servidor no catálogo e escolha uma pasta nova no cliente para recebê-los.

A aba Instalar sistema importa ISO/WIM/ESD do Windows ou a ISO oficial do
Xubuntu/Ubuntu. Windows é instalado com o cliente no WinPE (o kit em windows-pe
prepara o pendrive oficial, sem incluir mídia Microsoft); Xubuntu/Ubuntu com o
cliente no SLT Rescue em UEFI. Informe nome do computador e usuário: o servidor gera
uma senha temporária, mostrada uma única vez. Instalações em clientes diferentes e
clones podem andar em paralelo. A mesma aba converte um disco Windows MBR para
GPT/UEFI pelo WinPE. Consulte WORKFLOWS.md e windows-pe/README.md.

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
