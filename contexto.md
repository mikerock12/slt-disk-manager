# Contexto persistente

Atualizado em 2026-10-27. Projeto baseado na especificação técnica 1.0.0. Registros abaixo são históricos; o checkpoint mais recente prevalece.

## Checkpoint atual — 2026-10-27
- Backend/UI implementam clone integral entre clientes via imagem validada, adaptação GPT/MBR NTFS/ext4, arquivos com escopos bidirecionais, importação local no servidor e catálogo ISO/WIM/ESD + planos WinPE. Locks por máquina permitem trabalho simultâneo em PCs distintos. Transferências usam tabela de chunks normalizada; RESTORE sem estratégia explícita agora exige mesma capacidade.
- Controller97/97 (inclui clone, arquivos, importação, mídia e concorrência em3clientes); Windows42/42; arquivos mTLS real5/5; Desktop26/26 e renderizações XAML sintéticas revisadas. Go WinPE testa execução mock, confirmação, troca de disco, hashes/corrupção, cancelamento e falhas. Vet/cross-build Windows/Linux e kit PowerShell passaram. Não houve DiskPart/DISM real.
- PR2 https://github.com/mikerock12/slt-disk-manager/pull/2. Checkpoint remoto20e900a689b517fb2fd38c62b4d6dc9eae9513a0 validou Linux real CI40455586685: shrink/grow GPT/MBR NTFS/ext4 preservando boot/WinRE e sentinelas OS/dados, source imutável, GPT backup, corrupção/hibernação bloqueadas, loop sintético e VHDX. Windows do checkpoint também executa CI. Próxima publicação inclui integração completa, WinPE e workflowISO.
- Mídia fornecida pelo usuário ISOs/Windows10.iso: install.esd3.981.914.908B, SHA256507466d70b5e48e13ef9664cb9e912a61c7cbf3fabb57a0684b7c805173a36ab,4edições Home/HomeSingleLanguage/Education/Pro AMD64 versão10.0.19041.3803. Importação por API local e readback/hash passaram duas vezes; ISO original não alterada nem versionada. Catálogos de teste estão em artifacts/media-e2e-* (privados/ignorados). Instalação e primeiro boot não testados.
- ISO Rescue ainda em preparação: novo workflow PR constrói Debianlive em container e smokeQEMU BIOS/UEFI sem rede/discos de dados. Não declarar ISO construída/bootada antes de evidência. Restam CI completo, pacote beta.3, merge PR2, backupDrive atualizado e homologação Windows/hardware. docs/WORKFLOWS.md orienta operação; STATUS mantém módulos integrais pendentes.

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

## Entregas de implementação verificadas
- Controller ASP.NET Core/SQLite com CA, pin TLS, mTLS, revogação, NIC/CIDR privado, admin loopback, jobs assinados/consentimento/challenge e imagens RAW.
- Windows client e scripts de serviço: build, 24 checks segurança, E2E real 4/4, leitura de dois discos locais. SChannel exige reimportar chaves para container Windows no servidor e client (corrigido).
- Rescue Go: testes/vet e cross-build; E2E real com .NET 5/5, captura e restauração byte a byte de arquivo de 4 MiB+512 bytes, revogação. Não usou discos reais.
- Painel WPF implementado, compilado; revisão visual/funcional final em andamento.
- Controller 34/34 verificações sintéticas; solução inteira compilou com 0 avisos/0 erros. Dependência SQLitePCLRaw.lib.e_sqlite3 2.1.12 fixa advisory detectado na 2.1.11.
- Transporte JSON polling, persistência documentos SQLite, single admin token: desvios da visão-alvo explícitos em ADR/STATUS, sem declarar gRPC/EF/RBAC completo.
- Ainda pendentes: ISO e boot homologados, reboot remoto, arquivos/partições remotos, instalação automatizada de SO, PXE, clone direto/lote, MSI assinado, RBAC e validação em VMs/hardware.
- Git inicializado. main baseline a55c905; implementação na branch feat/workshop-client-server. Nenhum remote/PR/merge remoto criado até autenticação GitHub válida.

