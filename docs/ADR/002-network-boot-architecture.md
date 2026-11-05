# ADR 002 — Network Boot isolado e opt-in

Data: 2026-11-04. Base inspecionada: e87fb04, igual à base do mapa.

O Controller continua HTTPS/mTLS e administração somente loopback. Um processo
`SLT.NetworkBoot.Service` separado recebe uma única configuração por stdin herdado,
responde por stdout herdado e termina ao receber EOF. Esses handles privados são o
canal local; não existe porta administrativa adicional nem token administrativo no
processo de boot. O Controller encerra o filho ao parar ou sair e nunca religa PXE
depois de reiniciar. Falhas de bind revertem todos os listeners do filho.

Primeiro modo: EXTERNAL_DHCP, sem emissão de leases ou alteração do roteador.
ProxyDHCP fica bloqueado até implementação e teste de protocolo próprios. O DHCP
externo escolhe undionly.kpxe (BIOS), ipxe.efi (UEFI x64), e menu HTTP para user-class
iPXE; autoexec.ipxe oferece proteção adicional contra loop. TFTP serve somente os
dois loaders e autoexec; HTTP somente menu e artefatos publicados por ID.

Importação local copia arquivos de um manifesto fechado para armazenamento separado,
verifica tamanho/SHA256, recusa reparse points e parâmetros arbitrários. Perfis Rescue
são DEV até correspondência exata com o catálogo instalado pelo pipeline, que só
é produzido após os gates BIOS/UEFI, falhas de rede e fallback local. O manifesto
importado não pode autodeclarar homologação. O catálogo exige a mesma proteção
do diretório dos executáveis; alterá-lo requer acesso equivalente a substituir
o próprio programa. Perfis desconhecidos não podem iniciar pelo painel.
HTTP/TFTP pré-boot não autenticam a máquina e não fornecem integridade contra um
atacante ativo da LAN: SHA256 detecta corrupção no armazenamento; Secure Boot não
está homologado. Só rede de bancada explicitamente escolhida. Pareamento e pin TLS
continuam manuais no console Rescue; nenhuma credencial vai ao menu.

Não alteramos implantação Windows, scratch, USB, ISO ou jobs de disco. A primeira
entrega segue P0–P2; P3–P5 exigem evidências separadas. Reversão: parar boot, remover
regras de firewall explicitamente instaladas e manter DHCP/boot local do operador.
