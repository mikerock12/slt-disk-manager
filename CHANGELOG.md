# Changelog

## 0.1.0-beta.5 — 2026-10-29 — instalação Linux, primeiro boot Windows, redução em lotes e MBR→GPT

- **Instalação Ubuntu/Xubuntu pelo Rescue (UEFI/GPT):** importe a ISO oficial no servidor;
  o cliente em SLT Rescue particiona, extrai o sistema da própria ISO, instala GRUB/shim,
  cria o usuário com senha temporária gerada pelo servidor (exibida uma vez) e exige a
  troca no primeiro login. Instalação Linux e Windows em clientes diferentes seguem ao
  mesmo tempo.
- **Windows sem perguntas no primeiro boot:** a implantação WinPE grava `unattend.xml` com
  nome do computador, conta administradora local com senha temporária, idioma, fuso
  e telas de OOBE puladas; hibernação desligada para permitir clone seguro.
- **Clone em lotes para disco menor:** AJUSTAR para destino menor agora funciona sem
  imagem completa. O destino grava direto no disco; os blocos que passam do novo tamanho
  ficam em RAM (só os não nulos, até 1 GiB) até o NTFS/ext4 ser reduzido no próprio destino.
  Lotes só de zeros não trafegam.
- **Converter MBR para GPT/UEFI:** nova tarefa no WinPE usando o MBR2GPT.EXE da mídia
  Windows importada pelo operador (hash amarrado ao plano assinado), com validação antes,
  confirmação local e conferência do layout depois.
- **Instalador completo:** o pacote traz 7-Zip para ler ISOs no servidor e smartctl 7.5
  para SMART no cliente; runtime .NET e C++ continuam embutidos. Servidor e cliente podem
  ser instalados como serviço do Windows pelo próprio instalador (com aviso e UAC).
- **Robustez de rede:** download de lotes e relatórios de progresso tentam de novo em
  quedas de conexão, em vez de abortar instalações longas.
- **Relógio local:** certificados e planos toleram computadores com o relógio em horário
  local (comum em dual boot com Windows).
- Painel mostra quando o cliente aguarda confirmação no teclado local.

## 0.1.0-beta.4 — 2026-10-28 — clone em lotes

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
