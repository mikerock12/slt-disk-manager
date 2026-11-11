# SLT Disk Manager — Mapa Mestre de Implementação do Network Boot (PXE/iPXE)

> **Tipo:** especificação evolutiva, roteiro executável e checklist de aceite para agente de programação.  
> **Projeto:** Smells Like Tech Informática / SLT Disk Manager  
> **Repositório:** https://github.com/mikerock12/slt-disk-manager  
> **Branch-base analisada:** `main` — commit `e87fb042648c2d78da8c17579474122d6b2d70bd` (30/10/2026)  
> **Documento criado em:** 02/11/2026  
> **Status em 10/11/2026:** P0–P2 publicados na beta.6, com Rescue por PXE validado em VMs BIOS e UEFI com DHCP externo. Consulte as [evidências e os limites](docs/NETWORK-BOOT-HOMOLOGACAO.md). ProxyDHCP, P3–P5 e hardware físico seguem pendentes.
> **Regra de prevalência:** antes de codificar, ler o HEAD atual, `AGENTS.md`, `contexto.md`, `docs/STATUS.md`, a especificação técnica e os contratos vigentes. O código do HEAD prevalece sobre caminhos/contratos exemplificativos deste mapa.

---

## 0. Resultado desejado (critério de sucesso em linguagem de produto)

No **SLT Disk Manager Server**, o técnico ativa **Servidor de Boot pela Rede**, seleciona a interface Ethernet e os ambientes autorizados. Um ou mais computadores na mesma rede de bancada, sem pendrive e sem sistema operacional funcional, podem escolher **Network Boot / PXE** no firmware e visualizar o menu **SLT Network Boot**:

1. **SLT Rescue Linux — Diagnóstico e manutenção** (SMART/NVMe, inventário, recuperação, clonagem e fluxos já autorizados pelo servidor).
2. **Instalar Windows** por WinPE carregado da rede, com catálogo de imagens importadas e as proteções atuais do Controller e do agente.
3. **Instalar Linux** através dos perfis de distribuição efetivamente suportados e homologados.
4. **Ferramentas adicionais de recuperação**, mediante perfis testados e licença compatível.
5. **Inicializar disco local / sair**, sem modificar os discos.

O cliente inicializa o ambiente escolhido, obtém rede, solicita pareamento com o Controller e aparece no painel. **Inicializar por PXE não autoriza apagar, formatar, clonar ou restaurar discos.** As autorizações destrutivas continuam exigindo plano tipado e assinado, verificação de identidade/fingerprint, confirmação no servidor **e** confirmação local no cliente.

**Definição objetiva de MVP:** ativar/desativar o serviço no painel; iniciar **SLT Rescue Linux pela rede em BIOS e UEFI sem Secure Boot** em laboratório isolado; parear o agente e coletar inventário/SMART sem tocar em discos; reiniciar e carregar o sistema local após desativar o PXE. Instalação Windows por PXE é uma **fase posterior**, nunca incluída automaticamente no aceite do MVP.

---

## 1. Levantamento do repositório — REAL versus PLANEJADO

### 1.1 Funcionalidades existentes que devem ser preservadas

- `src/server/SLT.Controller.Host/`: Controller ASP.NET Core/.NET 10, persistência SQLite, HTTPS com pin SHA-256, mTLS, listas CIDR, API administrativa acessível somente por loopback, planos, auditoria e catálogo de imagens/mídias.
- `src/desktop/SLT.Desktop.Wpf/`: painel WPF com clientes, dispositivos, arquivos, clonagem, instalação e tarefas.
- `src/rescue-agent/`: agente Go Linux Rescue e agente Go WinPE, inventário, autenticação, execução e operações de instalação.
- `rescue/scripts/build-debian-live.sh`: ISO SLT Rescue baseada em Debian Live, `live-boot`, `live-config`, kernel AMD64 e ferramentas; já testada como ISO BIOS/UEFI em QEMU. **Isso NÃO equivale a inicialização PXE homologada.**
- `rescue/windows-pe/New-WinPEKit.ps1`: criação de kit WinPE integrado à mídia local oficial do operador. O fluxo atual **depende de USB/mídia local**, além de exigir armazenamento scratch NTFS físico fora do disco de destino.
- `src/server/SLT.Controller.Host/MediaRepository.cs`: importação de `.iso`, `.wim`, `.esd` para **implantação**. No Windows extrai `sources/install.wim` ou `install.esd`; para Ubuntu/Xubuntu pode guardar a ISO inteira. **Não existe hoje um catálogo independente de arquivos de boot PXE nem extração de `boot.wim` para Network Boot.**
- `docs/STATUS.md` e README: **PXE/Rescue local** e **reboot automático Windows→Rescue** aparecem como pendentes; teste físico e Secure Boot também não estão homologados.
- CI: `.github/workflows/ci.yml` e `.github/workflows/rescue-iso.yml`; testes: `tests/SLT.Controller.Tests`, `src/desktop/tests/SLT.Desktop.SmokeTests`, testes Go e `tests/integration/`.

### 1.2 Achados específicos que afetam o design

1. `src/server/SLT.Controller.Host/Program.cs` usa HTTPS na porta padrão `7443`, com `/api/` restrita a loopback. **Não expor a API administrativa ao cliente PXE.**
2. `src/desktop/SLT.Desktop.Wpf/ControllerClient.cs` exige HTTPS + token local + certificado pinado. O painel deve continuar acessando apenas localhost.
3. `src/rescue-agent/internal/agent/winpe_common.go`: o agente faz download integral do WIM/ESD para staging em scratch e chama DISM com caminho de arquivo local; usa `ScratchDir` na montagem e valida a mídia.
4. `src/rescue-agent/internal/agent/winpe_windows.go`: scratch em `X:` (RAM) é rejeitado para o método atual; precisa de disco NTFS saudável separado do alvo e espaço para imagem + 2 GiB. **Para Windows verdadeiramente sem USB e sem disco auxiliar, desenvolver caminho de mídia por rede**, sem remover as proteções do modo legado.
5. `rescue/scripts/test-iso-boot.sh` testa ISO local com QEMU e **nenhuma NIC**. Criar outro harness: testes PXE com NIC virtual e rede isolada.
6. No boot de rede, a placa/firmware inicia sem identidade mTLS do agente. Autenticação forte só começa **após** carregado o ambiente SLT e executado o pareamento do agente.
7. Não pressupor que qualquer ISO existente possa ser carregada diretamente por PXE. Um **perfil de boot** transforma uma mídia validada em kernel/initramfs, WinPE/BCD/WIM ou outra cadeia adequada.

### 1.3 Matriz atual e alvo

