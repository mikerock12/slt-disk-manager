# Network Boot P0–P2 — checkpoint de implementação

05/11/2026. Primeira entrega incremental do mapa: processo opt-in isolado,
DHCP externo sem leases, TFTP bootstrap, HTTP de artefatos, catálogo de boot
separado e painel WPF administrativo local. USB/ISO, mTLS, clone, arquivos e
confirmações de disco preservados.

Perfis importados não podem se declarar homologados: somente fingerprints
aprovados pelo laboratório e incluídos no catálogo instalado da mesma release
recebem NETWORK_BOOT_LAB. Perfis DEV bloqueiam o início pelo painel.

Validação local: Network Boot88/88; API administrativa HTTPS11/11; Desktop45/45;
exportador10/10; diagnóstico/login serial8/8. A prova interativa QEMU confirmou
usuário, sudo, saída real do comando e disco sintético readonly intacto.

O CI anterior passou Windows/Linux, auditoria, pacote e smoke dos instaladores.
CI41960628537 passou o fluxo BIOS completo: boot, login, CSR/mTLS/INVENTORY,
duas falhas de rede e fallback Linux local, com hashes intactos. UEFI chegou ao
menu, mas a saída serial de dois consoles duplicou caracteres e o leitor expirou.
Foi mantido console EFI nativo e UART explícito só no BIOS; o gate completo será
repetido no PR13 antes de publicar.

WinPE PXE, Windows sem scratch, ProxyDHCP, Secure Boot e hardware físico ficam
fora desta entrega. O portal tem PR55 em rascunho, com validação local e QA
responsivo; release, merge/deploy e backup final serão registrados após os gates
e a conferência dos arquivos reais. Registros internos completos permanecem
privados; não publicar referências privadas de backup. Responsável: coordenador.
