# Segurança

Relate problemas ao proprietário do projeto antes de publicar detalhes sensíveis.
Não anexe imagens, dados de clientes, tokens, chaves privadas ou diretórios state.

Fronteiras: NIC/CIDR explícitos, administração loopback+Bearer, TLS pinado, mTLS
por agente, revogação no banco, planos RSA com expiração, consentimento e
identidade revalidada antes da escrita. Nenhuma API de shell genérico.

A beta ainda precisa de revisão independente, testes de hardware e homologação.
Uma credencial administrativa local representa um operador; não há RBAC completo.
Dados brutos de imagens não são criptografados pelo app; use volume protegido.
Auditoria encadeada detecta alterações casuais, mas não protege contra admin local
que tenha todas as chaves. Servidor e dispositivos autorizados devem ser confiáveis.
