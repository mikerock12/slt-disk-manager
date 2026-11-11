# Network Boot P0–P2 — entrega 0.1.0-beta.6

A versão 0.1.0-beta.6 foi publicada em 08/11/2026 a partir do merge `7592dc6`.
O laboratório aprovou o Rescue por PXE em SeaBIOS e OVMF, sem ISO ou USB na
VM, com DHCP externo, pareamento CSR/mTLS e inventário assinado. O inventário
continuou disponível após parar HTTP/TFTP. Os testes também comprovaram falha
visível antes do kernel, interrupção durante o download do filesystem e boot
local após desativar o PXE, usando disco sintético somente leitura.

- [PR 13](https://github.com/mikerock12/slt-disk-manager/pull/13), [CI do aplicativo](https://github.com/mikerock12/slt-disk-manager/actions/runs/42442085269) e [CI de ISO e PXE](https://github.com/mikerock12/slt-disk-manager/actions/runs/42442085283).
- [Release beta.6](https://github.com/mikerock12/slt-disk-manager/releases/tag/v0.1.0-beta.6). Os 11 arquivos passaram na conferência de tamanho e SHA256. Os ZIPs portátil e Network Boot passaram na verificação CRC e os dois instaladores passaram no smoke do CI.
- [Portal](https://softwares.smellsliketech.com.br/apps/disk-manager/) atualizado em 10/11 pelo [PR 55](https://github.com/mikerock12/softwares-smells-like-tech/pull/55). A validação local passou 60 testes, build e dry-run. Em produção, as rotas de download apontaram para os dois EXEs oficiais e retornaram os tamanhos e SHA256 esperados.

O serviço fica desligado por padrão. Consulte o [guia operacional](NETWORK-BOOT-OPERACAO.md)
para preparar o DHCP externo e ativar o serviço em rede isolada. ProxyDHCP,
WinPE por PXE, implantação Windows sem scratch, Secure Boot e homologação em
hardware físico seguem pendentes.

## Checkpoint de 05/11/2026

05/11/2026. Primeira entrega incremental do mapa: processo opt-in isolado,
DHCP externo sem leases, TFTP bootstrap, HTTP de artefatos, catálogo de boot
separado e painel WPF administrativo local. USB/ISO, mTLS, clone, arquivos e
confirmações de disco preservados.

Perfis importados não podem se declarar homologados: somente fingerprints
aprovados pelo laboratório e incluídos no catálogo instalado da mesma release
recebem NETWORK_BOOT_LAB. Perfis DEV bloqueiam o início pelo painel.

Validação local: Network Boot 88/88; API administrativa HTTPS 11/11; Desktop 45/45;
exportador 10/10; diagnóstico/login serial 8/8; observador de erro visual 17/17.
A prova interativa QEMU confirmou
usuário, sudo, saída real do comando e disco sintético readonly intacto.

O CI anterior passou Windows/Linux, auditoria, pacote e smoke dos instaladores.
CI41964719717 passou BIOS completo e o positivo UEFI, incluindo um novo INVENTORY
assinado após encerrar HTTP/TFTP. O negativo UEFI reprovou pela ausência de mensagem
no serial; os dados brutos mostram perda de caracteres no console EFI. Diagnóstico
isolado confirmou a mensagem na tela VGA. O novo observador exige a frase completa
no serial ou em dois quadros VGA separados por pelo menos 0,3 s, com confiança OCR
média de pelo menos 80 e evidências preservadas. Somente a ambiguidade óptica
`;`/`:` no separador fixo da frase é normalizada, mantendo o reconhecimento bruto.
O replay dos quadros reais qualificou as três tentativas (88,24%); os resultados
originais reprovados não foram alterados. O gate completo será repetido no PR13.
