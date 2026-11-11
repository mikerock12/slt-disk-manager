# ADR 003 — Descoberta na rede e pareamento por código curto

Data: 2026-11-11. Base: beta.6 (`c83855c`).

## Problema

Para conectar um cliente era preciso digitar no computador atendido o endereço do
servidor e o pin SHA-256 do certificado, com 64 caracteres. Na bancada isso é lento e
sujeito a erro, e o pin precisa ser lido em outra tela.

## Decisão

1. **Descoberta.** O servidor responde, na porta UDP 7444, a uma pergunta de descoberta
   enviada em broadcast na sub-rede autorizada. A resposta traz nome, endereço HTTPS e
   pin do certificado. Ela não contém segredo e **não concede confiança**: só diz onde
   um servidor afirma estar. O cliente descarta respostas cujo endereço anunciado não
   seja o endereço de origem do pacote, endereços fora de redes privadas e respostas a
   perguntas que ele não fez (nonce).
2. **Código curto.** A confiança vem de um código de 8 dígitos que o cliente mostra e o
   operador digita no painel. O código é derivado de:
   - o pin do certificado que **o cliente realmente viu** na conexão TLS;
   - o hash do CSR que **o servidor realmente recebeu**;
   - um nonce do cliente e um nonce do servidor.

   O cliente envia primeiro o compromisso (`SHA-256` do seu nonce), recebe o nonce do
   servidor e só então revela o seu. Assim nenhum dos lados escolhe o próprio nonce
   depois de conhecer o do outro.

   `código = primeiros 8 bytes de SHA-256("SLT-PAIR-2\n" pin "\n" SHA-256(CSR) "\n"
   nonceCliente "\n" nonceServidor "\n")`, módulo 10⁸, com zeros à esquerda.
3. **O painel não mostra o código.** Na listagem administrativa o campo vem vazio para
   pedidos por código curto. O operador precisa ler o código na tela do cliente; copiar
   de uma tela para a outra no próprio servidor não confirmaria nada.
4. Cinco códigos errados rejeitam o pedido. O pedido vale dez minutos.
5. Depois de pareado, o cliente Windows guarda o endereço e o pin confirmados no seu
   diretório privado e não precisa mais de parâmetros. Se o servidor mudar de endereço
   mantendo o certificado, o cliente o reencontra pela descoberta e aceita apenas o
   mesmo pin.

## Por que isso resiste a um servidor falso na rede

Um computador que responda à descoberta no lugar do servidor apresenta outro
certificado. O cliente calcula o código com o pin desse certificado; o servidor
verdadeiro, se o atacante repassar o pedido, calcula com o seu. Os códigos diferem e a
aprovação é recusada. Para acertar por força bruta o atacante teria de fixar seu
compromisso antes de conhecer o nonce do outro lado: uma tentativa em 100 milhões por
pedido, com no máximo cinco tentativas.

## Compatibilidade

- Clientes que informam endereço **e** pin continuam no fluxo anterior (código gerado
  pelo servidor). Isso mantém o WinPE e os roteiros de laboratório existentes.
- Informar só o endereço também funciona: o pin é observado na conexão e confirmado
  pelo código curto. Serve para redes que bloqueiam broadcast.
- Um cliente novo diante de um servidor antigo, sem pin digitado, recusa o pareamento
  com mensagem explícita em vez de confiar na descoberta.
- `--discovery off` desliga a resposta no servidor; `--discovery-port` muda a porta.

## Limites

A descoberta usa broadcast IPv4 na sub-rede do servidor: não atravessa roteadores nem
VLANs. Não é mDNS. O firewall do servidor precisa permitir a porta UDP 7444 na rede
privada (regra criada por `Install-Server.ps1` e `Update-Server.ps1`).
