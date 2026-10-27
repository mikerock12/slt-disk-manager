# Instalação do serviço Windows

Para habilitar o gerenciador de arquivos, prepare um JSON local e informe `-FileRootsPath C:\SLT\scopes.json` na instalação. O instalador copia esse arquivo para `file-roots.json` no estado privado do agente, com acesso restrito a Administradores e SYSTEM, e registra o serviço apontando para essa cópia. Sem esse parâmetro, o serviço não anuncia acesso a arquivos; em reinstalações, informe-o novamente para habilitar os escopos. Não altere o arquivo original esperando mudar a cópia do serviço.

```json
[
  {"id":"documentos","label":"Documentos autorizados","path":"C:\\Users\\Cliente\\Documents","allowRead":true,"allowWrite":false},
  {"id":"entregas","label":"Entregas ao cliente","path":"C:\\SLT-Entregas","allowRead":false,"allowWrite":true}
]
```

Crie previamente as pastas autorizadas. O agente rejeita escopos que incluam seu estado, credenciais ou binários e caminhos com links/junctions. A restauração cria uma pasta nova dentro do escopo de escrita; não sobrescreve arquivos existentes. Escolha apenas os dados autorizados pelo responsável do computador.

Execute no PC cliente autorizado, em PowerShell como administrador. Use um pacote **publicado para win-x64**, não apenas a pasta de build. O diretório de origem precisa conter o executável e `Hardware/Collect-Inventory.ps1`.

```powershell
.\Install-Agent.ps1 -PackageDirectory C:\SLT\WindowsAgent `
  -Server https://192.168.1.20:7443 -ServerPin SHA256_DE_64_CARACTERES
```

O instalador copia o pacote para Program Files, restringe ACLs, roda uma sessão local de pareamento visível e registra o serviço `SLTWindowsAgent` como LocalSystem para consultas de hardware. Durante o pareamento, confira no computador cliente o código que aparece e aprove essa solicitação no painel do servidor. Não distribua o estado ou a chave privada de um PC a outro.

O agente utiliza somente conexões de saída; nenhuma regra de firewall de entrada é criada no cliente. O token administrativo do servidor nunca é necessário no cliente. `-SmartctlPath` habilita SMART com uma instalação local legítima de smartmontools. Nenhuma ferramenta de terceiros é redistribuída neste pacote.

`Uninstall-Agent.ps1` para o serviço e remove seu registro, preservando binários e credenciais para evitar apagar evidências. Revogue a identidade no servidor antes de descartar o estado. Para atualizar: desinstale o serviço, publique/copie uma versão compatível e reinstale usando o mesmo `-StateDirectory`; certificados válidos serão reutilizados. Instaladores MSI/assinatura Authenticode e homologação de hardware ainda são etapas de release separadas.
