# Perfil candidato Windows x64 / UEFI

**Estado: roteiro local para homologação, sem executor remoto e sem imagem Microsoft redistribuída.** O software não declara uma instalação Windows concluída por mostrar este roteiro.

Pré-requisitos: VM UEFI descartável, licença e mídia obtidas legitimamente, Windows PE/Setup suportado, drivers de rede/armazenamento necessários e chave BitLocker preservada quando aplicável. A mídia Linux SLT não contém DISM, Windows PE, BCDBoot, chave de produto ou bypass de ativação.

Fluxo a validar: identificar o destino por serial/capacidade e registrar consentimento; efetuar backup e validar imagem; inicializar mídia Windows autorizada; no Setup selecionar instalação personalizada e conferir o disco novamente; usar o particionamento do instalador para UEFI/GPT; instalar edição licenciada; validar boot sem USB, armazenamento, rede, drivers e ativação apropriada.

Para imagem corporativa previamente generalizada com Sysprep, preparar em WinPE a aplicação WIM por DISM e a criação de boot por BCDBoot seguindo a documentação oficial. Não há script SLT que execute diskpart/format/bcdboot: esse perfil precisa de revisão de destino e homologação antes de ganhar um job assinado.

Aceite obrigatório: primeiro boot em VM limpa, teste de reboot, logs Setup/DISM, hash da mídia, edição/versão e modo de firmware registrados, recuperação em falha simulada e nenhum segredo em relatório. Depois, testar somente hardware descartável com consentimento.

Referências primárias: [WinPE](https://learn.microsoft.com/windows-hardware/manufacture/desktop/winpe-intro), [DISM](https://learn.microsoft.com/windows-hardware/manufacture/desktop/dism-image-management-command-line-options-s14), [BCDBoot](https://learn.microsoft.com/windows-hardware/manufacture/desktop/bcdboot-command-line-options-techref-di), [Sysprep](https://learn.microsoft.com/windows-hardware/manufacture/desktop/sysprep--system-preparation--overview).
