using System.Globalization;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace SunsetBoost.Core.Updates;

/// <summary>Uma versão publicada no GitHub, mais nova que a instalada.</summary>
public sealed record UpdateInfo(Version Version, string Tag, string InstallerName, long InstallerSize, Uri InstallerUrl, Uri ShaUrl, Uri SigUrl, Uri PageUrl, string Notes);

/// <summary>
/// Procura e baixa atualizações nas Releases do GitHub. Cuidados, porque o app roda com admin:
/// <list type="bullet">
/// <item>só o repositório oficial, por HTTPS; os endereços de download são montados aqui (não vêm da resposta);</item>
/// <item>só arquivos com o nome exato do instalador; tamanho conferido e com limite;</item>
/// <item>o .sha256 da release tem que estar assinado (.sig, ECDSA P-256) por uma chave de <see cref="UpdateKeys"/>:
/// quem tiver acesso de escrita ao repositório troca o instalador e o .sha256, mas não faz a assinatura sem a chave privada;</item>
/// <item>o .sha256 assinado tem que citar o nome exato do instalador (com a versão): não dá pra reaproveitar a assinatura de uma versão antiga;</item>
/// <item>SHA-256 do arquivo baixado tem que bater com o .sha256 assinado;</item>
/// <item>quem roda o instalador é o Windows, sem admin, com o aviso do UAC: o app nunca executa o download com admin.</item>
/// </list>
/// </summary>
public static class Updater
{
    public const string Owner = "carlosftb";
    public const string Repo = "sunset-boost";

    /// <summary>Hosts aceitos depois dos redirecionamentos do GitHub (a página de download manda pro armazenamento de arquivos).</summary>
    internal static readonly string[] AllowedHosts =
    {
        "api.github.com", "github.com", "objects.githubusercontent.com", "release-assets.githubusercontent.com",
    };

    internal const long MaxApiBytes = 2 * 1024 * 1024;
    internal const long MaxInstallerBytes = 300L * 1024 * 1024;

    private static readonly Regex TagRx = new(@"^v(\d+)\.(\d+)\.(\d+)$");
    // Formato do sha256sum: "<hash>  <arquivo>" (ou "<hash> *<arquivo>"), uma linha só
    private static readonly Regex SignedShaRx = new(@"^([0-9a-fA-F]{64}) [ *](\S+)\r?\n?$");

    internal const int MaxSigBytes = 1024;

    /// <summary>Versão deste app (do &lt;Version&gt; do Directory.Build.props).</summary>
    public static Version Current
    {
        get
        {
            var info = typeof(Updater).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "0.0.0";
            var plain = info.Split('+', '-')[0];
            return Version.TryParse(plain, out var v) ? Normalize(v) : new Version(0, 0, 0);
        }
    }

    private static Version Normalize(Version v) => new(v.Major, v.Minor, Math.Max(0, v.Build));

    public static string Display(Version v) => $"{v.Major}.{v.Minor}.{v.Build}";

