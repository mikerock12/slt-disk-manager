# Network Boot — evidências por fase

Beta.7: o laboratório passou a incluir o modo SLT e o ProxyDHCP
([PR 15](https://github.com/mikerock12/slt-disk-manager/actions/runs/43118758070) e
[merge dcd01a9](https://github.com/mikerock12/slt-disk-manager/actions/runs/43123533851)).
Os dois cenários entraram nos portões que aprovam o catálogo de perfis do instalador.

P0–P2 validados em VMs na release beta.6. O laboratório completo passou no
[PR 13](https://github.com/mikerock12/slt-disk-manager/actions/runs/42043355355)
e no [merge 7592dc6](https://github.com/mikerock12/slt-disk-manager/actions/runs/42442085283).
O pacote Network Boot da release inclui o recibo do laboratório e os fingerprints
aprovados para o catálogo do instalador.

| Componente/cenário | Evidência atual | Limite |
|---|---|---|
| Rede RFC1918/CIDR/NIC, import/hash/paths/menu/catálogo de confiança | Suíte Network Boot 88/88 local | Arquivos sintéticos e sockets loopback; stop também com consumidor lento |
| HTTP concorrente/range, TFTP UDP, WRQ/traversal, colisão e start/stop | Mesma suíte | Sem DHCP na LAN do host |
| UI salvar/validar/consentimento/start/stop | Desktop 45/45; renders sintéticos | Não é screenshot de firmware |
| Export ISO → kernel/initrd/SquashFS | Exportador 10/10 e ISO beta.5.1 real extraída | Exportar não prova boot |
| Login serial após senha | Prova interativa QEMU local com technician/live, identidade e sudo; regressões8/8 | Corrige o harness; não substitui os gates PXE completos |
| PXE BIOS e UEFI sem ISO/USB | CI42442085283 aprovou menu, boot e login em SeaBIOS e OVMF | VMs com DHCP externo e Secure Boot desabilitado |
| CSR/mTLS/INVENTORY por PXE | CI42442085283 aprovou o pareamento e o inventário assinado em BIOS e UEFI | Disco sintético RAW somente leitura, com SHA256 preservado |
| API administrativa Network Boot via HTTPS real | Integração local11/11 | Token, consentimento, config inválida, traversal, auditoria e ausência de jobs; nenhum listener boot iniciado |
| ProxyDHCP (beta.7) | CI43118758070 e CI43123533851: com um DHCP sem opções de boot, clientes BIOS (SeaBIOS/iPXE ROM) e UEFI (EDK2) chegaram ao menu; o endereço veio do DHCP do laboratório; o SLT não ofereceu endereço; o DHCP seguiu concedendo antes, durante e depois | VMs. DHCP e SLT ficam no mesmo host do laboratório; em rede real estão em máquinas diferentes. Roteadores e firmwares físicos não testados |
| Modo SLT a partir da partição EFI (beta.7) | Mesmos CIs: plano `REBOOT_RESCUE` assinado e conferido, carregador `ipxe-local.efi` com hash do plano, VM UEFI iniciando por um disco GPT com `\EFI\SLT` (sem ROM PXE, ISO ou USB), pareamento por ticket de uso único, inventário por mTLS e `FORMAT_DISK` GPT/NTFS em disco sintético por ordem do servidor, sem teclado no cliente; reuso do ticket recusado; disco protegido recusado | O cliente Windows é representado pelas mesmas chamadas HTTPS; o passo `bcdedit`/partição EFI do Windows só tem testes com executor falso. Secure Boot desativado. BIOS/Legacy não atendido |
| Falha antes do kernel e durante fetch, retorno Linux local após parar PXE | CI42442085283 aprovou em BIOS e UEFI | Fallback usa fixture Linux live em disco RAW somente leitura |
| Parar HTTP/TFTP após boot preserva agente e Controller | CI42442085283 aprovou novo INVENTORY assinado com processo encerrado e portas liberadas em BIOS e UEFI | Disco sintético somente leitura |
| Mensagem explícita de falha na tela do firmware | CI42442085283 aprovou a observação VGA; observador estrito 17/17 em testes locais | Dois quadros com frase completa, separados por ao menos 0,3 s, com confiança média de ao menos 80 |
| WinPE PXE/Windows sem scratch | P3/P4 pendentes | Fluxo USB legado preservado |
| Secure Boot/hardware físico | Não homologados | Não generalizar VM a NICs/PCs reais |

O CI constrói a ISO e os loaders iPXE, cria namespace de rede dedicada sem rota
para o host, inicia DHCP externo de laboratório e nosso TFTP/HTTP, e testa SeaBIOS
e OVMF. Somente disco sintético RAW8MiB readonly. Registra versões, tempo até menu
e userspace, serial, screenshots e SHA256. O estado com chaves/token não integra
evidências nem release. Cleanup encerra apenas PIDs do próprio harness.

Comandos: `./scripts/test-all.ps1 -Integration`; no Linux, após build/export,
`sudo env PATH="$PATH" python3 rescue/pxe/test-netboot-vm.py --service-dll <dll>
--rescue-profile <dir> --bootstrap-profile <dir> --controller-dll <dll> --output <novo-dir>`.
O runner deve ter dotnet, QEMU, OVMF, iproute2, dnsmasq e Tesseract (`tesseract-ocr`,
usado somente para observar a tela nos testes). Não executar DHCP manualmente
fora do harness. Resultados futuros devem atualizar esta matriz com URLs/SHAs reais.
