# Instalação do serviço Windows

Execute no PC cliente autorizado, em PowerShell como administrador. Use um pacote **publicado para win-x64**, não apenas a pasta de build. O diretório de origem precisa conter o executável e `Hardware/Collect-Inventory.ps1`.

```powershell
.\Install-Agent.ps1 -PackageDirectory C:\SLT\WindowsAgent `
  -Server https://192.168.1.20:7443 -ServerPin SHA256_DE_64_CARACTERES
```

O instalador copia o pacote para Program Files, restringe ACLs, roda uma sessão local de pareamento visível e registra o serviço `SLTWindowsAgent` como LocalSystem para consultas de hardware. Durante o pareamento, confira no computador cliente o código que aparece e aprove essa solicitação no painel do servidor. Não distribua o estado ou a chave privada de um PC a outro.

O agente utiliza somente conexões de saída; nenhuma regra de firewall de entrada é criada no cliente. O token administrativo do servidor nunca é necessário no cliente. `-SmartctlPath` habilita SMART com uma instalação local legítima de smartmontools. Nenhuma ferramenta de terceiros é redistribuída neste pacote.

`Uninstall-Agent.ps1` para o serviço e remove seu registro, preservando binários e credenciais para evitar apagar evidências. Revogue a identidade no servidor antes de descartar o estado. Para atualizar: desinstale o serviço, publique/copie uma versão compatível e reinstale usando o mesmo `-StateDirectory`; certificados válidos serão reutilizados. Instaladores MSI/assinatura Authenticode e homologação de hardware ainda são etapas de release separadas.
