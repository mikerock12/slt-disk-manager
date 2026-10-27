package main

import (
	"context"
	"errors"
	"flag"
	"fmt"
	"os"
	"os/signal"
	"path/filepath"
	"time"

	"github.com/mikerock12/slt-disk-manager/rescue-agent/internal/agent"
)

func main() {
	var options agent.Options
	flag.StringVar(&options.Server, "server", "", "Controlador HTTPS na LAN, ex.: https://192.168.1.10:7443")
	flag.StringVar(&options.ServerPin, "server-pin", "", "SHA256 DER do certificado servidor, conferido localmente")
	flag.StringVar(&options.StateDir, "state-dir", "/var/lib/slt-rescue-agent", "Diretório privado para identidade e proteção de replay")
	flag.StringVar(&options.FixtureDir, "fixture-dir", "", "SOMENTE LAB: usa arquivos .raw deste diretório em vez de dispositivos físicos")
	flag.StringVar(&options.ScratchDir, "scratch-dir", "", "Diretório Linux persistente fora do destino, com espaço para preparar clone adaptado")
	flag.BoolVar(&options.AllowFileWrite, "allow-file-write", false, "SOMENTE LAB: permite restaurar arquivos .raw dentro de --fixture-dir")
	flag.BoolVar(&options.AllowPhysicalWrite, "allow-physical-write", false, "Permite pedir confirmação local adicional para sobrescrever disco físico offline")
	flag.BoolVar(&options.Once, "once", false, "Envia inventário e consulta uma tarefa, depois encerra")
	flag.DurationVar(&options.PollInterval, "poll-interval", 5*time.Second, "Intervalo entre consultas")
	flag.Parse()
	if options.Server == "" || options.ServerPin == "" {
		flag.Usage()
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
	if err = agent.Run(ctx, options); err != nil && !errors.Is(err, context.Canceled) {
		fmt.Fprintln(os.Stderr, "Rescue:", err)
		os.Exit(1)
	}
}
