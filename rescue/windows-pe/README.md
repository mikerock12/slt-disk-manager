# Instalação Windows com SLT WinPE

O kit inicia o agente Go no ambiente de instalação **Windows PE AMD64** da mídia
oficial do operador. Não inclui Windows, ADK, ISO, licença, senha ou chave de produto.
O gerador cria uma pasta nova; não monta imagens, formata USB nem altera o boot do PC.

## Preparação

1. Crie seu pendrive oficial de instalação Windows AMD64 com ferramentas licenciadas.
2. O pacote Windows já inclui `windows-pe/slt-winpe-agent.exe`. Somente no checkout
   de desenvolvimento, compile `cmd/slt-winpe-agent` com
   `GOOS=windows GOARCH=amd64 CGO_ENABLED=0` ou use `scripts/build.ps1`.
3. Separe armazenamento físico **NTFS**, saudável, com espaço para WIM/ESD + 2 GiB.
   Ele nunca pode ser o disco a instalar. FAT32 não fornece a ACL privada necessária;
   RAM X: é recusada para a mídia grande. Confira a letra desse armazenamento no WinPE.
4. No servidor, gere o kit abaixo. O endereço e o pin são públicos e devem ser
   conferidos diretamente no controlador; não use token administrativo no pendrive.

Na raiz do pacote extraído (ou da pasta de instalação, com o componente offline):

```powershell
./windows-pe/New-WinPEKit.ps1 `
  -OutputDirectory "$env:USERPROFILE/Desktop/SLT-WinPE-Kit" `
  -AgentPath ./windows-pe/slt-winpe-agent.exe `
  -Server https://192.168.1.10:7443 -ServerPin HASH_SHA256_64_HEXADECIMAIS `
  -ScratchDirectory 'E:\SLT-Scratch'
```

A pasta de saída deve ser nova e seu diretório pai deve existir. Ajuste IP, pin e
letra do scratch para a bancada. No checkout do código-fonte, use este exemplo:

```powershell
./rescue/windows-pe/New-WinPEKit.ps1 `
  -OutputDirectory ./artifacts/windows-pe-kit `
  -AgentPath ./artifacts/slt-winpe-agent.exe `
  -Server https://192.168.1.10:7443 -ServerPin HASH_SHA256_64_HEXADECIMAIS `
  -ScratchDirectory 'E:\SLT-Scratch'
```

5. Revise `LEIA-ME.md` e `SLT/kit-manifest.json`. Copie a pasta `SLT` e
   `Autounattend.xml` para a raiz do pendrive oficial. Se já existe answer file,
   preserve-o separadamente e revise os conflitos; não substitua sem conferir.
6. Inicie o cliente pelo pendrive, no firmware correspondente à instalação desejada.
   A rede inicializa e o agente solicita pareamento. Aprove e confira o código físico
   no servidor. A identidade WINPE é separada da identidade Windows/Rescue.
7. Importe ISO/WIM/ESD no controlador, selecione cliente/disco/edição/firmware,
   registre autorização e confirme o plano. Depois da transferência verificada da
   mídia, digite a confirmação novamente **no teclado do cliente** para apagar.

O agente usa inventário nativo somente leitura, protege os discos do sistema WinPE,
pendrive de boot, executável, estado e scratch; recusa discos sem serial/modelo ou
somente leitura. Reconfere fingerprint e capacidade antes de particionar. Montagens
do Windows instalado são volumes inativos dentro do PE, não uma clonagem ao vivo.

GPT/UEFI cria EFI FAT32 260 MiB, MSR 16 MiB e Windows NTFS. BIOS/MBR cria sistema NTFS
ativo de 550 MiB e Windows NTFS; requer setor lógico de 512 bytes, disco até 2 TiB e
`boot/bootsect.exe` da mídia oficial. A configuração de boot depende de DISM,
BCDBoot e, no modo BIOS, Bootsect. Os programas são executados com argumentos tipados;
o servidor não fornece comandos arbitrários. Tipos/tamanhos das partições, letras,
extents e filesystems são conferidos por IOCTL antes da aplicação da imagem.

O answer file mantém a etapa WindowsPE aguardando o agente. Se o kit não for
encontrado ou o agente falhar, mantém um prompt bloqueando a continuação automática
do Setup. Não contém seleção automática de disco ou ImageInstall. O modo de fallback
manual é Shift+F10 no instalador e `D:\SLT\Start-SLT.cmd`, ajustando a letra do USB.
Para sair, desligue o cliente; não prossiga no Setup sem revisar o estado do disco.

As credenciais e ledger ficam em `X:\SLT-State`, privados e voláteis: ao desligar,
revogue a identidade anterior e pareie novamente. Staging/relatório permanecem no
scratch para diagnóstico; podem conter arquivos de instalação e dados operacionais,
portanto não os publique nem os inclua no backup público das fontes.

## Estado de validação

Testes sintéticos verificam planos, hashes, corrupção, troca de disco, confirmação,
cancelamento e falhas de comandos com executores falsos. Nunca chamam DiskPart/DISM
reais nem abrem discos físicos. Build Windows/Linux e recusa fora de PE são verificáveis
no desenvolvimento; isso **não comprova instalação/boot em VM ou hardware**.

Antes de liberar escrita em computadores com dados, homologue numa VM descartável:
rede/pareamento, UEFI e BIOS, disco independente de scratch, instalação de edição
AMD64, falha/cancelamento e primeiro boot/OOBE. O resultado informa
`bootVerified:false` até essa verificação local. Outros clientes podem executar
clone/arquivos em paralelo, pois cada tarefa tem trava por cliente.

Falha de instalação encerra o agente após enviar o resultado. Não inicia a próxima
tarefa automaticamente: o disco pode estar parcial e ferramentas Microsoft podem
ter processos auxiliares. Confira o encerramento desses processos e o disco antes
de relançar o agente ou reiniciar; o ledger não permite repetir o mesmo job.

Referências oficiais: [inicialização do WinPE](https://learn.microsoft.com/en-us/windows-hardware/manufacture/desktop/wpeinit-and-startnetcmd-using-winpe-startup-scripts),
[scripts DiskPart](https://learn.microsoft.com/en-us/windows-server/administration/windows-commands/diskpart-scripts-and-examples),
[DISM](https://learn.microsoft.com/en-us/windows-hardware/manufacture/desktop/dism-image-management-command-line-options-s14),
[BCDBoot](https://learn.microsoft.com/en-us/windows-hardware/manufacture/desktop/bcdboot-command-line-options-techref-di).
