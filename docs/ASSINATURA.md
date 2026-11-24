# Assinatura das atualizações

O app só instala uma atualização se o `.sha256` da release vier com um `.sha256.sig` válido:
uma assinatura ECDSA P-256 feita com a chave privada do mantenedor. A chave pública fica no app
(`src/SunsetBoost.Core/Updates/UpdateKeys.cs`). A chave privada fica só no segredo `UPDATE_SIGNING_KEY` do GitHub Actions.

## O que isso protege, e o que não protege

| Situação | Sem assinatura | Com assinatura |
|---|---|---|
| Alguém com um token ou acesso de escrita troca o instalador e o `.sha256` de uma release pela API | o app instala | o app recusa (não tem como gerar o `.sig`) |
| Republicar o `.sha256` e o `.sig` de uma versão antiga numa tag nova | — | o app recusa (o `.sha256` assinado cita o nome do instalador, com a versão) |
| Alguém consegue rodar o workflow **Release** (empurra uma tag com o workflow alterado) | o app instala | **só é bloqueado** se o ambiente `release` exigir a sua aprovação (passo 3 abaixo) |
| A chave privada vaza | — | o app instala: troque a chave (veja "Se a chave vazar") |

O passo 3 (aprovação do ambiente) é o que separa "ter acesso ao repositório" de "conseguir publicar uma atualização". Não pule.

## 1. Gerar o par de chaves (uma vez)

Gere **fora da pasta do repositório** (por exemplo, numa pasta temporária), para a chave privada nunca entrar num commit.

Com o OpenSSL que vem no Git for Windows (Git Bash):

```bash
cd "$(mktemp -d)"
openssl genpkey -algorithm EC -pkeyopt ec_paramgen_curve:P-256 -out chave-privada.pem
openssl pkey -in chave-privada.pem -pubout -outform DER | openssl base64 -A; echo
```

Ou com o PowerShell 7 (`pwsh`, não o Windows PowerShell 5.1):

```powershell
$k = [System.Security.Cryptography.ECDsa]::Create([System.Security.Cryptography.ECCurve+NamedCurves]::nistP256)
$k.ExportPkcs8PrivateKeyPem() | Set-Content -NoNewline chave-privada.pem
[Convert]::ToBase64String($k.ExportSubjectPublicKeyInfo())
```

A última linha imprime a **chave pública**: uma linha só, com 124 caracteres, que começa com `MFkwEwYHKoZIzj0CAQYIKoZIzj0DAQcDQgAE`.

## 2. Colocar a chave pública no app

Em `src/SunsetBoost.Core/Updates/UpdateKeys.cs`:

```csharp
public static readonly string[] Trusted =
{
    "MFkwEwYHKoZIzj0CAQYIKoZIzj0DAQcDQgAE...",
};
```

Commit normal, por PR. A chave pública não é segredo.
Enquanto a lista estiver vazia, o app recusa todo download automático e o workflow de release para com erro.

## 3. Criar o ambiente `release` com o segredo

No GitHub: **Settings → Environments → New environment** → `release`.

- **Required reviewers**: você. Cada execução do workflow de release espera você clicar em **Approve** na página do Actions.
  Deixe **Prevent self-review** desligado, senão você não consegue aprovar o próprio release.
- **Deployment branches and tags** → **Selected branches and tags** → adicione a regra de tag `v*`.
- **Environment secrets** → **Add secret**: nome `UPDATE_SIGNING_KEY`, valor = o conteúdo inteiro de `chave-privada.pem`,
  incluindo as linhas `-----BEGIN ...-----` e `-----END ...-----`.

Ou pelo terminal, com o `gh` (o ambiente precisa existir; os revisores e a regra de tag são configurados no site):

```powershell
Get-Content -Raw chave-privada.pem | gh secret set UPDATE_SIGNING_KEY --env release --repo carlosftb/sunset-boost
```

Depois:

1. Guarde uma cópia da chave privada **offline** (gerenciador de senhas ou um pendrive criptografado). O GitHub não deixa ler o segredo de volta.
2. Apague o `chave-privada.pem` do disco.

Se a chave se perder, as versões já instaladas não aceitam mais atualização pelo app: será preciso gerar outra chave e
os usuários baixam essa versão manualmente pela página de Releases.

## 4. Conferir

Lance uma versão normalmente ([DESENVOLVIMENTO.md](DESENVOLVIMENTO.md#lançar-uma-versão)). O passo **Assinar o SHA-256**:

- para se o segredo estiver faltando ou não for P-256;
- confere a assinatura com as chaves de `UpdateKeys.cs` antes de publicar, então um segredo de outro par de chaves também faz o workflow parar.

No rascunho da release tem que aparecer `SunsetBoost-Setup-X.Y.Z.exe.sha256.sig`.
Para testar o download sem instalar: `sunsetboost-cli atualizacao --como 0.0.0 --baixar-para <pasta>`.

## Trocar a chave (planejado)

O app aceita qualquer uma das chaves de `Trusted`, então a troca é feita em duas versões:

1. Gere o par novo (passo 1).
2. Coloque a chave pública **nova** em `Trusted`, **sem tirar a antiga**. Lance essa versão (vN) ainda assinada com a chave antiga:
   quem está nas versões anteriores só confia na antiga.
3. Quando a maioria estiver na vN, troque o segredo `UPDATE_SIGNING_KEY` pela chave privada nova. As próximas versões saem assinadas com ela.
4. Numa versão seguinte, tire a chave pública antiga de `Trusted`.

Quem pular a vN (ficar numa versão que só conhece a chave antiga) vai ver "assinatura não é válida" e precisa baixar manualmente.

## Se a chave vazar

1. Gere um par novo e troque o segredo `UPDATE_SIGNING_KEY` imediatamente.
2. Lance uma versão com **só** a chave pública nova em `Trusted`.
   Para ela chegar a quem já tem o app instalado, ela ainda precisa da assinatura antiga: assine essa única versão com a chave
   vazada (troque o segredo de volta só para esse release) ou peça aos usuários para baixar manualmente.
3. Avise no README e nas notas da release. Até os usuários atualizarem, quem tem a chave vazada consegue assinar instaladores
   que as versões antigas aceitam: é o limite de qualquer chave embutida.
