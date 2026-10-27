# Perfil candidato Debian amd64 / UEFI

**Estado: roteiro local para homologação; instalação automática ainda não implementada.** A ISO SLT Live é ambiente de manutenção, não instalador homologado de sistema operacional.

Pré-requisitos: VM UEFI com disco descartável, ISO oficial Debian amd64 e SHA256/assinatura verificados. Não reutilizar senhas globais ou credenciais do servidor em imagens de instalação.

Fluxo a validar: registrar consentimento, serial/tamanho do destino e backup íntegro; iniciar o instalador Debian oficial; selecionar instalação e particionamento guiado após conferir o disco; usar esquema UEFI com ESP e filesystem suportado; concluir GRUB/initramfs pela distribuição; reiniciar sem a mídia e validar boot, UUIDs, armazenamento e conectividade. O Rescue não muda firmware ou monta partições do cliente em escrita por esse roteiro.

Para implantação por imagem RAW de uma instalação preparada, usar apenas o job RESTORE com disco offline, confirmação no servidor e no teclado do cliente; restaurar a imagem não comprova boot. O layout não é ampliado automaticamente em disco maior. UUIDs/GUIDs clonados requerem procedimento específico antes de conectar origem e clone simultaneamente.

Aceite obrigatório: boot UEFI, reboot e desligamento, filesystem consistente, rede, mapa GPT, ambiente de recuperação, teste de imagem corrompida e plano de rollback. Registrar versão da distribuição/kernel e resultados em matriz de hardware. Sem esse aceite, manter este perfil como candidato.

Referências primárias: [instalação Debian](https://www.debian.org/releases/stable/amd64/), [manual Debian Live](https://live-team.pages.debian.net/live-manual/html/live-manual/the-basics.en.html).
