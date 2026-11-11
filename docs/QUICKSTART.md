# Primeiro teste na bancada

Beta de laboratório. Nada aqui foi homologado em hardware físico: faça o primeiro
ciclo em máquinas virtuais ou discos descartáveis.

## Servidor Windows 10/11 x64

Use Ethernet e uma rede privada da assistência.

### 1. Fixe o IP do servidor

O servidor escuta só no IPv4 escolhido (porta 7443). Antes de instalar:

- **Recomendado — reserva de DHCP no roteador:** no painel do roteador, procure
  "Reserva de DHCP" ou "IP estático por MAC" e associe o endereço MAC da placa de rede
  do servidor (`ipconfig /all`, "Endereço Físico") a um IP, por exemplo `192.168.1.10`.
  O Windows continua em "Obter IP automaticamente" e recebe sempre o mesmo endereço.
- **Ou IP manual no Windows:** Configurações → Rede e Internet → Ethernet → Atribuição de
  IP → Editar → Manual. Informe IP, máscara, gateway e DNS, escolhendo um IP fora da faixa
  que o roteador distribui.

Confirme com `ipconfig` e anote também a sub-rede real, por exemplo `192.168.1.0/24`.
Só o servidor precisa de IP fixo. Se o endereço mudar mantendo o certificado, os
clientes desta versão reencontram o servidor sozinhos.

### 2. Instale o servidor

Use **SLT-Disk-Manager-Servidor-Setup**. Ele traz servidor, painel, 7-Zip, agentes
Rescue/WinPE e guias; não instala o cliente. Deixe marcado "Criar atalhos na área de
trabalho".

Para rodar como serviço do Windows, marque a opção e informe:

- o IP fixo e a sub-rede;
- a **pasta do repositório**: é onde ficam as imagens de disco, as mídias ISO/WIM/ESD
  importadas e os perfis de boot. Escolha um disco com bastante espaço. O padrão é
  `<unidade da instalação>\SLT Disk Manager Repositorio`.

O instalador pede permissão de administrador, cria o serviço em `Program Files`, libera
as portas 7443 (TCP) e 7444 (UDP, busca automática) só na rede privada e cria o atalho
**SLT Disk Manager - Painel**.

**O painel do serviço abre como administrador.** O serviço guarda a chave de acesso em
`connection.json`, que só Administradores e SISTEMA podem ler; por isso o atalho pede a
permissão do Windows (UAC) a cada abertura. Se o painel for aberto sem essa permissão,
ele explica o motivo e oferece "Reabrir como administrador".

Sem serviço (ou no pacote portátil), use o atalho **Servidor e painel** (`Servidor.cmd`)
e preencha IP/CIDR. Se o serviço estiver instalado, esse atalho abre o painel do serviço
em vez de iniciar um segundo servidor.

#### Já tem o serviço instalado de uma versão anterior?

Execute o instalador novo por cima e deixe marcada a tarefa **Atualizar o serviço do
servidor já instalado**. Certificados, pin, token, computadores conectados e imagens são
preservados. Se o serviço antigo estava em `Program Files (x86)`, ele passa para
`Program Files`.

Para **mudar a pasta do repositório** de um servidor já instalado (por exemplo, tirar do
C: e levar para o D:), abra o PowerShell como Administrador na pasta da instalação:

```powershell
./install-server/Set-ServerRepository.ps1 -RepositoryDirectory "D:\SLT Disk Manager Repositorio" -MoveExisting
```

O script para o serviço, cria a pasta só para Administradores e SISTEMA, move imagens,
mídias e perfis de boot conferindo o SHA256 de cada arquivo, só então apaga a origem,
atualiza o serviço e o reinicia. Use `-ValidateOnly` para ver o que seria feito sem
alterar nada. Certificados, pin, token e pareamentos não mudam. A desinstalação nunca
apaga o repositório nem a pasta de estado.

Instalação manual do serviço (PowerShell como Administrador):

```powershell
./install-server/Install-Server.ps1 -PackageDirectory "$PWD/server" -Bind 192.168.1.10 -AllowedSubnet 192.168.1.0/24 -RepositoryDirectory "D:\SLT Disk Manager Repositorio"
```

## Conectar os computadores dos clientes

Use **SLT-Disk-Manager-Cliente-Setup** em cada PC atendido. Abra o atalho
**SLT Disk Manager - Cliente** (ele pede permissão de administrador para ler SMART e
permitir o modo SLT).

1. O cliente **procura o servidor na rede sozinho** e mostra um **código de 8 dígitos**.
2. No servidor, o painel avisa em destaque que um computador quer conectar.
3. Digite o código mostrado na tela do cliente e clique em **Conectar este computador**.

Não é mais preciso digitar endereço nem o pin de 64 caracteres. O código confirma que o
cliente está falando com este servidor: se outro computador da rede se passar pelo
servidor, o código não confere e a conexão é recusada. Leia sempre o código na tela do
cliente; o painel não o exibe de propósito.

Se a rede bloquear a busca automática, informe só o endereço
(`Iniciar-Cliente.ps1 -Server https://192.168.1.10:7443`); o código de 8 dígitos
continua valendo. Servidores anteriores à beta.7 ainda exigem endereço e pin.

Para deixar o cliente sempre conectado, marque o serviço na instalação. A janela mostra
o código uma vez, durante a instalação. Atualizações posteriores usam a tarefa
**Atualizar o serviço do cliente já instalado** e preservam a conexão.

