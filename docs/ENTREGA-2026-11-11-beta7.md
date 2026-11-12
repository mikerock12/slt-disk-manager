# Entrega 0.1.0-beta.7 — conexão por código, modo SLT, formatação e instalador

Publicada em 12/11/2026 a partir do merge `dcd01a9fc8dfaa5a4d26dfa9013a2d9376f14628`
([PR 15](https://github.com/mikerock12/slt-disk-manager/pull/15)).
[Release](https://github.com/mikerock12/slt-disk-manager/releases/tag/v0.1.0-beta.7).

Beta de laboratório. **Nada foi homologado em hardware físico.**

## O que mudou

- **Conexão por código de 8 dígitos.** O cliente encontra o servidor na rede e mostra um
  código; o operador o digita no painel. Não se digita mais endereço nem o pin de 64
  caracteres. [ADR 003](ADR/003-descoberta-e-pareamento-curto.md).
- **Renovação automática** do certificado do cliente Windows.
- **Modo SLT.** Um cliente Windows UEFI (Secure Boot desativado) reinicia no SLT Rescue
  sem pendrive e passa a ser controlado pelo servidor.
  [ADR 004](ADR/004-modo-slt-e-proxydhcp.md).
- **Formatar disco** pelo painel: NTFS, exFAT, FAT32 ou ext4, em GPT ou MBR.
- **ProxyDHCP opcional** para o boot pela rede sem configurar o roteador.
- **Painel redesenhado** e mensagem clara quando falta permissão de administrador.
- **Instalador:** atalhos, pasta do repositório, atualização de serviço instalado,
  `Set-ServerRepository.ps1`, serviço em `Program Files` (64 bits).

## Evidências

| O que | Resultado | Limite |
|---|---|---|
| Suítes automáticas | Controller 213/213; cliente Windows 67/67; painel 67/67; Network Boot 113/113; Go com `-race` e `vet`; scripts de serviço 43/43 | Arquivos e pastas sintéticos |
| Cliente Windows ↔ servidor reais em loopback | 6/6, incluindo descoberta e código curto sem endereço nem pin, e reconexão com o servidor registrado | Mesma máquina |
| Rescue ↔ servidor reais | 7/7 (captura, restauração, clone em lotes, revogação) | Arquivos sintéticos |
| Formatação com sfdisk/mkfs reais | GPT/MBR × NTFS, FAT32, ext4 e exFAT em arquivos via loop | Linux do CI |
| PXE BIOS e UEFI | [CI de ISO e laboratório do merge](https://github.com/mikerock12/slt-disk-manager/actions/runs/43123533851) | VMs, Secure Boot desativado |
| Modo SLT | Mesmo CI: VM UEFI iniciando pela partição EFI, pareamento por ticket, inventário e `FORMAT_DISK` GPT/NTFS por ordem do servidor, sem teclado no cliente; reuso do ticket e disco protegido recusados | O cliente Windows é representado pelas mesmas chamadas HTTPS. O passo `bcdedit`/partição EFI só tem testes com executor falso |
| ProxyDHCP | Mesmo CI: DHCP sem opções de boot; menu em BIOS e UEFI; endereço vindo do DHCP da rede antes, durante e depois | DHCP e SLT no mesmo host do laboratório |
| Instaladores | [CI do aplicativo no merge](https://github.com/mikerock12/slt-disk-manager/actions/runs/43123533873): instalação silenciosa, atalhos com alvo, argumentos e bit de elevação, serviço em `Program Files` a partir de processo de 32 bits, desinstalação preservando o estado | Serviço real não é instalado no teste |
| Leitura em Windows real (somente leitura) | Em um Windows 10 UEFI: localizador da partição EFI devolveu um único volume no formato esperado; a pasta `EFI` foi lida por esse caminho; `bcdedit /enum {fwbootmgr}` respondeu | Nada foi gravado nem alterado |

## Arquivos da release

Os 11 arquivos foram baixados e conferidos com `scripts/verify-release.py`: tamanho e
SHA256 iguais aos registrados pelo GitHub, arquivos `.sha256` e `SHA256SUMS.txt`
coerentes, CRC dos dois ZIPs (1.518 e 16 entradas) e agentes Go construídos do commit
da release com árvore limpa (`vcs.revision=dcd01a9…`, `vcs.modified=false`).

| Arquivo | Bytes | SHA256 |
|---|---:|---|
| SLT-Disk-Manager-Servidor-Setup-0.1.0-beta.7.exe | 127.901.169 | `f1a64abd50798be94ce45efd6588ef2e90c05d7d5c894abc70a03e9643b5dbe6` |
| SLT-Disk-Manager-Cliente-Setup-0.1.0-beta.7.exe | 38.101.721 | `f3c13af69c607ef0108e57515ca285da9eef900ac933a4a08c321aab5f48912c` |
| SLT-Disk-Manager-0.1.0-beta.7-win-x64.zip | 232.417.827 | `6e1fe4f7388ed335126353a59ee502b3ebef89cc8f5dadf1bcaad79499772516` |
| SLT-Rescue-0.1.0-beta.7-amd64.iso | 504.365.056 | `18d5e244bca8241ceb20a2b7cfa6f11d9fff10568c6ac8de690f6747887e23cc` |
| SLT-Netboot-0.1.0-beta.7-amd64.zip | 500.558.363 | `16b2e112fca849ec6ebc979508d4ff39ca7322d72468fa3fcf85de70a8a3f04a` |
| SHA256SUMS.txt | 340 | `3f3b5c7b1644c357b8494805637dff562020200928b0d8a9e37aae3ba47ebedd` |
| package-copyrights.tar.gz | 1.358.885 | `b68174c6226b467b854956e841bdffdb15783da00a301fbfd0eceeea3587584f` |
| packages.tsv | 12.256 | `ebca0b0273180c39a79b9c54eeae3eeabd9b7b4d3d3d26c577b14c5986c2bb54` |
| source-index.tsv | 12.387 | `30def1d1e2483cee9f34585e5a734af6e0a94b43f81b93b76fe7e4135442765f` |

O pacote Netboot traz o recibo do laboratório (`LAB-RECEIPT.json`) com todos os portões
aprovados, inclusive `sltModeGatePassed` e `proxyDhcpGatePassed`, e o carregador
`ipxe-local.efi` usado pelo modo SLT.

## Portal

[Página](https://softwares.smellsliketech.com.br/apps/disk-manager/) atualizada pelo
[PR 62](https://github.com/mikerock12/softwares-smells-like-tech/pull/62). Validação
local: 60 testes, build e dry-run. Em produção, por HTTP: páginas 200, API com os dois
instaladores da beta.7 (tamanhos e SHA256 iguais aos da tabela), downloads 302 para a
release e download privado 401 sem login. Não houve conferência visual por navegador.

## Limites e pendências

- Sem homologação em hardware físico.
- Modo SLT: só UEFI sem Secure Boot; BIOS/Legacy não atendido; não testado com BitLocker;
  o passo do Windows que cria a entrada de boot não foi exercitado em Windows real.
- Instalar Windows continua exigindo o cliente WinPE em pendrive.
- Demais itens em [PENDENCIAS](PENDENCIAS.md).

## Atualizar uma instalação anterior

1. Execute `SLT-Disk-Manager-Servidor-Setup-0.1.0-beta.7.exe` por cima da instalação e
   mantenha marcadas "Criar atalhos na área de trabalho" e "Atualizar o serviço do
   servidor já instalado". O serviço passa para `Program Files` se estava em
   `Program Files (x86)`; certificados, pin, computadores conectados e imagens são
   preservados.
2. Abra o painel pelo atalho **SLT Disk Manager - Painel**.
3. Para levar o repositório a outro disco, no PowerShell como Administrador, na pasta da
   instalação:
   `./install-server/Set-ServerRepository.ps1 -RepositoryDirectory "D:\SLT Disk Manager Repositorio" -MoveExisting`
   (acrescente `-ValidateOnly` para ver antes o que seria feito).
4. Nos clientes, execute o instalador do cliente por cima; com o serviço instalado,
   mantenha marcada a tarefa de atualização.