| Recurso | Atual | Alvo | Fase |
|---|---|---|---|
| Controle PXE na UI | Ausente | Iniciar/parar, interface de rede, status, logs | P1 |
| DHCP coexistente | Ausente | ProxyDHCP ou integração explícita com DHCP, sem concessão de IP duplicada | P1 |
| TFTP bootstrap | Ausente | Entrega de bootloader pequeno, com diretório restrito | P1 |
| HTTP arquivos de boot | Ausente | Arquivos assinados/hash verificados, streaming e limites | P1 |
| iPXE menu | Ausente | Menu SLT e proteção contra loop | P1 |
| SLT Rescue PXE | ISO local somente | Boot Linux pela rede com agente Go | P2 |
| WinPE PXE | USB local | Boot `wimboot`/WinPE + agente SLT | P3 |
| Windows sem disco de scratch | Não | WIM remoto + scratch operacional seguro, dimensionado e testado | P3/P4 |
| Linux installer multissistema | Ubuntu/Xubuntu em fluxo controlado | Perfis suportados por distribuição/versão/arquitetura | P4 |
| Secure Boot | Não homologado | Cadeia **assinada e testada**, sem desativação automática | P5 |
| Wake-on-LAN/reboot remoto | Não integra PXE | Opcional, somente dispositivos autorizados e hardware compatível | P5 |

---

## 2. Restrições inegociáveis

- **Sem mudanças destrutivas de rede:** não substituir o DHCP do roteador por padrão; nunca habilitar servidor DHCP tradicional automaticamente, nem configurar Options 66/67 sem consentimento explícito e diagnóstico da topologia.
- **PXE desligado por padrão**; inicia mediante ação administrativa local. Ao parar, liberar sockets/portas e interromper distribuição de novos boots sem interromper jobs já autorizados do Controller.
- **Somente rede local de bancada**, em interface selecionada, com IPv4 privado e CIDR explícito; rejeitar loopback como interface PXE, WAN, VPN, adaptadores virtuais ou redes não confiáveis por padrão, com decisão administrativa auditada quando apropriado.
- **Não criar shell remoto genérico** nem aceitar comandos arbitrários através de DHCP, iPXE, scripts de boot ou Controller; manter jobs tipados e capacidades existentes.
- **Não incluir Windows, WinPE, ADK, ISO proprietária, firmware ou licenças Microsoft** em Git, release ou backups públicos. Mídias são importadas localmente pelo técnico com suas próprias licenças.
- **Não publicar chaves privadas, tokens, certificados, senhas de cliente, dados reais ou arquivos de backup sensíveis** no GitHub, em bundles públicos ou URLs de boot.
- **Nunca confundir descoberta por MAC com autenticação.** MAC e UUID SMBIOS podem ser forjados; MAC allowlist reduz exposição, mas **não** substitui mTLS/pareamento, autorização no servidor e confirmação no console do alvo.
- **Nenhuma limpeza/reparticionamento automático** em consequência de boot PXE. Testes automatizados não devem conectar discos do host ou de clientes.
- Preservar os meios existentes **USB/ISO**; o boot pela rede é uma opção adicional e reversível.
- Tratar Secure Boot como suporte **por cadeia validada e por perfil**, nunca prometer disponibilidade universal ou orientar desativação obrigatória.
- Todos os caminhos e parâmetros de boot devem ser gerados pelo servidor; sem interpolação insegura de valores do cliente em paths, argumentos de shell, URLs, BCD ou scripts.

---

## 3. Arquitetura proposta

```text
                                      Rede Ethernet de bancada
                                    ┌─────────────────────────┐
                                    │ Roteador / DHCP existente│
                                    │ IP, gateway, DNS         │
                                    └───────────┬─────────────┘
                                                │
┌───────────────────────────────────┐           │           ┌───────────────────────────┐
│ WINDOWS SERVER / SLT CONTROLLER   │           ├───────────┤ PC A: BIOS / PXE Ethernet │
│                                   │           │           └───────────────────────────┘
│ WPF Desktop (localhost admin)     │           │           ┌───────────────────────────┐
│           │                       │           ├───────────┤ PC B: UEFI / PXE Ethernet │
│           ▼                       │           │           └───────────────────────────┘
│ Controller HTTPS :7443            │           │           ┌───────────────────────────┐
│  - catálogos / certificados       │           └───────────┤ PC C: UEFI / WinPE        │
│  - CSR/mTLS / jobs assinados      │                       └───────────────────────────┘
│           │ localhost             │
│           ▼                       │
│ SLT NetworkBoot.Service           │
│  - ProxyDHCP/controlador de PXE   │ UDP 67/4011 conforme modo/topologia
│  - TFTP mínimo de bootstrap       │ UDP 69 (+ transferência TFTP)
│  - HTTP Boot Assets               │ TCP configurável, ex.: 8088
│  - scripts iPXE / perfis          │
│  - logs, saúde e rate limits      │
│           │                       │
│ Boot Artifact Store (separado)    │
│  - binários iPXE por arquitetura  │
│  - kernel/initrd/SquashFS Rescue  │
│  - boot.wim/BCD/boot.sdi (local)   │
│  - manifestos e hashes SHA-256    │
└───────────────────────────────────┘
```

### 3.1 Decisão recomendada para o Windows Server

Criar **`SLT.NetworkBoot.Service`** como processo/Windows Service próprio em C#/.NET, administrado pelo Controller através de um **canal local autenticado**. Razões: isolamento de portas de rede e permissões, início/parada sob demanda, diagnósticos de conflito, falha do PXE sem derrubar jobs de disco e testes independentes.

A API administrativa fica no Controller (somente loopback); o processo de boot não recebe token administrativo permanente de agentes. A interface HTTP pública serve **somente artefatos de boot explicitamente publicados**, em listener separado do `:7443`. Preferir stream por arquivo com read-only handle e hashes/manifesto; não compartilhar pasta interna genérica como webroot.

Alternativa aceitável: serviço hospedado dentro do Controller **somente se** teste de isolamento, desligamento, privilégio mínimo, vazamento de portas e regressão demonstrar equivalência. Documentar a decisão em ADR antes de codificar.

### 3.2 Componentes/projetos novos propostos

```text
src/
  network-boot/
    SLT.NetworkBoot.Core/
      NetworkBootOptions.cs
      BootProfile.cs
      BootArtifact.cs
      BootManifest.cs
      NetworkTopologyValidator.cs
      BootScriptRenderer.cs
      BootAccessPolicy.cs
      BootAuditModels.cs
    SLT.NetworkBoot.Service/
      Program.cs
      NetworkBootWorker.cs
      PxeProxyDhcpServer.cs
      TftpBootstrapServer.cs
      BootHttpServer.cs
      NetworkBootHealth.cs
      SLT.NetworkBoot.Service.csproj
    tests/
      SLT.NetworkBoot.Tests/
        Program.cs
        ParserTests.cs
        ProtocolTests.cs
        PathSecurityTests.cs
        ManifestTests.cs
        LifecycleTests.cs
src/server/SLT.Controller.Host/
  NetworkBootEndpoints.cs               # Somente localhost/administrativo
  NetworkBootManagement.cs              # Interface de controle com serviço
src/desktop/SLT.Desktop.Wpf/
  NetworkBootPanel.xaml
  NetworkBootPanel.xaml.cs
  NetworkBootModels.cs
rescue/
  pxe/
    README.md
    build-rescue-netboot.sh
    generate-ipxe-menu.sh               # Opcional; preferir geração no Controller
    manifests/
rescue/windows-pe/
  New-WinPENetworkKit.ps1                # NOVO: não substituir New-WinPEKit.ps1
  README-NetworkBoot.md
tests/integration/
  pxe-lab.py                             # Em VMs com LAN completamente isolada
  pxe-synthetic-protocol.py
  winpe-netboot-vm.py                    # Etapa posterior
.github/workflows/
  network-boot-ci.yml                    # Pode integrar ao ci.yml existente

docs/
  ADR/002-network-boot-architecture.md
  NETWORK-BOOT-OPERACAO.md
  NETWORK-BOOT-HOMOLOGACAO.md
```

