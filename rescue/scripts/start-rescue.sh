#!/usr/bin/env bash
set -euo pipefail
umask 077

# Local keyboard launcher only. No eval, mounts, saved connection parameters,
# remote shell or automatically selected disk/job.
slt_require_root() { [[ $EUID -eq 0 ]]; }
slt_agent_available() { [[ -x /usr/local/bin/slt-rescue-agent ]]; }
slt_agent_exec() { exec /usr/local/bin/slt-rescue-agent "$@"; }
slt_prepare_network() {
  local choice
  command -v nmcli >/dev/null && command -v nmtui >/dev/null && command -v nm-online >/dev/null || {
    echo 'Ferramentas de rede ausentes; use a ISO SLT atual.' >&2; return 1;
  }
  while true; do
    printf '\nRede local — conecte o cabo Ethernet ou configure seu Wi-Fi.\n'
    nmcli --colors no --fields DEVICE,TYPE,STATE,CONNECTION device status || return 1
    printf '1 — Usar conexão atual / aguardar rede (padrão)\n2 — Abrir configuração de Wi-Fi/Ethernet\n0 — Sair\nEscolha [1]: '
    IFS= read -r choice || return 2
    case "${choice:-1}" in
      1)
        if nm-online --quiet --timeout=20; then return 0; fi
        printf 'Nenhuma conexão ativa em 20 segundos. Confira cabo, Wi-Fi e DHCP; tente novamente.\n'
        ;;
      2) nmtui || printf 'Configuração de rede encerrada sem conexão.\n';;
      0) return 2;;
      *) printf 'Escolha 1, 2 ou 0.\n';;
    esac
  done
}

slt_start_main() {
  [[ $# -eq 0 ]] || { echo 'Uso: sudo slt-start (sem argumentos)' >&2; return 2; }
  slt_require_root || { echo 'Abra o terminal local e execute: sudo slt-start' >&2; return 1; }
  slt_agent_available || { echo 'Agente SLT não encontrado na mídia.' >&2; return 1; }
  local server pin mode confirmation scratch canonical
  local -a arguments
  printf '\nSLT Rescue — conectar este cliente ao servidor da bancada\n'
  printf 'Este assistente não monta discos nem inicia captura ou restauração.\n'
  slt_prepare_network || return $?
  printf 'Endereço HTTPS do servidor (ex.: https://192.168.1.10:7443): '
  IFS= read -r server || return 2
  # The agent additionally resolves every address and enforces LAN-only dialing.
  [[ ${#server} -le 300 && "$server" =~ ^https://[A-Za-z0-9.-]+(:[0-9]{1,5})?/?$ ]] || {
    echo 'Endereço inválido. Use HTTPS, host/IP e porta, sem credenciais, caminho ou espaços.' >&2; return 2;
  }
  printf 'Pin SHA-256 conferido na tela do servidor (64 caracteres hexadecimais): '
  IFS= read -r pin || return 2
  pin="${pin//:/}"
  [[ "$pin" =~ ^[A-Fa-f0-9]{64}$ ]] || { echo 'Pin inválido; copie os 64 caracteres exibidos pelo servidor.' >&2; return 2; }
  printf '\n1 — Inventário, diagnóstico e captura autorizada (padrão; sem gravação física)\n'
  printf '2 — Permitir restauração física, somente com plano e confirmações\n'
  printf 'Modo [1]: '
  IFS= read -r mode || return 2
  mode="${mode:-1}"
  arguments=(--server "$server" --server-pin "$pin")
  case "$mode" in
    1) ;;
    2)
      printf 'Discos de destino podem ser apagados. A confirmação local de cada job continuará obrigatória.\n'
      printf 'Digite PERMITIR GRAVACAO para habilitar essa capacidade nesta sessão: '
      IFS= read -r confirmation || return 2
      [[ "$confirmation" == 'PERMITIR GRAVACAO' ]] || { echo 'Gravação não habilitada.' >&2; return 2; }
      printf 'Clone em lotes (padrão do painel) grava direto no destino e não usa pasta temporária.\n'
      printf 'Pasta temporária FORA do destino só para restaurar imagem completa com adaptação de tamanho.\n'
      printf 'Caminho absoluto, ou vazio para clone em lotes ou restauração exata: '
      IFS= read -r scratch || return 2
      arguments+=(--allow-physical-write)
      if [[ -n "$scratch" ]]; then
        [[ ${#scratch} -le 4096 && "$scratch" == /* && "$scratch" != *[$'\r\n\t']* && -d "$scratch" && ! -L "$scratch" ]] || {
          echo 'A pasta precisa existir e ter caminho absoluto sem links ou caracteres de controle.' >&2; return 2;
        }
        canonical="$(realpath -e -- "$scratch")" || return 2
        [[ "$canonical" == "$scratch" ]] || { echo 'Use o caminho real da pasta, sem links ou segmentos relativos.' >&2; return 2; }
        arguments+=(--scratch-dir "$scratch")
      else
        printf 'Sem pasta temporária: clone em lotes e instalação seguem disponíveis; restaurar imagem completa adaptando tamanho fica indisponível.\n'
      fi
      ;;
    *) echo 'Escolha 1 ou 2.' >&2; return 2;;
  esac
  printf '\nConectando. Confira o código no painel e aprove o pareamento deste cliente.\n'
  printf 'As tarefas dependem de plano assinado, autorização no servidor e verificações locais.\n'
  printf 'Ctrl+C encerra o agente. Esta sessão Live não preserva identidade após desligar.\n'
  slt_agent_exec "${arguments[@]}"
}

if [[ "${BASH_SOURCE[0]}" == "$0" ]]; then slt_start_main "$@"; fi
