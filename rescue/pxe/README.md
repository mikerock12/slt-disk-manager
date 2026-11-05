# SLT Rescue pela rede

O perfil usa o mesmo kernel, initramfs e SquashFS da ISO SLT Rescue. O exportador
não inicia PXE, não monta mídia e não acessa dispositivos de bloco. O menu e os
listeners pertencem ao serviço Network Boot, administrado pelo Controller.

Em Linux, instale `python3` e `xorriso` e execute depois do build da ISO:

```sh
bash rescue/pxe/build-rescue-netboot.sh \
  artifacts/rescue-iso/live-image-amd64.hybrid.iso \
  artifacts/rescue-netboot \
  --version "$(tr -d '\r\n' < VERSION)" --source-commit "$GITHUB_SHA"
python3 rescue/pxe/test-export-rescue-netboot.py
```

`--source-commit` identifica a fonte que construiu a ISO, não necessariamente o
HEAD do exportador. Fora do CI, informe o commit registrado na construção dessa
ISO. O diretório de saída precisa ser novo. A publicação só acontece depois de
validar os formatos, par kernel/initramfs, cópias, hashes e imutabilidade da ISO
durante a exportação. Não existe inferência de homologação a partir da extensão.

No Windows, use `python rescue/pxe/export-rescue-netboot.py` com os mesmos argumentos
e acrescente `--extractor "C:/Program Files/7-Zip/7z.exe"`.

O resultado contém `vmlinuz`, `initrd.img`, `filesystem.squashfs`,
`netboot-manifest.json` e `SHA256SUMS.txt`. O manifesto registra a versão, commit
informado, hash da ISO de origem e tamanho/SHA256 de cada artefato. O estado
inicial é sempre `NETWORK_BOOT_DEV`; exportar arquivos não comprova boot. O
Controller importa o diretório em seu catálogo de boot independente das mídias
de implantação. O perfil declara X64, BIOS/UEFI e Secure Boot não suportado.

A receita é Debian 13/trixie. Kernel, initramfs e SquashFS devem vir da ISO SLT
construída pela receita deste projeto; não use este exportador como importador
genérico de qualquer distribuição. Os testes de formato não demonstram origem,
assinatura ou compatibilidade. Confira o hash publicado da ISO antes de exportar.

