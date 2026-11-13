# Entrega 0.1.0-beta.8 — 13/11/2026

Pedido do proprietário:

- corrigir o erro do `Set-ServerRepository.ps1`;
- ícones próprios nos programas e no instalador;
- gerenciar os discos do servidor no Windows;
- máquinas virtuais no servidor;
- modo kamikaze;
- portal, release e backup.

Os testes que mexem em discos ou no boot rodaram numa VM Hyper-V separada. A máquina de
desenvolvimento, que também é o servidor real, não foi reiniciada.

## O que mudou

- **`Set-ServerRepository.ps1 -MoveExisting`** falhava com “The property 'Sum' cannot be
  found” quando o repositório ou os perfis de boot estavam vazios. Com `StrictMode`, o
  `Measure-Object` sobre uma pasta sem arquivos não devolve objeto. A soma passou a ser feita
  à mão. No servidor real o repositório antigo estava vazio, nada foi movido e o serviço
  continuou apontando para a pasta anterior. Basta repetir o comando com esta versão.
- **Ícones** (`scripts/generate-icons.py`, `deploy/branding/`) no painel, no servidor, no
  serviço de boot e no cliente. Também nos atalhos `.cmd`, no instalador e em
  Adicionar/Remover Programas.
- **Discos do servidor**, **Máquinas virtuais** e **Modo kamikaze**: veja o
  [ADR 005](ADR/005-servidor-local-kamikaze-e-vms.md) e o
  [guia de uso](QUICKSTART.md#discos-do-próprio-servidor).

## Verificações

| Suíte | Resultado |
|---|---|
| Controller (executor falso, discos em arquivo) | 277/277 |
| Go (agentes Rescue/WinPE, kamikaze com discos em arquivo), vet Windows/Linux | PASS |
| Desktop (inclui as três abas novas) | 71/71 |
| Windows agent | 67/67 |
| Network Boot | 113/113 |
| Scripts de serviço (PowerShell 5.1 e 7) | 37/37 nesta máquina (Install-Server pulado por ser o servidor real; roda no CI) |
| Discos do servidor, real, VHDX sintéticos **dentro da VM** | 17/17 |
| Hyper-V real (VMs descartáveis de 512 MB, geração 1 e 2) | 11/11 |

### Laboratório kamikaze (VM Hyper-V geração 2, Secure Boot ligado, sem rede)

- **VM:** disco diferencial de um Windows 10 UEFI do laboratório anterior (a imagem base não
  foi alterada) mais três discos virtuais de 1 GB, 1,5 GB e 2 GB. O WinRE da VM estava
  desativado, então o `Winre.wim` oficial foi extraído da ISO do Windows 10 do projeto.
- **Plano pela API real** do Controller rodando na VM: clonar 1 GB → 2 GB e formatar 1,5 GB
  em MBR/exFAT. A imagem foi preparada com o DISM real, o pacote foi assinado e foi criada a
  entrada de boot única.
- **Reinício:** o Windows Boot Manager carregou o WinRE em ramdisk com Secure Boot e o
  console kamikaze abriu, verificou o pacote e identificou os discos sem serial pela
  identidade. As frases `APAGAR …` foram digitadas no teclado virtual.
- **Resultado:** clone conferido por SHA256 com a GPT de reserva realocada; formatação MBR,
  exFAT, rótulo KAMIKAZE; “Todas as operações foram concluídas”.
- **Volta ao Windows:** o Controller coletou o resultado `COMPLETED` e removeu as entradas
  de boot (0 restantes). O arquivo de 64 MB do clone confere por SHA256 com a origem e a
  partição pode crescer até o fim do disco de 2 GB.
- **No Windows da própria VM:**
  - “Verificar disco” e “Reparar inicialização (BCDBoot)” concluíram; a VM voltou a iniciar
    depois do BCDBoot;
  - formatar o disco do sistema foi recusado com o aviso para usar o kamikaze;
  - o reparo do Windows (DISM + SFC) também foi executado (registro abaixo).

Bugs reais achados no laboratório e corrigidos:

- os discos virtuais não informam serial e o agente os ignorava;
- a função de log do script de discos misturava o texto ao código de saída das ferramentas,
  o que derrubava BCDBoot, MBR2GPT e chkdsk;
- “Verificar disco” falhava inteiro quando o Windows já estava verificando o volume;
- `Get-PhysicalDisk` deixava o inventário com 22 s; agora fica só na verificação.

## Limites

- Sem homologação em hardware físico.
- Kamikaze:
  - a instalação do Windows foi coberta por validação e testes com executor falso, não ponta
    a ponta em VM;
  - BIOS/Legacy não foi exercitado;
  - instalar Linux no próprio servidor não é oferecido.
- Pass-through de HD físico para a VM não exercitado.
- MBR2GPT e SFC/DISM offline só com executor falso.
- O Malwarebytes desta máquina classificou `SLT.Desktop.dll` como `MachineLearning/Anomalous`
  (heurística). O proprietário adicionou a exceção. Os binários continuam sem assinatura.