**Nota:** nomes são sugestão, não contratos já existentes. Antes de criar, verificar namespace, organização das soluções e convenções do HEAD. Adicionar os novos projetos à `SLT.DiskManager.slnx`, mantendo o build existente.

### 3.3 Modelo de dados proposto

```csharp
// Pseudocontratos — adaptar após revisão de Models.cs e Store.cs
record NetworkBootConfig(
    int SchemaVersion,
    bool Enabled,
    string BindIPv4,
    string AllowedCidr,
    string Mode,              // PROXY_DHCP ou EXTERNAL_DHCP
    int HttpPort,
    string BootStorePath,
    int MaxConcurrentTransfers,
    bool RequireApprovedMacForMenu);

record BootProfile(
    string Id,
    string DisplayName,
    string Kind,              // RESCUE_LINUX | WINPE | LINUX_INSTALLER | LOCAL_BOOT
    string Architecture,      // X64_BIOS | X64_UEFI | ARM64_UEFI (futuro)
    string FirmwareSupport,
    string SecureBootStatus,  // UNSUPPORTED | TESTING | VERIFIED
    string ArtifactManifestId,
    bool Enabled,
    string ValidationStatus);

record BootArtifact(
    string Id,
    string RelativePath,
    long SizeBytes,
    string Sha256,
    string Source,
    string State);            // IMPORTING | AVAILABLE | REJECTED | INVALID

record BootSession(
    string Id,
    string ClientMac,
    string? Architecture,
    string? Firmware,
    string? IpAddress,
    string Event,
    DateTimeOffset TimestampUtc);
```

Não misturar **`DeploymentMedia`** com **`BootArtifact`**: `install.wim` é a imagem do SO a aplicar; `boot.wim` é a imagem que inicializa WinPE. Os dois têm propósito, validação, acesso e ciclo de vida diferentes.

### 3.4 Endpoints administrativos propostos (somente `127.0.0.1:7443`)

| Método | Endpoint | Descrição |
|---|---|---|
| GET | `/api/v1/network-boot/status` | Estado, interface/IP, modo, portas, uptime, erros de bind, sessões e métricas |
| GET | `/api/v1/network-boot/interfaces` | NICs elegíveis, sub-redes, gateway, conflitos potenciais |
| PUT | `/api/v1/network-boot/config` | Salva configuração validada; exige ação administrativa |
| POST | `/api/v1/network-boot/validate` | Pré-verifica NIC, CIDR, portas, arquivos, espaço e conflito DHCP |
| POST | `/api/v1/network-boot/start` | Inicia explicitamente e audita |
| POST | `/api/v1/network-boot/stop` | Desativa nova admissão e audita |
| GET | `/api/v1/network-boot/profiles` | Lista perfis e compatibilidade verificada |
| POST | `/api/v1/network-boot/profiles/import` | Importação local com tipo/versão e manifesto |
| PUT | `/api/v1/network-boot/profiles/{id}` | Habilita/desabilita e revalida um perfil |
| GET | `/api/v1/network-boot/events` | Boot sessions/eventos, sem dados sensíveis |

Os paths públicos do serviço de boot são somente, por exemplo, `/boot/v1/menu.ipxe`, `/boot/v1/assets/{artifactId}` e `/boot/v1/health`. **Não servir `/api/v1` nem `/agent/v1` nessa porta.** As operações dos agentes continuam no HTTPS `:7443` com seus requisitos de certificado.

---

## 4. Protocolos e coexistência com o roteador

### 4.1 Modos suportados

**Modo A — ProxyDHCP (`PROXY_DHCP`, recomendado no laboratório com roteador simples):** DHCP existente fornece IP; SLT responde com informações PXE sem emitir concessão de IP. Detectar arquitetura PXE (DHCP opção 93) e fase de chainload; controlar tráfego conforme o padrão e respostas reais capturadas em testes. UDP 67 e/ou 4011 podem ser necessários, dependendo do cenário; **não presumir** que ProxyDHCP funcionará com qualquer firmware, switch gerenciável, DHCP snooping, VLAN ou roteador.

**Modo B — DHCP externo (`EXTERNAL_DHCP`):** painel instrui o técnico a configurar DHCP/relay de rede, `next-server`/bootfile com arquitetura, sem reconfigurar roteador automaticamente. Em ambientes controlados, DHCP externo pode apontar direto para iPXE HTTP Boot quando o firmware suportar. Verificar opções 66/67 e arquitetura de cliente; um bootfile genérico para BIOS e UEFI pode quebrar o boot.

**Não implementar servidor de leases DHCP neste escopo.** Um modo DHCP próprio só poderá existir futuramente após design e homologação separados, obrigatoriamente em VLAN/segmento isolado.

### 4.2 Sequência de boot (BIOS/UEFI)

1. Usuário habilita SLT Network Boot no painel e seleciona a NIC do servidor; Controller faz validação e libera os listeners.
2. PC inicia em PXE e recebe IP via DHCP existente.
3. Firmware recebe informações de bootstrap pelo ProxyDHCP ou DHCP externo; não interferir em máquinas que não solicitem PXE.
4. Transferência do carregador via TFTP/HTTP Boot. Carregador BIOS e UEFI precisam ser selecionados separadamente.
5. iPXE abre o menu `SLT Network Boot` por HTTP. Responder de forma diferente a **PXE firmware** e **cliente já encadeado iPXE** para evitar loop (`user-class=iPXE`/identificador equivalente quando disponível, mais teste real).
6. Perfil escolhe `kernel + initrd + SquashFS` (Rescue) ou `wimboot + boot.wim + BCD/boot.sdi` (WinPE).
7. Ambiente carrega rede; agente Go aponta ao `https://IP_DO_SERVIDOR:7443` e verifica o SHA-256 do certificado pinado.
8. Agente solicita enrollment CSR, recebe aprovação no WPF, estabelece mTLS, envia inventário. Operação destrutiva só após os fluxos de autorização existentes.
9. Menu oferece boot local/fallback, timeout adequado e caminho de saída documentado. **Não alterar BootOrder/UEFI NVRAM do cliente no MVP.**

### 4.3 Portas e rede

| Serviço | Porta padrão proposta | Observação |
|---|---|---|
| DHCP do roteador | UDP 67/68 | Continua pertencendo ao roteador/DHCP existente |
| ProxyDHCP SLT | UDP 67 e/ou 4011 | Conforme modo e implementação; detectar conflitos e validar em VM/rede isolada |
| TFTP bootstrap SLT | UDP 69 + portas dinâmicas | Abertura de firewall restrita à NIC/CIDR; TFTP não é canal de arquivos grandes |
| HTTP Boot Assets | TCP 8088 (configurável) | Transmitir apenas conteúdo de boot, nunca dados administrativos |
| Controller SLT | HTTPS TCP 7443 | Fluxos existentes de enrollment, pin e mTLS |

