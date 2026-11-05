# Network Boot — evidências por fase

Checkpoint 2026-11-05, implementação P0–P2 em validação. Não é aceite P3–P5.

| Componente/cenário | Evidência atual | Limite |
|---|---|---|
| Rede RFC1918/CIDR/NIC, import/hash/paths/menu/catálogo de confiança | Suíte Network Boot 88/88 local | Arquivos sintéticos e sockets loopback; stop também com consumidor lento |
| HTTP concorrente/range, TFTP UDP, WRQ/traversal, colisão e start/stop | Mesma suíte | Sem DHCP na LAN do host |
| UI salvar/validar/consentimento/start/stop | Desktop 45/45; renders sintéticos | Não é screenshot de firmware |
| Export ISO → kernel/initrd/SquashFS | Exportador 10/10 e ISO beta.5.1 real extraída | Exportar não prova boot |
| Login serial após senha | Prova interativa QEMU local com technician/live, identidade e sudo; regressões8/8 | Corrige o harness; não substitui os gates PXE completos |
| PXE BIOS sem ISO/USB | CI41946920318 alcançou Linux ready, falhou no login automatizado | Gate reprovado; ainda não libera publicação |
| PXE UEFI sem ISO/USB | Harness `rescue/pxe/test-netboot-vm.py` preparado | Execução real pendente após corrigir login |
| CSR/mTLS/INVENTORY por PXE | Incluído no harness com disco RAW readonly e hashes antes/depois | Não alegar aprovado até recibo |
| API administrativa Network Boot via HTTPS real | Integração local11/11 | Token, consentimento, config inválida, traversal, auditoria e ausência de jobs; nenhum listener boot iniciado |
| ProxyDHCP | Bloqueado | Somente DHCP externo implementado |
| Falha antes do kernel e durante fetch, retorno Linux local após parar PXE | Cenários adicionados ao harness, execução pendente | Fallback usa fixture Linux live em disco RAW readonly; não comprova Windows instalado |
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
O runner deve ter dotnet, QEMU, OVMF, iproute2 e dnsmasq. Não executar DHCP manualmente
fora do harness. Resultados futuros devem atualizar esta matriz com URLs/SHAs reais.
