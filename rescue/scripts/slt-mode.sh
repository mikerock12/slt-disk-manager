#!/usr/bin/env bash
set -uo pipefail
umask 077

# Started by slt-mode.service on tty1 when the kernel command line carries slt.ticket=...
# (a paired Windows client asked to restart this PC in SLT mode). No argument is taken from
# the network here: the agent itself reads and validates /proc/cmdline.
slt_mode_banner() {
  printf '\033[2J\033[H'
  printf '  ============================================================\n'
  printf '   SLT DISK MANAGER  -  MODO SLT\n'
  printf '  ------------------------------------------------------------\n'
  printf '   Este computador foi reiniciado pelo cliente SLT e esta sob\n'
  printf '   controle do servidor da assistencia.\n\n'
  if grep -qw 'slt.write=1' /proc/cmdline; then
    printf '   GRAVACAO HABILITADA: clonar, formatar e instalar sistema\n'
    printf '   sao executados quando autorizados no painel do servidor,\n'
    printf '   sem nova confirmacao neste teclado.\n\n'
  else
    printf '   Somente leitura: inventario, diagnostico e captura.\n\n'
  fi
  printf '   Para sair do modo SLT: desligue ou reinicie o computador\n'
  printf '   (Ctrl+Alt+Del). O Windows instalado volta a iniciar.\n'
  printf '  ============================================================\n\n'
}

slt_mode_main() {
  [[ $EUID -eq 0 ]] || { echo 'slt-mode exige root (servico do sistema).' >&2; return 1; }
  grep -qw 'slt\.ticket=[0-9a-f]\{64\}' /proc/cmdline || { echo 'Este boot nao e um modo SLT.' >&2; return 0; }
  slt_mode_banner
  # live-boot already used the wired network to fetch the system; give NetworkManager a moment to take over.
  local attempt
  for attempt in $(seq 1 24); do
    if nm-online --quiet --timeout=5 2>/dev/null || ip -4 route show default 2>/dev/null | grep -q .; then break; fi
    printf '   Aguardando a rede...\n'
  done
  exec /usr/local/bin/slt-rescue-agent --slt-mode
}

if [[ "${BASH_SOURCE[0]}" == "$0" ]]; then slt_mode_main "$@"; fi
