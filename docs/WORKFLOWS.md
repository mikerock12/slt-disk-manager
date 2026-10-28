# Clone, arquivos e instalação em paralelo

## Clonar cliente 1 para cliente 2

Inicie os dois clientes pelo SLT Rescue/Linux de manutenção independente. Confira
URL/pin e pareie cada um no painel. Origem e destino devem estar sem partições
montadas. O agente destino deve ter `--allow-physical-write`.

### Em lotes (padrão)

Na aba Clonagem, mantenha a transferência **Em lotes**. Origem e destino ficam ligados
ao mesmo tempo: a origem lê o disco e envia lotes de 4 MiB, o destino grava cada lote
direto no disco. O servidor guarda no máximo 256 MiB por vez (ajustável com
`--stream-window-chunks`) e apaga cada lote assim que o destino confirma a gravação.
Clonar dois HDDs de 1 TB não exige 1 TB livre no servidor nem disco extra no Rescue.

Funciona para EXATO, PRESERVAR e AJUSTAR quando o destino é igual ou maior. No AJUSTAR
para disco maior, as partições posteriores (recuperação/OEM) já são gravadas deslocadas
e a partição NTFS/ext4 é expandida no próprio destino depois de reler e comparar todos os
dados. Autorize a origem e depois o destino (APAGAR no painel e no teclado local do
Rescue). A origem termina assim que enviar o último lote; o destino segue até verificar.
Uma queda de rede, de energia ou cancelamento interrompe o clone: o destino fica parcial
e é preciso planejar de novo, com a origem intacta. Não há imagem para repetir.

### Imagem completa no servidor

Use quando precisar reduzir para um disco menor, restaurar a mesma origem mais de uma
vez ou guardar a imagem. O servidor precisa de espaço igual à capacidade inteira da
origem. Para AJUSTAR nesse modo, o agente destino precisa de `--scratch-dir` apontando
para armazenamento saudável separado do destino.

Na ISO SLT Rescue, use `sudo slt-start` para configurar Ethernet ou Wi-Fi, conferir
URL/pin e iniciar o agente. Para AJUSTAR, prepare antes um armazenamento temporário
independente, já formatado e com espaço livre suficiente. Não use a origem, o destino
ou o pendrive de boot como scratch. Identifique o dispositivo pelo modelo, serial,
tamanho e UUID; não suponha que `/dev/sdb` será sempre o mesmo disco:

```bash
lsblk -o NAME,SIZE,MODEL,SERIAL,FSTYPE,UUID,MOUNTPOINTS
```

Depois de conferir fisicamente o disco extra, monte apenas sua partição de scratch.
O exemplo abaixo exige substituir UUID_DO_SCRATCH pelo UUID real. Ele não formata
nem apaga a partição; se houver erro de filesystem, pare e revise esse armazenamento.
Não monte as partições de origem/destino e não use opções para remover hibernação.

```bash
sudo mkdir -p /mnt/slt-scratch
sudo mount /dev/disk/by-uuid/UUID_DO_SCRATCH /mnt/slt-scratch
findmnt /mnt/slt-scratch
df -h /mnt/slt-scratch
sudo mkdir -p /mnt/slt-scratch/SLT
sudo slt-start
```

Informe `/mnt/slt-scratch/SLT` no assistente e habilite escrita somente no cliente
destino. Ao finalizar todas as tarefas, encerre o agente e use
`sudo umount /mnt/slt-scratch` antes de remover o armazenamento extra. Se estiver
ocupado, não force a remoção; confira tarefas/processos ainda ativos.

Na aba Clonagem, selecione origem, disco, destino e estratégia:

- EXATO: todos os setores, com a mesma capacidade e tamanho de setor.
- PRESERVAR: mesma disposição de partições em disco maior, com GPT secundária relocada.
- AJUSTAR: redimensiona um volume NTFS/ext4 e move as partições posteriores,
  preservando boot, recuperação/OEM e identificadores. O mínimo real depende da
  análise do filesystem, não apenas dos bytes de arquivos visíveis.

O plano informa alterações e espaço de scratch. Para adaptação, reserve no Rescue
origem + destino + maior entre destino/maior partição + 64 MiB. O servidor precisa
armazenar a imagem RAW inteira da origem. A cópia de trabalho é verificada antes
da primeira gravação no destino. Não reduza o disco original.

