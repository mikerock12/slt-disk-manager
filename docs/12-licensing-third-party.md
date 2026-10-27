# Licenças e redistribuição

O proprietário ainda não escolheu licença comercial/open-source. O repositório
público publica o código com direitos reservados; não atribuir MIT/GPL ao código
do projeto sem decisão explícita do proprietário.

.NET runtime: licença e avisos oficiais acompanham o pacote em
`docs/licenses/DotNet-LICENSE.txt` e `DotNet-ThirdPartyNotices.txt`.
Microsoft.Data.Sqlite e SQLitePCLRaw:
consulte os pacotes fixados no csproj; dependência SQLite nativa atualizada para
2.1.12 após detecção de advisory na versão 2.1.11.

Rescue/WinPE Go usam somente biblioteca padrão; a licença do runtime está em
`docs/licenses/Go-LICENSE.txt`. Debian/live-build e ferramentas
`smartmontools`, GNU ddrescue, Partclone, rsync e outros têm licenças próprias,
incluindo GPL. A ISO de laboratório é construída com pacotes oficiais Debian e
o código deste repositório. O build registra versões, índice de fontes por versão
e avisos de copyright; esses registros acompanham a evidência de CI. Licenças dos
pacotes também estão em `/usr/share/doc` no sistema Live. Distribuição comercial
e eventual oferta de código correspondente continuam sujeitas à revisão do proprietário.

Não são distribuídos Windows, WinPE, WIM, ISO Microsoft, chaves de ativação ou
drivers Windows de fabricantes. O firmware Linux fornecido pelos pacotes Debian
acompanha a ISO sob as respectivas licenças. O operador fornece mídia Windows e licenças legítimas. Os perfis
documentados não contornam criptografia, Secure Boot nem ativação.
