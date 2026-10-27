# Contexto persistente

Atualizado em 2026-10-26. Projeto novo baseado na especificação técnica 1.0.0.

## Objetivo autorizado
Implementar client Windows, controlador Windows/WPF e Rescue Linux; criar repo público e destaque no GitHub, commit/push/PR/CI/merge e backup no Google Drive.

## Estado e evidências
- Inicialmente só havia a especificação (76 KB), sem repositório Git.
- SDK .NET 10.0.403, Git, Node e Python instalados; Go não encontrado no PATH.
- Conta GitHub conectada: mikerock12. CLI gh com token inválido; criação de repo e destaque exigem fallback por navegador se a CLI não for recuperada.
- Google Drive conectado a Maicon Nunes.
- Nenhum teste em VM/hardware executado. Não declarar produto completo/homologado antes dos critérios da especificação.

## Decisões de implementação
- .NET 10 LTS, ASP.NET Core, WPF, SQLite; Rescue Go.
- Protocolo v1 HTTPS JSON com polling de saída, mTLS e planos assinados. ADR documenta troca do gRPC proposto para permitir implementação e diagnóstico simples; sem shell arbitrário.
- Inventário e diagnóstico Windows somente leitura; escrita offline somente no Rescue com barreiras locais adicionais.
- Operações ainda não homologadas são claramente bloqueadas, sem simular sucesso.

## Próximos passos
Implementação paralela por diretórios, integração e testes de segurança, empacotamento, publicação e backup. Consolidar resultados dos agentes aqui e em AGENTS.md ao concluir cada tarefa.