Firewall Windows: criar/remover regras **escopadas à interface/IP/sub-rede**, com consentimento UAC. Restaurar estado anterior ao desinstalar/desabilitar, sem apagar regras de terceiros. Caso portas estejam ocupadas, **falhar fechado e informar a causa**; jamais encerrar outro serviço ou alterar o roteador para “fazer funcionar”.

### 4.4 Especificação mínima de segurança dos servidores UDP/HTTP

- TFTP: apenas leitura, root isolada e fixa, sem `..`, caminhos absolutos, reparse points/junctions/symlinks ou enumeração; RFC/opções implementadas corretamente; limites de tamanho/quantidade e timeouts; não carregar mídia do cliente para o servidor por TFTP.
- ProxyDHCP: tratar pacotes como não confiáveis; tamanho máximo, campos binários validados, tipos DHCP/PXE conhecidos, arquitetura, VLAN e transaction ID; nunca anunciar endereços IP nem responder a DHCP genérico fora do modo autorizado.
- HTTP: allowlist de artefatos por ID, validação de nomes, `Content-Length`, `Range` quando apropriado, integridade do arquivo no catálogo, backpressure, rate limiting, conexões máximas, timeouts, leitura read-only, `Cache-Control` coerente, limites por cliente. Evitar hotlink WAN.
- HTTPS do Controller não pode ser substituído por HTTP por causa do boot. Transferência de arquivos pré-boot pode começar em HTTP por compatibilidade do firmware; verificar SHA e assinatura/bootloader antes de confiar. Preferir HTTPS em iPXE quando a cadeia/certificados forem compatíveis, sem fingir que HTTP sozinho fornece autenticidade.
- Logs sem senha, token ou caminho de dados de cliente; endereço MAC/IP apenas para auditoria local com retenção configurável.

---

## 5. Perfis de boot e mecanismo de mídias

### 5.1 Perfil `SLT_RESCUE_LINUX_X64`

**Meta:** iniciar o Debian Live Rescue via rede sem mídia USB, preservando o agente Go e suas verificações.

Trabalho técnico:

- Evoluir `rescue/scripts/build-debian-live.sh` ou criar `rescue/pxe/build-rescue-netboot.sh` para exportar **`vmlinuz`, `initrd` e `filesystem.squashfs`** a partir da mesma versão/build do Rescue usado na ISO. Não presumir nomes fixos sem inspecionar a saída do `live-build`.
- Gerar `manifest.json` com `schemaVersion`, commit, arquitetura, versão Debian, SHA-256 e tamanho de cada artefato; validar que todos pertencem ao **mesmo build**. Não importar apenas pela extensão.
- Pesquisar/validar o fluxo `live-boot` (por exemplo `boot=live components ip=dhcp fetch=http://IP:PORTA/.../filesystem.squashfs` ou alternativa suportada pela versão real). `fetch` pode copiar o SquashFS à RAM; dimensionar memória e avisar sobre demanda. `httpfs`/NFS/iSCSI são alternativas **somente após teste**, pois demandam configuração/compatibilidade adicional.
- O initramfs deve conseguir DHCP/rede **antes** de buscar o filesystem. Testar drivers `virtio`, `e1000`, `e1000e`, Realtek/Intel comuns; não assumir que firmware PXE funcional garante driver Linux.
- Manter utilitários existentes (`smartctl`, `nvme-cli`, ddrescue/partclone, NetworkManager, `slt-start`, binário Go), sem autogravação. Configurar ponto de entrada opcional para pré-preencher URL/pin **públicos** do servidor; nunca incluir credencial administrativa ou certificado/chave do agente na imagem.
- Adicionar opção de iniciar apenas diagnóstico read-only antes do enrollment; versão inicial inicia agente somente após confirmação local de URL/pin e pareamento.
- Testar boot usando QEMU SeaBIOS e OVMF por PXE, sem CD-ROM e sem volumes físicos. Verificar inventário e SMART em dispositivos sintéticos.

**Condição de aceite P2:** cliente alcança prompt Rescue em VM BIOS e VM UEFI, rede funcional, `slt-start`/agente executa, servidor registra enrollment, coleta inventário via mTLS, discos sintéticos **imutáveis** e boot USB anterior ainda funcional.

### 5.2 Perfil `SLT_WINPE_X64` (fase posterior)

**Meta:** iniciar WinPE AMD64 sem pendrive e conectar o agente WinPE existente ao Controller.

- Importar a mídia WinPE **fornecida legitimamente pelo operador** (ADK + WinPE add-on ou mídia permitida), com `boot.wim`, BCD, `boot.sdi` e carregadores necessários. O fluxo de importação atual focado em `install.wim` **não substitui esse processo**.
- Criar `rescue/windows-pe/New-WinPENetworkKit.ps1` e manifesto de artefatos, sem sobrescrever `New-WinPEKit.ps1`; preferir WinPE preparado por ADK e injeção controlada dos arquivos `slt-winpe-agent.exe`, configuração pública de servidor/pin e script de inicialização.
- Iniciar via **iPXE + wimboot** com arquivos HTTP da **mesma mídia validada**. Validar `boot.wim`, BCD e `boot.sdi` por boot real; não confiar que toda ISO de instalação Windows serve sem ajustes.
- Incluir driver de NIC e armazenamento WinPE quando necessário, obedecendo distribuição e licença do fabricante.
- Detectar a execução WinPE e as ferramentas `wpeinit`, `DISM`, `BCDBoot` e, no modo BIOS, `bootsect.exe` legítimo e compatível. Falhar antes de operações destrutivas quando faltar um componente.
- Emular e testar perda da rede, timeout, falta de memória, firmware divergente, mudança de NIC, download incompleto, mídia corrompida, pin errado e falta de scratch.
- Não incluir WIM, ADK, material Microsoft ou senha em repositório público, artefato de CI ou release binária pública.

#### Problema crítico: armazenamento temporário sem pendrive

**Estado real:** `winpe_common.go` baixa o payload integral de implantação para `ScratchDir` físico local. `winpe_windows.go` rejeita scratch em `X:` e exige espaço de mídia + 2 GiB fora do disco a instalar. **Portanto o simples boot WinPE por rede NÃO elimina automaticamente a necessidade desse armazenamento auxiliar.**

Implementar um **segundo backend de mídia**, selecionado explicitamente:

- `LOCAL_STAGING` — comportamento legado inalterado; download SHA-256 completo, scratch físico, proteção de disco.
- `NETWORK_SOURCE` — usar arquivo WIM de implantação em origem remota controlada e protegida na bancada, por exemplo **SMB read-only** (com conta restrita, credenciais efêmeras não embutidas em bootfile público) e `DISM /Apply-Image /ImageFile:<caminho remoto>` **somente se demonstrado funcional nos testes do WinPE/ADK usado**. A imagem remota deve ser imutável e validada (SHA original no catálogo; revisão de como garantir integridade na transferência). O scratch do DISM continua **local e separado do destino**, mas pode ser muito menor do que a imagem; dimensionar RAM disponível com margem, e testar regras de uso de `X:` para **scratch operacional pequeno**, sem aceitar staging integral em RAM por engano.

