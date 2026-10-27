# Agente Windows

Windows 10/11 x64; .NET 10, ou pacote publicado self-contained. Nenhuma operação deste agente grava setores ou altera boot/partições. O inventário usa Get-Disk/Get-Partition; o diagnóstico SMART opcional usa smartctl instalado separadamente, sem redistribuir terceiros.

```powershell
.\SLT.WindowsAgent.exe --server https://192.168.1.20:7443 --server-pin HASH_SHA256_EXIBIDO_NO_SERVIDOR
```

Copie o pin diretamente do computador servidor. Confira o código de pareamento de oito dígitos no cliente antes de aprovar no painel. O agente inicia todas as conexões; nenhuma porta de entrada é aberta no cliente. Cada instalação cria chave RSA individual e certificado com ACL restrita a usuário atual/SYSTEM/Administradores. Não copie o diretório de estado entre computadores.

Para inventário completo, inicie como administrador. `--state-dir` define estado privado (padrão `%ProgramData%\SLT\WindowsAgent`). `--smartctl` exige caminho absoluto para smartctl.exe legítimo, por exemplo `C:\Program Files\smartmontools\bin\smartctl.exe`. Sem essa opção a capacidade SMART não é anunciada. `--collect-only` imprime inventário local JSON sem conexão; `--once` realiza pareamento se necessário e um ciclo de inventário/job antes de sair.

Os jobs aceitos são INVENTORY e SMART; outros tipos são rejeitados localmente. Planos exigem assinatura RSA do servidor fixado, autorização, UUID, deviceId correto e validade. Diário persistente impede replay antes da execução. Falhas de rede não repetem jobs. Um resultado local fica em `job-<id>.result.json`; se sua confirmação ao servidor se perder, o operador deve reconciliar o estado e planejar um novo diagnóstico.

O adaptador SMART converte somente caminhos físicos locais `PhysicalDrive0..255` para `/dev/pd0..255`, conforme o [manual oficial do smartctl para Windows](https://github.com/smartmontools/smartmontools/blob/main/src/smartctl.8.in). Usa exclusivamente `--json --all`; testes que alteram estado do dispositivo não são executados.

Teste de integração reproduzível: após compilar Controller e agente em Debug, execute `pwsh -NoProfile -File tests/integration/windows-agent-e2e.ps1` na raiz. Ele usa porta loopback 17444 e estado único sob `runtime/` ignorado pelo Git, verifica TLS fixado, pareamento, mTLS, job INVENTORY assinado e revogação. Faz apenas inventário local em leitura; não valida restauração, boot ou compatibilidade de hardware. A importação TLS do certificado no Windows usa o provedor protegido da conta atual para compatibilidade com SChannel; nenhum PFX temporário é gravado pelo agente.

Certificado expirado/revogado exige ação local e novo pareamento; não apague credenciais automaticamente. Revogue primeiro a identidade antiga no painel e use um novo diretório de estado. Credenciais são sensíveis e nunca devem entrar em commit ou backup público. Instalação como serviço exige os scripts de implantação do projeto. SMART ausente, serial não informado por adaptador e permissões insuficientes são apresentados como indisponíveis; não como saúde garantida.