Autorize a captura e aguarde imagem disponível. Depois prepare o destino e digite
APAGAR no painel e no teclado local do Rescue. O destino inteiro será substituído.
Os jobs de outros PCs podem continuar; cada máquina executa uma tarefa por vez.
Não há transmissão direta A→B: a imagem validada no servidor permite conferir a
origem e repetir o planejamento sem recapturar.

FIT bloqueia NTFS hibernado/sujo, BitLocker/LUKS, RAID/LVM, discos dinâmicos e MBR
com partições estendidas/lógicas. Não desative esses bloqueios. Mesmos GUIDs e
conteúdo de boot não comprovam boot no novo hardware; valide firmware, drivers,
recuperação, ativação e dados em laboratório antes de usar dados de clientes.

## Copiar arquivos escolhidos

No cliente Windows, crie um JSON de pastas autorizadas, por exemplo:

```json
[
  {"id":"backup","label":"Arquivos para backup","path":"C:\\Users\\Cliente\\Documents","allowRead":true,"allowWrite":false},
  {"id":"entregas","label":"Receber arquivos","path":"C:\\SLT-Entregas","allowRead":true,"allowWrite":true}
]
```

As pastas devem existir. Inicie Cliente.cmd e informe o JSON, ou use o instalador
com `-FileRootsPath`. Sem configuração, o agente oferece somente inventário/SMART.
O instalador mantém uma cópia privada da configuração no estado do serviço.

Na aba Arquivos, navegue pela raiz autorizada, selecione arquivos/pastas e crie o
backup. Aguarde disponível. Para enviar arquivos do próprio servidor, use Importar
arquivo/pasta, informe caminho local e autorização e aguarde o catálogo concluir.
Escolha o cliente/raiz e uma **pasta nova** para receber o backup. Isso também
permite enviar um backup feito no cliente 1 ao cliente 2.

Os hashes são conferidos na origem, servidor e destino. Diretórios vazios e nomes
Unicode são preservados. Não há VSS: arquivos abertos para gravação ou alterados
durante a leitura causam falha. Links, junctions e hardlinks são bloqueados; ACLs,
ADS e configurações do Windows não fazem parte do backup de arquivos. Use clone
integral para preservar o disco inteiro. Não há substituição de pasta existente.

## Instalar Windows no cliente 3

Na aba Instalar Windows importe a ISO, WIM ou ESD local. ISO requer 7-Zip instalado
no servidor; a importação extrai apenas install.esd/install.wim, enumera edições e
calcula SHA256. A mídia fica somente no repositório local. A mídia fornecida pelo
proprietário contém Windows 10 Home, Home Single Language, Education e Pro AMD64.

Use pendrive oficial de instalação Windows AMD64 e o gerador
`windows-pe/New-WinPEKit.ps1` incluído no pacote. Leia o guia dessa pasta: o kit não
formata o pendrive nem inclui componentes Microsoft. Ele inicia o agente no WinPE,
mantém o Windows Setup aguardando e requer scratch físico fora do destino. O
gerador recebe URL/pin públicos, nunca chave privada/token administrativo.

No cliente 3 iniciado em WinPE, confira e aprove o pareamento. Escolha cliente,
disco, edição e firmware iguais ao modo de boot do pendrive. Planeje e confirme
APAGAR no painel e localmente no cliente. O agente confere mídia, ferramentas,
identidade e volumes antes de DiskPart, DISM e BCDBoot. BIOS também exige bootsect
da mídia legítima. O disco de destino é totalmente apagado.

Reinício só ocorre quando marcado explicitamente e após confirmação do resultado
pelo servidor. Aplicar imagem/criar arquivos de boot não prova primeiro boot/OOBE.
Esse teste ainda precisa de VM/hardware. Não há quebra de ativação ou licença.

## Se falhar

Cancelar/desconectar durante gravação pode deixar destino parcial. Consulte a
tarefa e os registros, confira os discos e planeje nova operação. Nunca remova
o ledger para reenviar o mesmo job. A origem de um clone adaptável não é alterada.
Não publique imagens, mídias Windows, certificados ou dados de clientes no GitHub
ou no backup das fontes.