## Modo SLT: clonar, formatar e instalar sem pendrive

Para mexer no disco de um computador, o Windows dele não pode estar em uso. O **modo
SLT** reinicia o computador no sistema SLT, carregado deste servidor pela rede:

1. No servidor, deixe o **Boot pela rede** em execução com o pacote `SLT-Netboot` desta
   versão importado (página "Boot pela rede"; veja [operação](NETWORK-BOOT-OPERACAO.md)).
2. No painel, selecione o cliente Windows e clique em **Reiniciar em modo SLT…**, ou no
   próprio cliente use o atalho **Reiniciar em modo SLT Disk Manager** (ou a tecla R na
   janela do cliente).
3. Confirme. O Windows avisa e reinicia; em um ou dois minutos o computador volta a
   aparecer no painel como **Modo SLT**, sem digitar nada nele.
4. Use as páginas Clonagem, Instalar sistema, Disco e imagem, ou o botão **Formatar
   disco…**. Cada gravação pede a confirmação digitada no painel.
5. Para voltar ao Windows, reinicie o computador.

Requisitos e limites:

- firmware **UEFI com Secure Boot desativado**, cabo de rede e cliente instalado como
  serviço ou aberto como administrador;
- computadores em **BIOS/Legacy** não reiniciam em modo SLT: use o boot pela rede (F12)
  ou o pendrive SLT Rescue. O painel mostra o motivo quando o botão não está disponível;
- se o servidor ou a rede falharem, o computador volta ao Windows sozinho; nada é
  gravado no disco pelo reinício;
- instalar **Windows** continua usando o cliente WinPE (pendrive); o modo SLT cobre
  clonagem, formatação, captura, restauração e instalação de Ubuntu/Xubuntu.

## Diagnóstico

Na página Computadores, selecione computador e disco e use **Consultar SMART** ou
**Atualizar inventário**. Não são executados CHKDSK de reparo nem testes de escrita no
Windows.

## Formatar um disco

Com o computador em modo SLT (ou no SLT Rescue com gravação habilitada), selecione o
disco e clique em **Formatar disco…**. Escolha o sistema de arquivos (NTFS, exFAT, FAT32
ou ext4), a tabela (GPT ou MBR) e, se quiser, um rótulo. Tudo o que está no disco é
apagado e ele fica com uma única partição. O disco do próprio sistema em execução,
discos montados e discos protegidos contra gravação são recusados.

## Captura, restauração e clone

1. Primeiro homologue em VM/discos descartáveis.
2. Coloque origem e destino em modo SLT. Alternativas: boot pela rede (F12) ou a ISO SLT
   Rescue em pendrive, com `sudo slt-start` (deixe o endereço vazio para o Rescue
   procurar o servidor e mostrar o código de 8 dígitos).
3. O disco de origem deve estar offline, sem partições montadas, com serial e
   fingerprint confiáveis.
4. Na página Clonagem, a transferência **Em lotes** (padrão) liga origem e destino ao
   mesmo tempo: o servidor guarda só uma janela de 256 MiB e apaga cada lote depois
   que o destino o grava. Serve para EXATO, PRESERVAR e AJUSTAR. Autorize a origem e
   depois o destino.
5. **Imagem completa no servidor** serve para restaurar a mesma origem várias vezes ou
   guardar a imagem: exige espaço igual à capacidade inteira da origem no repositório.
6. Digite o identificador `APAGAR ...` no painel. Em modo SLT a gravação segue; no
   Rescue iniciado por pendrive ou PXE, o cliente pede a mesma confirmação no teclado.
7. Aguarde a releitura/verificação. Igualdade de bytes não comprova boot em outro
   hardware; controladora, UEFI e ativação podem exigir trabalho posterior.

## Arquivos e sistemas em outros clientes

Configure as pastas autorizadas no cliente conforme WORKFLOWS.md e use a página
Arquivos para backups selecionados e envios.

A página Instalar sistema importa ISO/WIM/ESD do Windows ou a ISO oficial do
Xubuntu/Ubuntu. Windows é instalado com o cliente no WinPE (o kit em windows-pe
prepara o pendrive oficial, sem incluir mídia Microsoft); Xubuntu/Ubuntu com o
cliente no SLT Rescue/modo SLT em UEFI. O servidor gera uma senha temporária, mostrada
uma única vez. A mesma página converte um disco Windows MBR para GPT/UEFI pelo WinPE.

## Falhas, consentimento e dados

Cancelar durante uma gravação pode deixar o destino parcial. O agente para no próximo
checkpoint e registra erro/cancelamento; não recomeça silenciosamente. Reinício do
servidor marca tarefas ativas como interrompidas. Confira o destino e replaneje.

Registre a autorização do responsável na ordem de serviço. Guarde o repositório e a
pasta de estado em armazenamento protegido (por exemplo, volume BitLocker sob sua
gestão); esta versão não implementa criptografia própria de imagens, retenção
automática nem cofre de chaves. Nunca envie dados de clientes ao GitHub.

Certificados de cliente valem 30 dias e o cliente Windows renova o seu sozinho, dez
dias antes do vencimento, sem código novo. Só um cliente que ficar mais de 30 dias sem
falar com o servidor precisa ser desconectado no painel e conectado de novo.
