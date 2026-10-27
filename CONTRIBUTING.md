# Contribuição

Leia AGENTS.md, contexto.md, docs/STATUS.md e a especificação. Não alterar contratos
ou diretórios de outro agente sem coordenação. Toda tarefa encerra com atualização
de AGENTS.md/contexto.md: evidência, decisão, arquivos, teste e pendência.

Execute scripts/build.ps1 e scripts/test-all.ps1 -Integration. Não usar discos reais
em testes automatizados. Separe build/teste sintético de homologação em VM/hardware.
Registre desvios de arquitetura em docs/ADR; nunca simule sucesso de ferramenta
ausente. Não versionar dados de clientes, credenciais ou estado.
