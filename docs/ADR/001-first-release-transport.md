# ADR — transporte e persistência da beta

Decisão em 2026-10-26: a primeira implementação usa HTTPS JSON v1 e polling de
saída, preservando TLS fixado, mTLS, jobs tipados assinados e autorização por job
nas transferências. O gRPC/protobuf proposto permanece evolução, não dependência
fictícia. Chunks binários têm tamanho limitado e hash SHA256. A comunicação LAN
não exige conta GitHub/Drive nem internet após instalação.

SQLite usa uma conexão local serializada, WAL e `synchronous=FULL`. Documentos
JSON versionados por tipo/ID permitem manter os contratos pequenos nesta beta;
o esquema relacional EF Core granular da especificação permanece evolução. A
versão inicial de esquema é `user_version=1`; não há migração destrutiva implícita.

Os recibos de chunk ocorrem após flush e releitura do hash. A verificação profunda
do catálogo roda fora do lock do banco para não suspender heartbeats em imagens
grandes. Reinícios interrompem jobs e imagens incompletas, sem replay automático.

Referências de implementação: [Kestrel e certificados](https://learn.microsoft.com/aspnet/core/security/authentication/certauth?view=aspnetcore-10.0),
[política .NET](https://dotnet.microsoft.com/platform/support/policy/dotnet-core).