## Fechamento de integração / revisão
- Controller35/35, Windows24/24, Go5 testes+vet, WindowsE2E4/4 e GoE2E6/6. Segunda instância no mesmo state é recusada antes de abrir o banco. Verificação não interrompe restore ativo. Instalação SYSTEM restringe executáveis a pasta dedicada de Program Files e ACL Administradores/SYSTEM; rejeita reparse/root.
- Desktop sem erros/avisos; auditoria ID long e mensagem VERIFYING corrigidos. QA visual permanece pendente: lançamento pela ferramenta de UI travou até interrupção, sem janela/screenshot.
- Pacote self-contained beta.2 em artifacts, com runtime, scripts e guia. Build ISO disponível em workflow manual, mas WSL não está instalado operacionalmente e não há Docker aqui; ISO/boot não testados.
- Destino Drive criado e verificado: https://drive.google.com/drive/folders/10F1ZWzqpRpwDYd3y-TVECFxa2MTUu5Wa . Upload/recibos serão registrados no fechamento de entrega.
- Autenticação GitHub bloqueante: gh token inválido, navegador GitHub/Google sem sessão. Usuário recebeu solicitação de login enquanto implementação continuava; não criar PR/merge/destaque fictícios.

## Artefatos e continuidade
- Commit implementação 9a65e66 em feat/workshop-client-server. Main ainda é baseline; sem remote nem merge fictício. Última alteração documental registra recibos e helpers de backup.
- Pacote beta.2 gerado: artifacts/SLT-Disk-Manager-0.1.0-beta.2-win-x64.zip (173.358.572 bytes), SHA256 b9e822afd2de867fad091afe5425c355cea3ae77b1248233c26cd2e11dccb04f.
- Backup fonte/histórico: https://drive.google.com/file/d/1SnQiWVMYd-O0WK6grrkKkgFKAZz9AlEI/view?usp=drivesdk . Upload verificado por metadados.
- Binários no mesmo destino Drive, em 3 partes por limite 100MiB do conector. Partes com 64MiB, 64MiB e 39.140.844 bytes. package-manifest.json e Reunir-Pacote.ps1/.cmd incluídos; reunião local produziu ZIP com hash idêntico. Listagem Drive confirmou 8 arquivos iniciais e tamanhos; sem compartilhamento público.
- Próxima retomada GitHub: conferir gh auth status/login e existência de mikerock12/slt-disk-manager antes de criar; publicar main baseline+branch implementação, abrir PR, anexar à conversa, aguardar CI, corrigir falhas, merge, fixar repo no perfil e atualizar backup. Não marcar isso como feito antes dos retornos remotos.
- Homologação/escopo faltante em docs/STATUS.md. Inventário/diagnóstico é o primeiro uso indicado nos dois computadores; escrita física continua sem homologação. Pacote tem QUICKSTART.md na raiz; documentação completa atualizada está no source backup/workspace.