**Ponto de decisão obrigatório (ADR):** documentar se é seguro usar `X:\SLT-Scratch` na configuração testada, com carga máxima e memória mínima, ou se WinPE exigirá uma alternativa (ramdisk dedicado, volume auxiliar, streaming por API própria, etc.). Não afirmar suporte “sem qualquer disco auxiliar” até a instalação integral em VM sem segundo disco demonstrá-lo. Se a solução não for robusta, manter `NETWORK_SOURCE` como experimental e exigir scratch físico no perfil correspondente.

**Importante:** WinPE RAMDISK (`X:`) não é equivalente ao volume do sistema que será instalado. Nunca usar partição do disco de destino como scratch prévio à implantação, salvo novo desenho específico com demonstração de proteção e recuperação e consentimento adicional.

### 5.3 Perfis Linux de instalação

Não rotular uma mídia como “universal” por estar em `.iso`. Definir **adaptadores versionados** por família de distribuição:

- Ubuntu/Xubuntu: manter fluxo atual e criar perfil de netboot para versões/arquiteturas realmente testadas.
- Debian: instalador ou live netboot validado, com pacotes/URLs estáveis e checksums oficiais.
- Fedora e outros: somente quando houver estratégia específica de kernel/initrd, repositório/ISO e validações de boot.
- Menu pode **oferecer** outras distribuições, mas deve mostrar `UNSUPPORTED`/`EXPERIMENTAL` até homologação, impedindo instalação automatizada não testada.

Matriz por perfil: versão, arquitetura, firmware BIOS/UEFI, Secure Boot, RAM mínima, tamanho de rede/download, drivers e se instala de forma nativa ou via agente SLT. Se for apenas executar o instalador da distribuição, **não alegar instalação pelo Controller** nem exibir aprovação de disco como se o instalador externo obedecesse ao protocolo SLT.

### 5.4 Boot local, timeout e loops

- Entrada `Boot local`/`Sair` somente se firmware e bootloader suportarem chainloading seguro. Caso contrário, oferecer retorno/escape para menu de boot do firmware sem alegar boot local automático.
- Timer para menu, entrada padrão **não destrutiva**, proteção contra novo boot PXE após reinício de instalação. Não alterar ordem UEFI automaticamente.
- iPXE stage 1 e stage 2 devem ter identificadores diferentes: respostas por arquitetura e detecção de cliente já inicializado por iPXE (evitar baixar `ipxe.efi` repetidamente).
- Se PXE indisponível, falha explícita com caminho de boot local normal; não deixar máquinas ligadas num loop indefinido.

---

## 6. Painel WPF — experiência do técnico

Nova aba **Boot pela rede**, próxima a **Instalar sistema**:

```text
┌──────────────────── SLT DISK MANAGER ───────────────────────────┐
│ [Máquinas] [Clonagem] [Instalar sistema] [Boot pela rede] ...    │
├─────────────────────────────────────────────────────────────────┤
│ SERVIDOR DE BOOT                                               │
│ Status: DESLIGADO / INICIANDO / ATIVO / FALHA                    │
│ Interface Ethernet: [Intel I219-V • 192.168.1.10        v]       │
│ Sub-rede autorizada: [192.168.1.0/24]   Modo: [ProxyDHCP v]     │
│ HTTP boot: [8088]   Clientes ativos: 0                          │
│ [Verificar configuração] [ATIVAR BOOT] [DESATIVAR]              │
│                                                                 │
│ PERFIS DISPONÍVEIS                                             │
│ [x] SLT Rescue — BIOS/UEFI               HOMOLOGADO (após teste)│
│ [ ] Windows PE — x64                     EM DESENVOLVIMENTO      │
│ [ ] Ubuntu / Debian                      PENDENTE                │
│                                                                 │
│ CLIENTES RECENTES                                               │
│ Horário    MAC parcial   Modo  Estado/etapa       IP           │
│ ...                                                             │
│                                                                 │
│ AVISOS: DHCP, portas, firewall, Secure Boot, RAM, homologação   │
└─────────────────────────────────────────────────────────────────┘
```

Requisitos de UI:

- Botão ativar só se pré-verificações passarem; desativar imediatamente a oferta a **novas** sessões; status de portas e erros reais; sem “ATIVO” antes do serviço confirmar bind.
- Exibir nome amigável da NIC, IP, máscara/CIDR, velocidade, VLAN quando detectável, porta e perfil.
- Mostrar distinção **Disponível** (arquivo presente) versus **Homologado** (testado) e **Seguro para escrita** (perfil/agente testado separadamente).
- Não exibir credenciais, tokens, chaves privadas ou senhas temporárias em logs/exportações.
- A interface não implementa TFTP/DHCP nem operações de disco; chama API localhost do Controller. UI desabilitada durante reconfiguração crítica, com mensagens de rollback.
- Não dar impressão de que Wi-Fi é universal para pré-boot: PXE normalmente exige NIC Ethernet/UEFI compatível; os drivers Wi-Fi dentro do Rescue só passam a atuar **depois** que o SO Live começou.

---

## 7. Plano incremental de execução para agentes de IA

> **Instrução ao coordenador:** não executar todas as fases em um único PR. Fazer commits pequenos, testes a cada etapa, PRs com evidências e merge somente após gates. Criar branch da `main` atualizada; usar `feat/network-boot-*`. Respeitar divisão de arquivos e responsabilidades em `AGENTS.md`.

### P0 — Descoberta, arquitetura e testes de base (pré-requisito)

- [ ] Obter `main` mais recente, registrar commit SHA, executar build e suites existentes; guardar relatório baseline.
- [ ] Ler `AGENTS.md`, `contexto.md`, especificação, `docs/STATUS.md`, `docs/WORKFLOWS.md`, `docs/04-protocol.md`.
- [ ] Revisar `Program.cs`, `Store.cs`, `MediaRepository.cs`, `Models.cs`, configuração do instalador e `slt-start`.
- [ ] Publicar ADR com escolha processo separado/embutido, listeners/portas, formato dos manifestos, perfil de logs e políticas DHCP.
- [ ] Mapear dependências e licenças: iPXE, wimboot, binários EFI e ferramentas TFTP; baixar somente fontes oficiais, validar SHA e licenças; **não** incorporar componentes não auditados por simples URL.
- [ ] Decidir interfaces/arquiteturas suportadas inicialmente: x86_64 BIOS PXE e x86_64 UEFI sem Secure Boot, em laboratório **isolado**.
- [ ] Definir padrão de evidência e flag `experimental`/`validated`; criar matriz de compatibilidade e estratégia de rollback.

**Gate P0:** ADR aprovado, CI inicial verde, nenhum serviço de rede novo ativo por padrão.

