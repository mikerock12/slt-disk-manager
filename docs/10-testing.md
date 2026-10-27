# Testes e evidências

Executados nesta máquina Windows em 2026-10-26:

- Build da solução: 0 erros/avisos.
- Controller: 37 checks sintéticos de identidade, autorização, assinatura,
  confirmação, locks, integridade, geometrias, reinício, auditoria e backup SQLite.
  Inclui regressões de limpeza com arquivo bloqueado e após liberação do handle;
  falha de limpeza preserva a pasta com aviso em vez de gerar exceção sem tratamento.
- Windows client: 24 checks de assinatura, pin, URI privada, replay e SMART.
- Windows client E2E: 4 cenários reais entre processos (HTTPS, CSR/mTLS/inventário,
  tarefa assinada após reinício, revogação). Duas unidades locais foram apenas lidas.
- Rescue Go: 5 testes com subcenários e vet; fixture de 4 MiB+512 bytes capturada e
  restaurada com igualdade completa. Compilação Linux amd64 e Windows fixture.
- Integração .NET↔Go: 6 cenários, incluindo exclusão de segunda instância e ciclo
  captura/verificação/restauração com 2 chunks via HTTPS/mTLS reais.
- Scripts PowerShell e bash: validação sintática; scripts de instalação de serviço
  não instalados no sistema durante os testes.
- Auditoria NuGet com NU1901–NU1904 como erro: passou após atualização SQLite.

Os harnesses .NET são executáveis de verificação (`dotnet run`), não suites xUnit.
Retornam exit code diferente de zero em falhas. CI chama explicitamente cada um.

Comandos reproduzíveis:

```powershell
./scripts/build.ps1 -Configuration Debug
./scripts/test-all.ps1 -Integration
```

O Windows usa SChannel e requer chaves importadas em container compatível; testes
em sandbox podem falhar por restrição de criptografia/rede. A integração real foi
executada com acesso normal aos serviços Windows, sem desativar pin de certificado.

Ainda não executados: UI visual completa (lançamento pela ferramenta de UI
travou), boot de ISO, VMs UEFI/Windows/Linux, queda de energia durante escrita,
matriz física SATA/NVMe/RAID/Secure Boot e primeiro boot após clone/deployment.
Não confundir sucesso nos fixtures com homologação desses cenários.
