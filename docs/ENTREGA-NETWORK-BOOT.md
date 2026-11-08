# Network Boot P0–P2 — checkpoint de implementação

05/11/2026. Primeira entrega incremental do mapa: processo opt-in isolado,
DHCP externo sem leases, TFTP bootstrap, HTTP de artefatos, catálogo de boot
separado e painel WPF administrativo local. USB/ISO, mTLS, clone, arquivos e
confirmações de disco preservados.

Perfis importados não podem se declarar homologados: somente fingerprints
aprovados pelo laboratório e incluídos no catálogo instalado da mesma release
recebem NETWORK_BOOT_LAB. Perfis DEV bloqueiam o início pelo painel.

Validação local: Network Boot 88/88; API administrativa HTTPS 11/11; Desktop 45/45;
exportador 10/10; diagnóstico/login serial 8/8; observador de erro visual 17/17.
A prova interativa QEMU confirmou
usuário, sudo, saída real do comando e disco sintético readonly intacto.

O CI anterior passou Windows/Linux, auditoria, pacote e smoke dos instaladores.
CI41964719717 passou BIOS completo e o positivo UEFI, incluindo um novo INVENTORY
assinado após encerrar HTTP/TFTP. O negativo UEFI reprovou pela ausência de mensagem
no serial; os dados brutos mostram perda de caracteres no console EFI. Diagnóstico
isolado confirmou a mensagem na tela VGA. O novo observador exige a frase completa
no serial ou em dois quadros VGA separados por pelo menos 0,3 s, com confiança OCR
média de pelo menos 80 e evidências preservadas. Somente a ambiguidade óptica
`;`/`:` no separador fixo da frase é normalizada, mantendo o reconhecimento bruto.
O replay dos quadros reais qualificou as três tentativas (88,24%); os resultados
originais reprovados não foram alterados. O gate completo será repetido no PR13.

WinPE PXE, Windows sem scratch, ProxyDHCP, Secure Boot e hardware físico ficam
fora desta entrega. O portal tem PR55 em rascunho, com validação local e QA
responsivo; release, merge/deploy e backup final serão registrados após os gates
e a conferência dos arquivos reais. Registros internos completos permanecem
privados; não publicar referências privadas de backup. Responsável: coordenador.