### P1 — Infraestrutura PXE/HTTP + UI (sem imagem inicializável destrutiva)

- [ ] Criar `SLT.NetworkBoot.Core` e `SLT.NetworkBoot.Service`, com ciclo de vida idempotente, locks, validação da interface/CIDR e log auditável.
- [ ] Implementar modo ProxyDHCP (se houver protocolo/biblioteca validada) e modo DHCP externo, sem DHCP de leases.
- [ ] Implementar TFTP somente leitura com sandbox e limites; arquivos pequenos de bootstrap para BIOS e UEFI.
- [ ] Implementar HTTP estático de artefatos por ID, conteúdo imutável e limites; pinagem/manifestos SHA-256.
- [ ] Construir script/menu iPXE por perfil e detecção anti-chainload-loop.
- [ ] Adicionar endpoints administrativos apenas loopback e aba WPF: validação/start/stop/status/logs.
- [ ] Instalação de Windows Service e regras de firewall com UAC explícito; rollback ao parar/desinstalar.
- [ ] Testar DHCP verdadeiro **somente** em rede virtual host-only isolada; não transmitir pacotes PXE em LAN de produção no CI.
- [ ] Integrar testes de parser DHCP, TFTP, HTTP, path traversal, timeout, corrupção, choque de portas e start/stop repetido.

**Gate P1:** firmware/VM chega ao menu iPXE e pode sair, sem tocar em discos e sem alterar o DHCP do roteador de produção.

### P2 — SLT Rescue Linux pela rede (MVP operacional)

- [ ] Exportar kernel/initramfs/SquashFS do build Debian Live do projeto; manifesto com SHA e versão.
- [ ] Adicionar geração de perfil Rescue BIOS/UEFI + boot parameters para rede/`live-boot`.
- [ ] Verificar RAM, NIC, timing DHCP, caminho HTTP e fallback se servidor ficar indisponível.
- [ ] Iniciar a VM **sem ISO e sem disco USB**, somente virtual PXE; testar SeaBIOS e OVMF.
- [ ] Rodar `slt-start`, enrollment CSR/pin/mTLS, inventário/SMART em discos sintéticos read-only.
- [ ] Testar perda de conexão antes de carregar filesystem, durante transferência e após boot.
- [ ] Validar que a ISO USB antiga continua gerada e inicia com os testes existentes.
- [ ] Documentar como usar, dependências do roteador e limitações de hardware.

**Gate P2 / MVP:** Rescue PXE funcionando em VM BIOS/UEFI, sem Secure Boot, com agente no Controller e sem alterar discos de dados. **Não publicar como homologado em PC real.**

### P3 — WinPE PXE, somente inicialização + inventário

- [ ] Criar importação de WinPE e WIM de BOOT separado de imagem `install.wim`.
- [ ] Adaptar gerador para kit WinPE de rede, sem mudar a versão USB.
- [ ] Integrar `wimboot`, BCD/boot.sdi/boot.wim, drivers necessários, seleção BIOS/UEFI e integridade.
- [ ] Executar WinPE via PXE em QEMU/Hyper-V VM com **somente disco sintético de destino**, ainda sem permitir escrita.
- [ ] Parear agente WinPE via CSR/mTLS; comprovar inventário de discos, de NIC e detecção do firmware.
- [ ] Garantir que token admin e secrets não apareçam no `boot.wim`, `boot.ipxe` ou em arquivos via TFTP/HTTP.

**Gate P3:** boot WinPE e agente executando inventário, sem pendrive; nenhuma instalação destrutiva ativada nesta fase.

### P4 — Implantação Windows sem mídia/disco auxiliar + Linux adicionais

- [ ] Fazer ADR específica de `NETWORK_SOURCE` vs `LOCAL_STAGING`, credenciais SMB restritas e scratch WinPE.
- [ ] Demonstrar que a versão escolhida de DISM consegue aplicar WIM remoto no laboratório; implementar verificação de integridade e política de credenciais sem expor share de escrita.
- [ ] Validar scratch temporário com RAM mínima configurada; bloqueio por insuficiência de memória e por compartilhamento inacessível.
- [ ] Testar instalação Windows 10/11 em VMs com **apenas disco de destino** (sem USB e sem segundo disco). Caso falhe, classificar recurso como experimental e manter exigência de armazenamento auxiliar.
- [ ] Revalidar todas as proteções do agente: disco físico, serial/modelo, firmware, mídia, assinaturas, confirmação local, hash e anti-replay.
- [ ] Testar Windows até boot local completo, OOBE, login e drivers; não tratar `DISM success` como finalização total.
- [ ] Implementar perfis Linux adicionais versionados com testes de boot/instalação/primeiro boot e confirmações correspondentes.
- [ ] Testar duas ou mais implantações simultâneas, isolamento de RAM/banda e retorno do Controller sem deadlock.

**Gate P4:** instalação/primeiro boot completos em VMs descartáveis; sem dependência de USB ou scratch físico **somente se** a evidência comprovar este modo. Sem homologação para discos reais.

### P5 — Compatibilidade, Secure Boot e hardware físico

- [ ] Testar firmware BIOS PXE, UEFI PXE, UEFI HTTP Boot (se desejado) com NICs reais de fabricantes diferentes.
- [ ] Integrar **somente bootloaders assinados e cadeia de confiança verificada** para Secure Boot; avaliar releases assinadas atuais de iPXE e seus requisitos. Não usar build EFI não assinado no perfil Secure Boot.
- [ ] Verificar Windows WinPE, `wimboot`, Debian Live/GRUB/shim com Secure Boot e revogações/DBX atualizadas. A cadeia do Linux Rescue precisa de validação própria, não herdada automaticamente do boot WinPE.
- [ ] Matriz de RAM (2/4/8/16 GiB), velocidade de rede (100M/1G/2.5G), placas Intel/Realtek, UEFI/BIOS e NVMe/SATA.
- [ ] Executar testes de erro: cabo retirado, queda de energia no servidor, DHCP indisponível, conflito de PXE, TFTP atrasado, falha de servidor, mídia adulterada, cliente fora de allowlist.
- [ ] Homologar inicialmente em discos **descartáveis** e com autorização presencial, depois elaborar procedimento para bancada com clientes reais.
- [ ] Definir suporte a VLAN/DHCP relay, WoL e reboot remoto **somente como evolução opcional**, com aprovação e verificações específicas.

**Gate P5:** publicar matriz de hardware **realmente testado**, logs/evidências e limitações declaradas; somente aí divulgar perfil como homologado para produção.

---

## 8. Testes obrigatórios e critérios de aceite

### 8.1 Matriz automatizada (mínimo)