## Correção da janela SLT.Controller.Tests.exe
- Usuário enviou screenshot de erro de aplicação. EventLog Application/.NET Runtime 1026 de 19:00:31 apontou IOException sem tratamento no Directory.Delete da limpeza de controller.db em um teste antigo. Não é evidência de falha do servidor/client beta.2.
- SQLite Pooling=False e catch do corpo do teste já estavam presentes, mas finally ainda podia lançar. TestCleanup agora valida o diretório temporário exclusivo, tenta limpar até 3 vezes e registra aviso/preserva a pasta em caso de lock/permissão, sem mascarar o resultado da suíte.
- Teste reproduziu arquivo bloqueado e liberação do handle: 37/37 checks, exit 0. Nenhum processo SLT.Controller.Tests/WerFault nem janela correspondente encontrado na inspeção atual. Não houve necessidade de encerrar processos do usuário.
- Somente testes/documentação alterados. Pacote executável beta.2 permanece com o mesmo hash. Backup das fontes será atualizado mantendo o ID Drive já registrado.
- Usuário respondeu que autenticará via gh auth login. Não presumir login concluído: última consulta ainda informou token inválido.
- Após reconexão feita pelo usuário, get_profile do plugin confirmou mikerock12 e search_repositories não encontrou o projeto. O plugin está conectado e funcional; faltam operações de criar repo e pin nos tools disponíveis. CLI e navegador têm autenticação separada: gh continua inválido; nova aba https://github.com/new abriu login, marcada para handoff. Aguardar login nessa aba OU gh auth login, sem pedir nova reconexão do plugin.
- Correção commitada em 0c6df63 e source backup substituído no mesmo ID Drive; readback confirmou tamanho 308.365 bytes. Binários beta.2 não mudaram, pois correção é do harness de testes.

## Publicação resolvida e nova prioridade
- Login navegador confirmado pelo usuário. Repo público https://github.com/mikerock12/slt-disk-manager criado e fixado no perfil. Publicação via plugin (CLI continua sem token); snapshot local 222e638 tem árvore idêntica ao commit remoto60c42fe3.
- PR1 https://github.com/mikerock12/slt-disk-manager/pull/1 anexado e mesclado após CI40426098422 SUCCESS: Windows build/testes/auditoria/pacote e Linux race/vet/scripts. Merge c5dfc79493c91b06b33fce2c7f512bf06cd4a44e.
- Histórico original preservado em archive/local-baseline e archive/local-workshop-history. main agora acompanha origin/main; trabalho atual feat/adaptive-clone-files.
- Usuário exige principalmente clone de disco INTEIRO cliente1→cliente2, todaspartições GPT/MBR, boot/OS/dados, adaptando capacidade, e gerenciador de arquivos para backups específicos e envios aos clientes. Também quer Windows10 no cliente3 em paralelo a clone entre1/2. Não confundir clone via servidor com requisito de stream direto: fluxo A→imagem validada→B atende seleção de origem/destino.
- Pergunta assíncrona pendente: caminho local da ISO/WIM/ESD Windows10 autorizada. Implementar infraestrutura de deployment enquanto aguarda, mas não inventar teste de instalação/boot sem mídia.
- Contrato de implementação next em docs/05-clone-files-contract.md. Agents analisaram sem editar e serão liberados após este registro. Adaptação modifica só cópia de trabalho; nunca reduzir filesystem de origem. WinPE para instalação Windows; Linux Rescue para clone físico offline.
- Implementação em andamento: Windows FILE_* (41 checks) e WPF clone/arquivos (18 checks+5 PNGs sintéticos artifacts/ui). Backend arquivos/clones compilou, testes Controller antigos 37/37; E2Efiles Windows em17445 após correção paths:null→[]. Não recompilar Debug enquanto esse teste estiver ativo; Release disponível.
- Rescue adaptável implementado com8 testes locais/vet e script test-adaptive-linux.sh; CI real Linux precisa apt fdisk util-linux ntfs-3g e2fsprogs dosfstools qemu-utils. Exige scratch saudável e valida cópia inteira antes do destino; original imutável. FIT preserva partições posteriores WinRE/OEM. Agent Desktop implementará novos winpe_*.go e bootstrapUSB; Agent Rescue mantém helpers EnrollMode/Plan.Deployment/VerifyAssignment.
- Mídia fornecida: D:/PROJETOS/SLTDiskManager/ISOs/Windows 10.iso (4.727.242.752 bytes), sources/install.esd 3.981.914.908 e boot.wim. 7z instalado; host sem UAC elevado/ADK. Não enviar mídia a GitHub/CI/backup fontes. Contrato deployment em docs/06-windows-deployment-contract.md.
