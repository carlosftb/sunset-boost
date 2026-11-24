namespace SunsetBoost.Core.Updates;

/// <summary>
/// Chaves públicas (ECDSA P-256) que assinam as atualizações. O app só instala uma versão nova se o .sig dela
/// foi feito com a chave privada de uma destas. A chave privada fica só no segredo UPDATE_SIGNING_KEY do GitHub Actions.
/// Como gerar, guardar e trocar a chave: docs/ASSINATURA.md.
/// </summary>
public static class UpdateKeys
{
    /// <summary>
    /// SubjectPublicKeyInfo em base64 (uma linha, começa com "MFkw"). Mais de uma só durante a troca de chave.
    /// O workflow de release confere a assinatura com estas mesmas chaves antes de publicar.
    /// Vazia = nenhuma atualização é aceita (o app recusa tudo até a chave ser colocada aqui).
    /// </summary>
    public static readonly string[] Trusted =
    {
        // Cole aqui a chave pública gerada conforme docs/ASSINATURA.md, entre aspas, seguida de vírgula.
    };
}