| Cenário | Gate | Aceite |
|---|---|---|
| Serviço PXE desabilitado após instalação | P1 | Nenhuma porta DHCP/TFTP/boot HTTP vinculada sem autorização |
| Detectar interface errada e IP WAN/VPN | P1 | Recusar configuração ou exigir fluxo admin específico, sem expor rede |
| Porta 69/4011/HTTP já ocupada | P1 | Falha segura, erro visível, sem derrubar software existente |
| Outro DHCP ativo na LAN simulada | P1 | PXE coexistente sem oferecer leases concorrentes |
| BIOS e UEFI chainload | P1 | Menu exibido, sem loop infinito; imagens por arquitetura corretas |
| Path traversal TFTP/HTTP | P1 | Bloqueio (`..`, absolutos, symlink/reparse, IDs inválidos) |
| Flood DHCP/TFTP/HTTP e arquivo grande | P1 | Limites, timeouts e estabilidade do Controller |
| Stop/start repetidos | P1 | Sockets fechados e reabertos sem zombies, jobs ativos preservados |
| PXE → Linux Rescue | P2 | Boot real em VM BIOS/UEFI sem ISO/USB |
| Rescue → mTLS + inventário | P2 | Cadastro visível no painel; host synthetic disk hash intacto |
| PXE → WinPE | P3 | Boot em VM; `slt-winpe-agent` conectando sem mídia física |
| Windows via mídia em rede | P4 | Rede, scratch, hash e confirmação locais; primeiro boot final comprovado |
| Falha da rede durante instalação | P4 | Job interrompido, sem replay e sem alegar disco íntegro |
| Rede isolada e múltiplos clientes | P4 | Paralelismo sem colisão de diretórios/sessões, limites respeitados |
| Secure Boot ativado | P5 | Somente perfis com cadeia assinada e evidência VM/físico |

### 8.2 Ambientes de teste

- **CI:** testes unitários e integração local sem emitir DHCP no runner compartilhado. Para boot real, QEMU em namespace/network bridge **dedicada isolada** (`tap`/`socket`/`user-mode` conforme harness), sem bridge para a rede real. A topologia deve ser criada e destruída pelo harness com logs e assertions.
- **Discos de teste:** arquivos RAW/qcow2/VHDX temporários, nunca volumes físicos, passthrough de host ou `PhysicalDrive` do operador.
- **Firmware:** SeaBIOS e OVMF, com variáveis OVMF isoladas; Secure Boot apenas em etapa própria com certificados e versões conhecidos.
- **Métricas:** duração até menu, taxa HTTP de download, memória máxima por VM, RAM disponível do Rescue/WinPE, bytes transferidos por sessão, concorrência, códigos de erro e tempo de resposta do Controller.
- **Regressões:** build .NET, Go vet/test, suites Controller/Windows Agent/Desktop, ISO USB, integração HTTPS/mTLS, clones e deploys existentes.

### 8.3 Comandos existentes (baseline; revisar documentação do HEAD)

```powershell
./scripts/build.ps1 -Configuration Debug
./scripts/test-all.ps1 -Integration
./scripts/package-release.ps1
```

O novo harness deve ter comando próprio reproduzível, por exemplo:

```powershell
# Proposta: só criar quando houver implementação real
python tests/integration/pxe-lab.py --mode rescue --firmware bios,uefi --isolated
```

Jamais registrar os exemplos propostos como testes aprovados. A cada fase, salvar SHA do commit, comando executado, plataforma/versões, retorno, screenshots/séries de logs, hashes de disco antes/depois e limitações observadas.

---

## 9. Estratégia de versionamento, PR, integração e backup

1. Verificar `git status`, atualizar `main`, criar branch por fase (`feat/network-boot-p0-design`, `feat/network-boot-p1-core`, etc.). Nenhum commit direto em `main`.
2. Contratos `Core` e ADR primeiro; implementação independente do serviço e da UI em commits distintos; evitar conflitos paralelos em `Program.cs`, `AGENTS.md`, `contexto.md` e `.slnx`.
3. Abrir PR por fase com: problema, escopo, arquitetura, arquivos alterados, compatibilidade, segurança, matriz de testes, evidências, riscos, rollback e próximos passos.
4. Executar CI completo. Se GitHub Actions falhar por infraestrutura/cobrança, registrar claramente e executar **testes locais equivalentes**, sem declarar CI remoto aprovado.
5. Requerer revisão de segurança para interfaces de rede, TFTP, DHCP, HTTP e permissões. Não fazer merge se falhar teste de não regressão/destruição de discos.
6. Após merge, atualizar `README.md`, `docs/STATUS.md`, `docs/WORKFLOWS.md`, `AGENTS.md` e `contexto.md` conforme disciplina do projeto. Versionar somente código, scripts e **manifestos sem payload proprietário**.
7. Build/publicação no GitHub Releases somente após verificações reais; não substituir release estável/beta anterior sem política de rollback.
8. Backup privado Google Drive de **fonte + Git bundle + manifestos/hashes + evidências apropriadas**, pelo procedimento já usado; **não** incluir imagens Windows/WinPE, certificados privados, dados de clientes ou mídias recebidas, salvo política de backup privado explicitamente autorizada e auditada. Validar releitura/hash/tamanhos e guardar recibos em pasta ignorada.
9. Atualização do site de softwares, se houver nova release, em PR separado. Não modificar TonalHub, Converter ou outros projetos sem necessidade demonstrada e aprovação no contexto da tarefa.

### Política de release e comunicação

- `NETWORK_BOOT_DEV`: código em desenvolvimento, não habilitar na release distribuída.
- `NETWORK_BOOT_LAB`: ambiente em VM homologado, aviso explícito de laboratório.
- `NETWORK_BOOT_HARDWARE_VERIFIED`: homologado somente para matriz física publicada.
- Nas telas e no README, divulgar status por **perfil de boot + arquitetura + Secure Boot**, e não uma etiqueta global “PXE funciona em tudo”.

---

## 10. Registro de riscos e decisões pendentes

| ID | Risco/decisão | Severidade | Mitigação/critério |
|---|---|---|---|
| R01 | Serviço competir com DHCP local ou oferecer PXE na rede errada | Crítica | Desligado por padrão, NIC/CIDR, ProxyDHCP em laboratório, sem leases |
| R02 | Firmware PXE incompatível, BIOS/UEFI recebendo loader incorreto | Alta | Seleção arquitetura; VM + matriz física |
| R03 | Secure Boot bloquear binário iPXE/Rescue | Alta | Loader assinado e cadeia de confiança homologada; não burlar Secure Boot |
| R04 | Transferência parcial/alterada de binário por HTTP/TFTP | Alta | Manifests imutáveis, SHA/verificação e cadeia de assinatura no boot |
| R05 | WinPE sem RAM/scratch suficiente, DISM falhar com disco parcial | Crítica | Separar staging e scratch, validar RAM e falhar antes de escrever; testes com falhas injetadas |
| R06 | Credenciais SMB/Controller vazarem no menu/preboot público | Crítica | Não embutir credenciais, separar bootstrap de mTLS e usar acesso efêmero limitado |
| R07 | Boot PXE gerar operação destrutiva sem consentimento | Crítica | Nenhum job automático; manter assinatura, fingerprint, confirmação dupla |
| R08 | Loop de boot após primeiro reboot do instalador | Alta | Menu não destrutivo, fallback, fluxo explícito de retorno ao disco local |
| R09 | Exposição/DoS por TFTP/HTTP e uso excessivo de banda | Alta | VLAN/CIDR, limites por cliente, rate limiting e monitoramento |
| R10 | Licença ou redistribuição ilegal de WinPE/ISOs/bootloaders | Alta | Origem local, avisos legais, inventário SBOM e licenças |
| R11 | Regressão nas operações antigas/instalador | Alta | Service isolado, contratos existentes preservados, CI e smoke tests |
| R12 | Chamar “qualquer ISO” de compatível | Média | Perfis explícitos e homologação por SO, versão, firmware e arquitetura |

