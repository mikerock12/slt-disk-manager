# Changelog

## Não publicado — clone em lotes

Clone cliente → cliente em lotes, agora padrão no painel: origem e destino ficam ligados
ao mesmo tempo, o servidor guarda no máximo 256 MiB (ajustável) e apaga cada lote
depois que o destino confirma a gravação. Clonar discos de 1 TB não exige 1 TB livre
no servidor nem pasta temporária no Rescue. Vale para EXATO, PRESERVAR e AJUSTAR para
disco igual ou maior; o ajuste acontece no próprio destino depois de reler e comparar
todos os dados. Reduzir para disco menor e repetir restaurações continuam no modo com
imagem completa, mantido como opção. Novo agente Rescue necessário nos dois clientes.

## 0.1.0-beta.3.3 — 2026-10-28

Corrige a implantação Windows pelo WinPE: o script DiskPart executava
`online disk` sempre, e o DiskPart abortava (0x80042595) em disco já online,
o caso comum, antes de particionar. Agora o comando só é usado quando o disco
está offline. Em VM BIOS/MBR sintética, DiskPart, DISM, BCDBoot e bootsect
concluíram e o Windows iniciou pelo destino; OOBE, UEFI/GPT e hardware ainda
não foram comprovados. Não use a implantação Windows da beta.3.2.


## 0.1.0-beta.3.2 — 2026-10-27

Distribuição com instalador EXE por componentes e ISO Rescue BIOS/UEFI com
firmware Ethernet/Wi-Fi, NetworkManager e assistente de conexão/pareamento.
Corrige exemplos do WinPE para os caminhos do pacote instalado e documenta
montagem do armazenamento temporário separado para clone adaptável.
Mantém os motores e testes da beta.3.1; documentos públicos omitem referências
de backups privados. A beta.3.1 já publicada permanece preservada.


## 0.1.0-beta.3 — 2026-10-27

Clone integral cliente→servidor→cliente com GPT/MBR e ajuste NTFS/ext4 em cópia
de trabalho; motores Linux testados em discos sintéticos menores/maiores.
Gerenciador de arquivos com raízes autorizadas, backup e envio bidirecional,
importação local e proteção contra links/substituição de destino existente.
Catálogo ISO/WIM/ESD e agente Go WinPE com plano assinado, confirmação local,
DISM/BCDBoot e execução paralela entre máquinas distintas.

Windows10 ISO local lida com quatro edições AMD64; instalação/primeiro boot
Windows e hardware permanecem sem homologação. Nova proteção na limpeza de
testes evita erro de aplicação por arquivo temporário bloqueado.


## 0.1.0-beta.2 — 2026-10-26

Primeira beta local: Controller, Desktop, Windows client e Rescue Go; TLS/mTLS,
inventário, SMART, planos assinados, imagens RAW e auditoria. Testes em arquivos
sintéticos e inventário Windows real somente leitura.

Correções de integração: containers de chave compatíveis com Windows SChannel,
lock de instância antes de abrir o banco, bloqueio de verificação concorrente a
restore, ACLs do instalador e readiness do launcher. Sem homologação de boot ou
operações destrutivas em hardware. Consulte docs/STATUS.md para pendências.
