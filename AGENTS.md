# SLT Disk Manager — instruções de colaboração

Leia `SLT_Disk_Manager_Especificacao_Tecnica.md` e `contexto.md` antes de alterar o projeto.
O usuário autorizou trabalho multiagentes, implementação, repositório público, CI/PR/merge e backup no Drive.

## Responsabilidades
- Coordenador: contratos, Controller, persistência, segurança, integração, CI e entrega.
- Agente Windows: somente `src/windows-agent/` e testes específicos.
- Agente Desktop: somente `src/desktop/` e documentação de sua interface.
- Agente Rescue: somente `src/rescue-agent/`, `rescue/` e testes Go.
- Mudanças de contratos passam pelo coordenador. Não sobrescrever alterações de colegas.

## Regras obrigatórias
- LAN, TLS fixado e certificados individuais; descoberta não concede confiança.
- Nada de shell remoto genérico. Jobs tipados e capacidades explícitas.
- Gravação de disco exige plano assinado, validade, confirmação humana contextual, disco offline e revalidação local de fingerprint.
- Testes automáticos usam arquivos/discos sintéticos, nunca discos reais de desenvolvimento ou clientes.
- Não apresentar funcionalidade, ISO, teste físico ou homologação como concluída sem evidência.
- Nunca versionar certificados privados, tokens, dados de clientes, imagens ou estado operacional.
- Em TODO fim de tarefa: atualizar este arquivo (registro abaixo) e `contexto.md`, com mudanças, verificações, pendências e responsável. Agentes enviam seu resumo ao coordenador, que consolida ambos para evitar conflitos.

## Registro de tarefas
- 2026-10-26 / coordenador: especificação lida; workspace novo; .NET 10.0.403 disponível; GitHub CLI sem autenticação válida; conectores GitHub e Drive responderam. Contratos iniciais em `docs/04-protocol.md`.
