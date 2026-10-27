# Release e backup

`scripts/package-release.ps1` publica server, desktop e client Windows x64 com
runtime incluído, copia Rescue Linux e scripts de operação, gera SHA256SUMS e ZIP.
O pacote é beta sem assinatura de código; conferir hash após transporte.

O conector Drive limita cada arquivo a 100 MiB. O ZIP beta.2 tem 173.358.572 bytes e
foi dividido em 3 partes (64 MiB, 64 MiB, 39.140.844 bytes). Baixe as partes junto de
package-manifest.json, Reunir-Pacote.ps1 e Reunir-Pacote.cmd na mesma pasta e execute
o CMD. A reunião foi testada e produziu o SHA256 original:
`b9e822afd2de867fad091afe5425c355cea3ae77b1248233c26cd2e11dccb04f`.

O backup de projeto inclui fontes, especificação, documentação, configurações de
build/CI e Git bundle quando disponível. Exclui caches, build temporário, runtime,
credenciais, banco operacional, dados e imagens de clientes. O ZIP portátil pode
ser salvo separadamente no mesmo destino Google Drive privado.

Para recuperar fonte de um Git bundle:

```powershell
git clone slt-disk-manager.bundle pasta-recuperada
```

Escolha a branch feat/workshop-client-server se a publicação/merge ainda estiver
pendente. Refaça build/testes antes de uso. Este backup não restaura identidade da
bancada: credenciais devem ter cópia privada protegida sob gestão do proprietário,
ou o servidor novo deve gerar CA e exigir novo pareamento/revogação do antigo.

Para copiar estado operacional consistentemente, pare o serviço e proteja a pasta
de estado completa (CA, certificado servidor, admin token, SQLite e repositório).
Não copie banco WAL aberto isoladamente nem envie essa pasta a GitHub público.
O helper Store.Backup usa API de backup SQLite e foi testado, mas não há assistente
de backup de operação no painel nesta beta.
