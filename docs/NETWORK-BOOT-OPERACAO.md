# Boot pela rede — operação de laboratório

O módulo inicia desligado. Esta entrega adiciona EXTERNAL_DHCP, TFTP read-only,
menu iPXE e perfil SLT Rescue x64 BIOS/UEFI. Consulte a matriz de evidências antes
de usar: Secure Boot, hardware físico, WinPE PXE e instalação sem scratch não estão
homologados. O boot não aprova nenhum job nem autoriza gravação.

1. Use uma Ethernet física numa rede de bancada isolada, IP fixo RFC1918 e CIDR
   explícito. O painel recusa VPN, Wi-Fi e adaptadores virtuais por padrão.
2. Baixe o pacote `SLT-Netboot-<versão>-amd64.zip` da mesma release, confira seu
   SHA256 e extraia numa pasta local. Ele é publicado somente depois do teste PXE
   do CI. Não importe ISO comum como perfil nem renomeie install.wim para boot.wim.
3. Na aba **Boot pela rede**, importe os dois manifestos: `rescue-netboot` e
   `ipxe-bootstrap`. A cópia gerenciada fica separada das mídias de implantação
   no estado privado do Controller. Importação confere tamanhos/SHA256; um ID é
   imutável. Para substituir arquivos, importe um novo ID versionado.
4. Selecione Ethernet, IP, CIDR e porta HTTP (8088 padrão), salve e valide.
   Mantenha exatamente um perfil iPXE e pelo menos um Rescue habilitados.
5. Configure explicitamente seu DHCP externo: IP normal para o cliente,
   `next-server=<IP do SLT>`, BIOS → `undionly.kpxe`, UEFI x64 opção93=7/9 →
   `ipxe.efi`. Os loaders SLT incluem script que busca `autoexec.ipxe` por TFTP;
   este encadeia o menu HTTP na porta configurada, evitando baixar o loader em
   laço. iPXE genérico pode precisar de regra user-class iPXE → menu HTTP.
   SLT não configura o roteador, não fornece leases e não implementa ProxyDHCP
   nesta entrega. Relay/VLAN/routers domésticos sem bootfile por arquitetura
   podem exigir DHCP externo diferente. Não instale DHCP na rede de produção.
6. Firewall é uma ação elevada separada do técnico. No PowerShell como
   Administrador execute `install-server/Configure-NetworkBootFirewall.ps1`
   com `-Executable <caminho/SLT.NetworkBoot.Service.exe> -Bind <IPv4>
   -AllowedCidr <rede/prefixo> -InterfaceAlias <Ethernet> -HttpPort 8088`.
   As regras são privadas, restritas ao programa, interface, endereço e CIDR.
   TCP permite só a porta HTTP; TFTP precisa de UDP69 e portas efêmeras de
   transferência, restritas ao mesmo programa. Não são criadas regras DHCP67/4011.
7. Confirme a rede isolada e clique **Ativar boot**. RUNNING só aparece após
   confirmação de bind dos dois listeners pelo processo separado.
8. Escolha Network Boot no cliente e SLT Rescue no menu. O padrão após 15s é
   sair para o firmware; não promete boot automático do disco em todo firmware.
   No Rescue use `sudo slt-start`, configure rede, digite URL HTTPS e confira o
   pin pessoalmente no servidor. Aprovação CSR/código/mTLS continua necessária;
   diagnóstico usa modo leitura. Não há chave global, senha admin ou token na URL.
9. **Desativar** encerra o processo/listeners. Não cancela os jobs do Controller
   nem agentes já carregados. Para reverter o firewall execute o mesmo script
   com `-Remove` e os parâmetros obrigatórios. Desinstalação do servidor deve ser
   precedida por desativação e remoção dessas regras opcionais. DHCP externo
   continua sob responsabilidade do operador; remova suas opções PXE se necessário.

O Controller gerencia o processo de boot por handles stdin/stdout herdados, inclusive
quando instalado como Windows Service. Não existe serviço SCM de boot auto-start
independente; isso mantém a ação local de ativação e o desligamento após reinício.
Pacote portátil e instalador Servidor incluem runtime do processo; Cliente não recebe
listeners. Start/stop não requer mudar IP, boot order ou privilégio de discos.

Sem internet após baixar os pacotes. Planeje ao menos 4 GiB de RAM no cliente de
laboratório: `fetch=` copia o SquashFS à RAM; 2 GiB ainda não validados. Não há
integridade autenticada no pré-boot HTTP/TFTP: hashes protegem o catálogo de corrupção,
mas não substituem cadeia de boot assinada contra atacante ativo na bancada. O pin
TLS digitado no Rescue autentica o Controller, não o firmware anterior.

Diagnóstico: confira link/IP e DHCP, arquitetura do bootfile, next-server, firewall
privado e conflito UDP69/TCP8088. HTTP público tem apenas `/boot/v1/health`, menu e
artefatos por ID; respostas 403 fora do CIDR e 404 para paths desconhecidos são
esperadas. Arquivos grandes são lidos por stream, com limite de concorrência e
timeouts; TFTP permite só bootstrap pequeno e recusa WRQ/traversal. API administrativa
continua somente localhost:7443 com token e TLS pinado. Falha não reinicia PXE sozinho.

Origem de terceiros: [iPXE](https://ipxe.org/howto/chainloading), build fixado por
commit em `scripts/build-ipxe-bootstrap.sh`, código-fonte/licença no pacote.
[Debian live-boot](https://manpages.debian.org/trixie/live-boot-doc/live-boot.7.en.html)
fornece o parâmetro fetch. ISO USB anterior e seus fluxos continuam disponíveis.