O parâmetro `fetch=http://IPv4:porta/...` do
[live-boot trixie](https://manpages.debian.org/trixie/live-boot-doc/live-boot.7.en.html)
copia o SquashFS para RAM; use IPv4 literal. DHCP no initramfs é o padrão quando
`ip` não é especificado. O perfil inicial recomenda 4 GiB de RAM, com os valores
efetivamente testados registrados na matriz de homologação. Acesso ao menu não
estabelece identidade: no console local execute `sudo slt-start`, confira URL e
pin do Controller e aprove o código de pareamento no painel. A opção padrão do
assistente mantém gravação física desabilitada. Nenhum agente ou job é iniciado
automaticamente pelo simples boot PXE.

Os artefatos públicos não contêm token administrativo, chave privada, certificado
de cliente nem senha da bancada. HTTP/TFTP pré-boot não oferecem autenticidade
criptográfica da rede. Os hashes detectam alteração nos arquivos importados; não
substituem uma cadeia de boot assinada ou proteção da LAN de laboratório.

Para reverter, pare o serviço Network Boot e selecione a inicialização normal no
firmware do cliente. A ISO/USB anterior continua disponível e não é alterada pela
exportação. Este fluxo não inclui WinPE, instalação Windows sem scratch, suporte
universal a ISOs, Secure Boot ou homologação em hardware físico.

O harness `test-netboot-vm.py` cria uma namespace Linux nova com somente
bridge/tap de bancada, DHCP externo de laboratório e os listeners reais SLT.
Ele recusa executar DHCP na namespace original, não adiciona rota padrão, não
anexa ISO/USB e usa somente um arquivo RAW de 8 MiB em modo somente leitura.
Exemplo no runner Linux dedicado, depois de compilar serviço e Controller:

```sh
sudo env PATH="$PATH" python3 rescue/pxe/test-netboot-vm.py \
  --service-dll src/network-boot/SLT.NetworkBoot.Service/bin/Release/net10.0/SLT.NetworkBoot.Service.dll \
  --controller-dll src/server/SLT.Controller.Host/bin/Release/net10.0/SLT.Controller.Host.dll \
  --rescue-profile artifacts/rescue-netboot \
  --bootstrap-profile artifacts/ipxe-bootstrap \
  --local-system-image artifacts/rescue-iso/live-image-amd64.hybrid.iso \
  --output artifacts/pxe-lab
```

Dependências: `unshare`, `iproute2`, `dnsmasq`, `qemu-system-x86`, `ovmf`, Python
e .NET. O perfil iPXE deve vir do build SLT com console serial e script embarcado
que faz DHCP e encadeia `tftp://${next-server}/autoexec.ipxe`; isso obriga o teste
a atravessar o TFTP SLT também quando a ROM BIOS do QEMU já é iPXE. O DHCP do
harness escolhe `undionly.kpxe`/`ipxe.efi` pela arquitetura e não emite informações
na LAN do host. A namespace é descartada com o processo; o cleanup encerra apenas
os processos que o harness criou.

O gate completo exige os dois firmwares e `--controller-dll`: console local,
`slt-start` no modo padrão, código de pareamento conferido no serial, certificado
individual/mTLS, job INVENTORY assinado e hash RAW intacto. SMART é consultado
no disco virtual; atributos físicos não são esperados desse dispositivo. O
recibo distingue boot somente de aceite MVP. Logs, telas e hashes são evidências
na saída; chaves e token do Controller ficam em pasta temporária separada e nunca
são copiados para a saída pública. Execute o harness para produzir evidências;
sua presença no código não equivale a teste aprovado.

Para cada firmware, o harness também inicia uma VM nova, carrega somente o menu,
interrompe o serviço SLT e verifica liberação de HTTP/TFTP. Selecionar Rescue deve
produzir erro explícito de boot, sem alcançar o marcador de userspace e mantendo
o hash do disco de dados. Isso cobre perda do servidor **antes** de kernel e
filesystem.

Outro cenário limita a NIC virtual a 16 Mbit/s por `tc` na namespace isolada e
interrompe o serviço durante o download do SquashFS. O teste espera o evento
`HTTP_BEGIN` do rootfs no log de uma instância nova do serviço e mede mais de
64 KiB de tráfego nessa transferência antes da parada. O limite de banda impede
que o rootfs completo seja entregue nesse intervalo. O aceite exige erro de
download/initramfs, ausência de userspace Rescue e hash do disco de dados intacto;
o recibo registra os bytes de rede observados, incluindo framing TCP, e não os
confunde com bytes úteis exatos de SquashFS. O shaping é removido ao fim dessa
VM. Esse cenário não cobre perda da rede durante operações destrutivas.

Com `--local-system-image`, o SHA da ISO híbrida deve coincidir com a origem do
perfil exportado. O harness copia seus bytes para um arquivo temporário `.raw`,
conectado exclusivamente como disco virtio somente leitura. Após parar o boot
SLT, mantém NIC e DHCP externo e configura ordem rede → disco. O aceite exige
Linux real iniciado, login local, ausência de `fetch=` no cmdline e origem do
Live em `/dev/vdb` ou partição desse disco, além dos hashes intactos. Isso comprova
retorno a uma **fixture Linux local baseada no Rescue híbrido**; não comprova
retorno a Windows instalado, a um SO persistente de cliente, nem hardware real.
Não há CD-ROM ou USB nessa VM. O recibo registra separadamente os gates de
boot/inventário, falha anterior ao filesystem e fallback Linux local.