**Decisões para ADR antes da implementação avançada:**

- Serviço independente vs hospedado no Controller (recomendação: independente).
- Implementar ProxyDHCP diretamente em .NET vs integrar processo auditado; compatibilidade Win11/serviços Windows.
- Estratégia de DHCP externo em redes com VLAN/relay e suporte a UEFI HTTP Boot.
- Estratégia Secure Boot para iPXE, Rescue Linux e WinPE; compatibilidade com DBX.
- Fonte remota de WIM e scratch WinPE sem segundo disco (prova em VM).
- Distribuição/assinatura dos bootloaders de terceiros, licenças e atualização segura.
- Política de expiração/allowlist de sessões PXE sem tratá-la como autenticação forte.

---

## 11. Definição de pronto (Definition of Done)

Uma fase está pronta **somente** quando:

- [ ] Código implementado no repositório real, não apenas mocks/documentação.
- [ ] Contratos e validações server-side/agente coerentes e tipados; novo endpoint não expõe API administrativa.
- [ ] Testes da fase executados, reproduzíveis, verde com evidência, incluindo negativos e regressões.
- [ ] Boot real de laboratório comprovado quando a fase diz oferecer boot real; sem pendrive/mídia montada se o requisito for `network-only`.
- [ ] Sem alteração de disco físico no CI; sem autogravação pela simples escolha de PXE.
- [ ] Compatibilidade BIOS/UEFI/Secure Boot declarada por perfil e respaldada por testes.
- [ ] Documentação operacional de configuração DHCP/firewall/NIC, solução de problemas e reversão.
- [ ] Inventário de terceiros, licenças e hashes das ferramentas incluídas atualizado.
- [ ] Instalação/atualização/desinstalação e stop/start não rompem serviço Controller/Windows Agent.
- [ ] PR revisado, merge verificado, changelog/`STATUS.md`/`AGENTS.md`/`contexto.md` atualizados.
- [ ] Backup privado validado somente quando for parte do ciclo de release e mediante fluxo autorizado.

**Aceite final do produto (futuro):** um PC de teste conectado por Ethernet, sem pendrive, seleciona boot PXE, entra no SLT Rescue, aparece no painel como cliente pareado, executa diagnóstico; outro PC de teste inicia WinPE pela rede e instala Windows com as confirmações já existentes; um terceiro executa perfil Linux homologado; tudo comprovado em VMs e depois em hardware descartável, sem alterar indevidamente DHCP, segurança do Controller ou dados de máquinas vizinhas.

---

## 12. Instrução pronta para o agente coordenador (copiar como briefing)

> Você está no repositório `mikerock12/slt-disk-manager`. Sua missão é implementar incrementalmente a funcionalidade SLT Network Boot (PXE/iPXE) seguindo este arquivo. Comece pela **P0**, leia o HEAD do repositório e todos os documentos obrigatórios. Registre inconsistências entre este mapa e o código mais recente antes de mudar contratos. Priorize o serviço independente no Windows, PXE desligado por padrão, ProxyDHCP sem leases, TFTP restrito, HTTP de artefatos somente leitura e painel WPF administrativo localhost. A primeira entrega funcional deve ser **P1 + P2: menu de PXE e SLT Rescue Linux iniciando em VMs BIOS/UEFI sem USB, com agent CSR/mTLS e inventário read-only**. **Não** implemente instalação/destruição automática. Preserve USB, Windows Agent, Clone, Deploy, arquivos e segurança atuais. Faça commits por fase, testes negativos e de regressão, PR/CI, documentação e plano de rollback. Não anuncie suporte a WinPE, Windows sem scratch, Secure Boot ou qualquer ISO até que suas próprias fases estejam completas e testadas. Nunca inclua ISOs WinPE/Windows ou segredos em código público ou releases. Relate o que foi efetivamente feito, evidências e pendências.

---

## 13. Referências técnicas e arquivos de origem

### Repositório SLT — evidências do estado atual

- Código: https://github.com/mikerock12/slt-disk-manager
- README: https://github.com/mikerock12/slt-disk-manager/blob/main/README.md
- Matriz de status: https://github.com/mikerock12/slt-disk-manager/blob/main/docs/STATUS.md
- Instruções multiagentes: https://github.com/mikerock12/slt-disk-manager/blob/main/AGENTS.md
- Contexto persistente: https://github.com/mikerock12/slt-disk-manager/blob/main/contexto.md
- Workflows: https://github.com/mikerock12/slt-disk-manager/blob/main/docs/WORKFLOWS.md
- Controller: https://github.com/mikerock12/slt-disk-manager/blob/main/src/server/SLT.Controller.Host/Program.cs
- Catálogo de mídias: https://github.com/mikerock12/slt-disk-manager/blob/main/src/server/SLT.Controller.Host/MediaRepository.cs
- Build Rescue: https://github.com/mikerock12/slt-disk-manager/blob/main/rescue/scripts/build-debian-live.sh
- WinPE: https://github.com/mikerock12/slt-disk-manager/blob/main/rescue/windows-pe/New-WinPEKit.ps1
- WinPE agent: https://github.com/mikerock12/slt-disk-manager/blob/main/src/rescue-agent/internal/agent/winpe_common.go
- Teste de ISO local: https://github.com/mikerock12/slt-disk-manager/blob/main/rescue/scripts/test-iso-boot.sh

### Documentação externa (revalidar versões no momento da implementação)

- iPXE chainloading (BIOS/UEFI): https://ipxe.org/howto/chainloading
- iPXE com Secure Boot: https://ipxe.org/secboot
- iPXE `wimboot`: https://ipxe.org/wimboot
- iPXE inicialização WinPE: https://ipxe.org/howto/winpe
- Microsoft — PXE para Windows PE: https://learn.microsoft.com/pt-br/windows/deployment/configure-a-pxe-server-to-load-windows-pe
- Microsoft — WinPE: https://learn.microsoft.com/en-us/windows-hardware/manufacture/desktop/winpe-intro/
- Microsoft — customização de imagens WinPE: https://learn.microsoft.com/en-us/windows/deployment/customize-boot-image
- Debian `live-boot` (parâmetros `fetch=` e `httpfs=`): https://dyn.manpages.debian.org/unstable/live-boot-doc/live-boot.7.en.html

**Observação final:** este mapa representa **planejamento baseado na inspeção do repositório**, não implementação, release nem homologação. Uma mudança no HEAD pode exigir ajustes. A decisão mais importante da fase Windows é separar **carregamento do WinPE** de **aplicação da imagem Windows**, resolvendo o scratch com testes reais antes de prometer zero pendrives/zero discos auxiliares.
