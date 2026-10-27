# Changelog

## 0.1.0-beta.2 — 2026-10-26

Primeira beta local: Controller, Desktop, Windows client e Rescue Go; TLS/mTLS,
inventário, SMART, planos assinados, imagens RAW e auditoria. Testes em arquivos
sintéticos e inventário Windows real somente leitura.

Correções de integração: containers de chave compatíveis com Windows SChannel,
lock de instância antes de abrir o banco, bloqueio de verificação concorrente a
restore, ACLs do instalador e readiness do launcher. Sem homologação de boot ou
operações destrutivas em hardware. Consulte docs/STATUS.md para pendências.