    public static HttpClient CreateClient(HttpMessageHandler? handler = null)
    {
        var client = handler is null ? new HttpClient(new SocketsHttpHandler { AllowAutoRedirect = true, MaxAutomaticRedirections = 5 })
                                     : new HttpClient(handler);
        client.Timeout = TimeSpan.FromSeconds(30);
        // Nome genérico: nada do usuário vai no pedido
        client.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("SunsetBoost-Updater", Display(Current)));
        client.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
        return client;
    }

    /// <summary>Procura uma versão mais nova. null = está em dia.</summary>
    public static async Task<UpdateInfo?> CheckAsync(HttpClient http, Version current, CancellationToken ct = default)
    {
        var url = new Uri($"https://api.github.com/repos/{Owner}/{Repo}/releases?per_page=20");
        using var resp = await http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct);
        EnsureAllowed(resp.RequestMessage?.RequestUri);
        resp.EnsureSuccessStatusCode();
        var json = await ReadLimitedAsync(resp, MaxApiBytes, ct);
        return PickNewest(json, current);
    }

    /// <summary>Lê a lista de releases do GitHub e escolhe a mais nova acima da atual. Rascunhos e tags fora do padrão vX.Y.Z ficam de fora.</summary>
    internal static UpdateInfo? PickNewest(string json, Version current)
    {
        current = Normalize(current);
        UpdateInfo? best = null;
        using var doc = JsonDocument.Parse(json);
        if (doc.RootElement.ValueKind != JsonValueKind.Array) return null;
        foreach (var rel in doc.RootElement.EnumerateArray())
        {
            if (rel.TryGetProperty("draft", out var d) && d.ValueKind == JsonValueKind.True) continue;
            var tag = rel.TryGetProperty("tag_name", out var t) ? t.GetString() ?? "" : "";
            var m = TagRx.Match(tag);
            if (!m.Success) continue;
            var ver = new Version(int.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture),
                int.Parse(m.Groups[2].Value, CultureInfo.InvariantCulture), int.Parse(m.Groups[3].Value, CultureInfo.InvariantCulture));
            if (ver <= current || (best is not null && ver <= best.Version)) continue;

            var name = $"SunsetBoost-Setup-{Display(ver)}.exe";
            long size = -1;
            bool hasSha = false;
            if (rel.TryGetProperty("assets", out var assets) && assets.ValueKind == JsonValueKind.Array)
                foreach (var a in assets.EnumerateArray())
                {
                    var an = a.TryGetProperty("name", out var n) ? n.GetString() : null;
                    if (an == name && a.TryGetProperty("size", out var s) && s.TryGetInt64(out var sz)) size = sz;
                    if (an == name + ".sha256") hasSha = true;
                }
            if (size <= 0 || size > MaxInstallerBytes || !hasSha) continue; // release sem instalador ou sem SHA-256: ignora

            var notes = rel.TryGetProperty("body", out var b) ? b.GetString() ?? "" : "";
            if (notes.Length > 4000) notes = notes[..4000];
            var basePath = $"https://github.com/{Owner}/{Repo}/releases";
            best = new UpdateInfo(ver, tag, name, size,
                new Uri($"{basePath}/download/{tag}/{name}"), new Uri($"{basePath}/download/{tag}/{name}.sha256"),
                new Uri($"{basePath}/download/{tag}/{name}.sha256.sig"), new Uri($"{basePath}/tag/{tag}"), notes);
        }
        return best;
    }

    /// <summary>
    /// Baixa o instalador pra <paramref name="folder"/> (com admin, uma pasta que só admin altera), confere a assinatura
    /// do .sha256 e o SHA-256 do instalador. Devolve o caminho do instalador conferido. Qualquer diferença apaga o arquivo e lança exceção.
    /// </summary>
    public static Task<string> DownloadAsync(HttpClient http, UpdateInfo info, string folder, IProgress<double>? progress = null, CancellationToken ct = default) =>
        DownloadAsync(http, info, folder, UpdateKeys.Trusted, progress, ct);

    /// <summary>Igual ao público, com as chaves aceitas vindas de fora (os testes usam uma chave própria).</summary>
    internal static async Task<string> DownloadAsync(HttpClient http, UpdateInfo info, string folder, IReadOnlyList<string> trustedKeys,
        IProgress<double>? progress = null, CancellationToken ct = default)
    {
        if (trustedKeys.Count == 0)
            throw new InvalidDataException("esta versão do app não tem a chave pública de atualização, então não aceita downloads automáticos");

        Directory.CreateDirectory(folder);
        // Restos de downloads anteriores saem antes (a pasta é só nossa)
        foreach (var old in Directory.GetFiles(folder, "SunsetBoost-Setup-*")) TryDelete(old);

        // Primeiro o .sha256 e a assinatura dele: sem assinatura válida, nem baixa o instalador
        var shaBytes = await GetSmallAsync(http, info.ShaUrl, 4096, "o arquivo .sha256", ct);
        var sigBytes = await GetSmallAsync(http, info.SigUrl, MaxSigBytes, "a assinatura (.sig)", ct);
        if (!VerifySignature(shaBytes, sigBytes, trustedKeys))
            throw new InvalidDataException("a assinatura da release não é válida: ela não foi publicada com a chave do Sunset Boost");
        var expected = ParseSignedSha256(System.Text.Encoding.ASCII.GetString(shaBytes), info.InstallerName)
                       ?? throw new InvalidDataException($"o .sha256 assinado não é do {info.InstallerName}");

        var target = Path.Combine(folder, info.InstallerName);
        var partial = target + ".partial";
        try
        {
            using (var resp = await http.GetAsync(info.InstallerUrl, HttpCompletionOption.ResponseHeadersRead, ct))
            {
                EnsureAllowed(resp.RequestMessage?.RequestUri);
                resp.EnsureSuccessStatusCode();
                if (resp.Content.Headers.ContentLength is { } len && len != info.InstallerSize)
                    throw new InvalidDataException($"tamanho diferente do anunciado ({len} em vez de {info.InstallerSize} bytes)");

                using var sha = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
                await using (var src = await resp.Content.ReadAsStreamAsync(ct))
                await using (var dst = new FileStream(partial, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                {
                    var buf = new byte[81920];
                    long total = 0;
                    int n;
                    while ((n = await src.ReadAsync(buf, ct)) > 0)
                    {
                        total += n;
                        if (total > info.InstallerSize) throw new InvalidDataException("o download passou do tamanho anunciado");
                        sha.AppendData(buf, 0, n);
                        await dst.WriteAsync(buf.AsMemory(0, n), ct);
                        progress?.Report((double)total / info.InstallerSize);
                    }
                    if (total != info.InstallerSize) throw new InvalidDataException("o download veio incompleto");
                }
                var got = Convert.ToHexString(sha.GetHashAndReset());
                if (!got.Equals(expected, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException($"o SHA-256 do instalador não bate com o da release (esperado {expected[..12]}…, veio {got[..12]}…)");
            }
            File.Move(partial, target, overwrite: true);

            // Confere de novo o arquivo final: é ele que o Windows vai abrir
            if (!HashFile(target).Equals(expected, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("o instalador mudou depois de baixado");
            return target;
        }
        catch
        {
            TryDelete(partial);
            TryDelete(target);
            throw;
        }
    }

    /// <summary>
    /// Confere a assinatura ECDSA P-256 (SHA-256, DER em base64, como o workflow de release gera) dos bytes exatos do .sha256.
    /// Basta uma das chaves aceitas bater. Assinatura mal formada ou chave que não é P-256 = inválida.
    /// </summary>
    internal static bool VerifySignature(byte[] data, byte[] sigFile, IEnumerable<string> trustedKeys)
    {
        byte[] sig;
        try { sig = Convert.FromBase64String(System.Text.Encoding.ASCII.GetString(sigFile).Trim()); }
        catch (FormatException) { return false; }
        if (sig.Length == 0) return false;

        foreach (var key in trustedKeys)
        {
            using var ecdsa = ECDsa.Create();
            try
            {
                ecdsa.ImportSubjectPublicKeyInfo(Convert.FromBase64String(key), out _);
                if (ecdsa.ExportParameters(false).Curve.Oid.Value != P256Oid) continue;
                if (ecdsa.VerifyData(data, sig, HashAlgorithmName.SHA256, DSASignatureFormat.Rfc3279DerSequence)) return true;
            }
            catch (CryptographicException) { }
            catch (FormatException) { }
        }
        return false;
    }

    private const string P256Oid = "1.2.840.10045.3.1.7";

    /// <summary>
    /// O .sha256 assinado tem que ser uma linha "&lt;hash&gt;  &lt;arquivo&gt;" com o nome exato do instalador esperado.
    /// Devolve o hash em maiúsculas, ou null.
    /// </summary>
    internal static string? ParseSignedSha256(string content, string installerName)
    {
        var m = SignedShaRx.Match(content ?? "");
        return m.Success && m.Groups[2].Value == installerName ? m.Groups[1].Value.ToUpperInvariant() : null;
    }

    internal static string HashFile(string path)
    {
        using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        return Convert.ToHexString(SHA256.HashData(fs));
    }

    /// <summary>Recusa resposta que terminou fora do GitHub ou sem HTTPS (depois dos redirecionamentos).</summary>
    internal static void EnsureAllowed(Uri? uri)
    {
        if (uri is null || uri.Scheme != Uri.UriSchemeHttps || !AllowedHosts.Contains(uri.Host, StringComparer.OrdinalIgnoreCase))
            throw new InvalidDataException($"resposta veio de um endereço não esperado ({uri?.GetLeftPart(UriPartial.Authority) ?? "?"})");
    }

    /// <summary>Baixa um arquivo pequeno da release (o .sha256 ou o .sig). Se faltar, diz qual.</summary>
    private static async Task<byte[]> GetSmallAsync(HttpClient http, Uri url, long max, string what, CancellationToken ct)
    {
        using var resp = await http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct);
        EnsureAllowed(resp.RequestMessage?.RequestUri);
        if (resp.StatusCode == System.Net.HttpStatusCode.NotFound) throw new InvalidDataException($"a release não tem {what}");
        resp.EnsureSuccessStatusCode();
        return await ReadLimitedBytesAsync(resp, max, ct);
    }

    private static async Task<string> ReadLimitedAsync(HttpResponseMessage resp, long max, CancellationToken ct) =>
        System.Text.Encoding.UTF8.GetString(await ReadLimitedBytesAsync(resp, max, ct));

    private static async Task<byte[]> ReadLimitedBytesAsync(HttpResponseMessage resp, long max, CancellationToken ct)
    {
        if (resp.Content.Headers.ContentLength > max) throw new InvalidDataException("resposta grande demais");
        await using var s = await resp.Content.ReadAsStreamAsync(ct);
        using var ms = new MemoryStream();
        var buf = new byte[16384];
        int n;
        while ((n = await s.ReadAsync(buf, ct)) > 0)
        {
            if (ms.Length + n > max) throw new InvalidDataException("resposta grande demais");
            ms.Write(buf, 0, n);
        }
        return ms.ToArray();
    }

    private static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }
}

/// <summary>Abre o instalador baixado.</summary>
public static class InstallerLauncher
{
    /// <summary>
    /// Pede pro Explorer (que roda sem admin) abrir o instalador: ele começa sem admin e o próprio Windows mostra o UAC.
    /// Assim o app com admin nunca executa um download direto, e o usuário vê e confirma cada atualização.
    /// </summary>
    public static void Start(string installerPath)
    {
        if (!File.Exists(installerPath)) throw new FileNotFoundException("instalador não encontrado", installerPath);
        var explorer = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "explorer.exe");
        System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(explorer, $"\"{installerPath}\"") { UseShellExecute = false });
    }
}
