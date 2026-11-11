package main

import (
	"bufio"
	"context"
	"errors"
	"flag"
	"fmt"
	"os"
	"os/signal"
	"path/filepath"
	"strconv"
	"strings"
	"time"

	"github.com/mikerock12/slt-disk-manager/rescue-agent/internal/agent"
)

func chooseServer(servers []agent.ServerEndpoint) (agent.ServerEndpoint, bool) {
	fmt.Println("\nMais de um servidor SLT Disk Manager respondeu:")
	for i, s := range servers {
		fmt.Printf("  %d - %s (%s)\n", i+1, s.Name, s.URL)
	}
	fmt.Print("Digite o número do servidor desta bancada: ")
	line, _ := bufio.NewReader(os.Stdin).ReadString('\n')
	index, err := strconv.Atoi(strings.TrimSpace(line))
	if err != nil || index < 1 || index > len(servers) {
		return agent.ServerEndpoint{}, false
	}
	return servers[index-1], true
}

func main() {
	var options agent.Options
	var sltMode bool
	var discoveryPort int
	var discoveryAddress string
	flag.StringVar(&options.Server, "server", "", "Controlador HTTPS na LAN, ex.: https://192.168.1.10:7443 (vazio: procurar na rede)")
	flag.StringVar(&options.ServerPin, "server-pin", "", "SHA256 DER do certificado servidor (vazio: confirmar pelo código de pareamento)")
	flag.StringVar(&options.StateDir, "state-dir", "/var/lib/slt-rescue-agent", "Diretório privado para identidade e proteção de replay")
	flag.StringVar(&options.FixtureDir, "fixture-dir", "", "SOMENTE LAB: usa arquivos .raw deste diretório em vez de dispositivos físicos")
	flag.StringVar(&options.ScratchDir, "scratch-dir", "", "Diretório Linux persistente fora do destino, com espaço para preparar clone adaptado")
	flag.BoolVar(&options.AllowFileWrite, "allow-file-write", false, "SOMENTE LAB: permite restaurar arquivos .raw dentro de --fixture-dir")
	flag.BoolVar(&options.AllowPhysicalWrite, "allow-physical-write", false, "Permite pedir confirmação local adicional para sobrescrever disco físico offline")
	flag.BoolVar(&options.Once, "once", false, "Envia inventário e consulta uma tarefa, depois encerra")
	flag.DurationVar(&options.PollInterval, "poll-interval", 5*time.Second, "Intervalo entre consultas")
	flag.IntVar(&discoveryPort, "discovery-port", agent.DefaultDiscoveryPort, "Porta UDP da descoberta do servidor na rede local")
	flag.StringVar(&discoveryAddress, "discovery-address", "", "Consulta um IP da LAN em vez de broadcast (redes que bloqueiam broadcast)")
	flag.BoolVar(&sltMode, "slt-mode", false, "Modo SLT: lê servidor, pin e ticket da linha de comando do kernel (reinício pedido por um cliente Windows pareado)")
	flag.Parse()
	if options.ServerPin != "" && options.Server == "" {
		fmt.Fprintln(os.Stderr, "--server-pin exige --server.")
		os.Exit(2)
	}
	if options.FixtureDir != "" && options.AllowPhysicalWrite {
		fmt.Fprintln(os.Stderr, "Não combine modo fixture com permissão de gravação física.")
		os.Exit(2)
	}
	state, err := filepath.Abs(options.StateDir)
	if err != nil {
		fmt.Fprintln(os.Stderr, err)
		os.Exit(2)
	}
	options.StateDir = state
	ctx, cancel := signal.NotifyContext(context.Background(), os.Interrupt)
	defer cancel()
	if sltMode {
		data, err := os.ReadFile("/proc/cmdline")
		if err != nil {
			fmt.Fprintln(os.Stderr, "Modo SLT:", err)
			os.Exit(2)
		}
		boot, present, err := agent.ParseSltCommandLine(string(data))
		if !present || err != nil {
			fmt.Fprintln(os.Stderr, "Modo SLT: este boot não traz os parâmetros do servidor.", err)
			os.Exit(2)
		}
		// The pin came from the paired Windows client over mTLS, not from the network.
		options.Server, options.ServerPin, options.PinTyped, options.Ticket = boot.Server, boot.Pin, true, boot.Ticket
		options.AllowPhysicalWrite, options.ConfirmAtServer = boot.Write, boot.Write
	} else {
		options.Server, options.ServerPin, options.PinTyped, err = agent.LocateServer(ctx, options.Server, options.ServerPin, discoveryPort, discoveryAddress, os.Stdout, chooseServer)
		if err != nil {
			fmt.Fprintln(os.Stderr, "Rescue:", err)
			os.Exit(1)
		}
	}
	if err = agent.Run(ctx, options); err != nil && !errors.Is(err, context.Canceled) {
		fmt.Fprintln(os.Stderr, "Rescue:", err)
		os.Exit(1)
	}
}
