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
- 2026-10-26 / Windows agent (consolidado): client/instaladores implementados; 24 checks de segurança; E2E real 4/4 (TLS, CSR/mTLS, INVENTORY assinado, revogação); correção SChannel validada. Inventário local em leitura viu dois discos; não é homologação de hardware.
- 2026-10-26 / Rescue (consolidado): Go stdlib, inventário/SMART/capture/restore, ledger, hashes e guards locais; testes e vet passaram, builds Windows fixture/Linux; scripts ddrescue/Partclone candidatos, perfis de implantação manuais, ISO não construída.
- 2026-10-26 / coordenador: Controller/SQLite/painel integrados; suite Controller 34/34 e E2E real Go↔.NET 5/5 com arquivos sintéticos; SQLite nativo corrigido para 2.1.12; build solução 0 warnings/erros. Matriz `docs/STATUS.md` registra módulos ainda pendentes; não confundir beta com produto integral.
- 2026-10-26 / Desktop (consolidado): WPF compilado sem erros/avisos, contratos revisados (audit ID long), timeout60s, VERIFYING informado corretamente, consentimento/challenge sem autofill/Enter. QA visual não concluída: ferramenta de lançamento travou e foi interrompida; sem screenshot nem processo aberto.
- 2026-10-26 / revisão Windows/coordenador: corrigidos lock exclusivo antes de abrir DB, verificação concorrente a restore, ACL/reparse/root no instalador, nome do serviço e readiness portátil. Controller agora35/35; E2E Go agora6/6; NuGetAudit com advisories como erro passou. Pacote final beta.2 em geração; Drive pasta criada/verificada. GitHub ainda depende de login.
- 2026-10-26 / entrega/backup: commit implementação 9a65e66; ZIP beta.2 self-contained 173.358.572B, SHA256 b9e822afd2de867fad091afe5425c355cea3ae77b1248233c26cd2e11dccb04f. Fonte/Git bundle enviados ao Drive; pacote em 3 partes devido ao limite 100MiB, reunião testada com hash idêntico e uploads conferidos por listagem/tamanho. Relatório docs/ENTREGA-2026-10-26.md. GitHub público/pin/push/PR/CI remoto/merge pendentes de autenticação. Escopo integral e homologação continuam pendentes, listados em STATUS.
- 2026-10-26 / correção de erro visível: EventLog .NET Runtime 1026 de 19:00:31 confirmou IOException sem tratamento na limpeza de controller.db de uma execução antiga. Pooling já estava desativado; finalmente de testes agora usa TestCleanup com validação do diretório exclusivo, tentativas limitadas e aviso/preservação se arquivo permanecer bloqueado. Regressões de lock e liberação passaram; Controller 37/37, exit 0. Nenhum processo/janela de teste ativo na inspeção atual. Alteração só em testes/documentação; pacote beta.2 inalterado. Usuário informou que fará gh auth login; autenticação ainda inválida na última consulta.
- 2026-10-26 / reconexão GitHub: plugin confirmado funcional na conta mikerock12; busca não encontrou slt-disk-manager. Não confundir credencial do plugin com gh: CLI ainda inválido. Ferramentas atuais do plugin não incluem criar repositório ou fixar perfil; fallback navegador abriu login, deixado disponível ao usuário. Commit da correção 0c6df63; backup Drive atualizado e metadados confirmam 308.365 bytes no arquivo de fontes existente.
