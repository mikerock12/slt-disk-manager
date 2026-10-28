package main

import (
	"context"
	"errors"
	"flag"
	"fmt"
	"github.com/mikerock12/slt-disk-manager/rescue-agent/internal/agent"
	"os"
	"os/signal"
	"time"
)

func main() {
	var o agent.WinPEOptions
	flag.StringVar(&o.Server, "server", "", "URL HTTPS do controlador na LAN")
	flag.StringVar(&o.ServerPin, "server-pin", "", "Pin SHA256 do certificado conferido no servidor")
	flag.StringVar(&o.StateDir, "state-dir", `X:\SLT-State`, "Diretório privado de credenciais/ledger WINPE")
	flag.StringVar(&o.ScratchDir, "scratch-dir", "", "Diretório local saudável fora do destino para mídia e trabalho DISM")
	flag.StringVar(&o.BootsectPath, "bootsect", "", "bootsect.exe da mídia legítima, obrigatório para BIOS")
	flag.BoolVar(&o.AllowDeployment, "allow-deployment", false, "Permite plano DEPLOY_WINDOWS, ainda exige confirmação física local")
	flag.BoolVar(&o.Once, "once", false, "Um inventário e uma consulta de job")
	flag.DurationVar(&o.PollInterval, "poll-interval", 5*time.Second, "Intervalo de consulta")
	flag.Parse()
	if o.Server == "" || o.ServerPin == "" {
		flag.Usage()
		os.Exit(2)
	}
	ctx, cancel := signal.NotifyContext(context.Background(), os.Interrupt)
	defer cancel()
	if err := agent.RunWinPE(ctx, o); err != nil && !errors.Is(err, context.Canceled) {
		fmt.Fprintln(os.Stderr, "WinPE:", err)
		os.Exit(1)
	}
}
