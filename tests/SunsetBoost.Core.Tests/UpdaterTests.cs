using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using SunsetBoost.Core.Updates;

namespace SunsetBoost.Core.Tests;

/// <summary>GitHub falso: responde por URL; pode simular redirecionamento pra outro host.</summary>
internal sealed class FakeGitHub : HttpMessageHandler
{
    public Dictionary<string, (byte[] Body, string? FinalUrl)> Routes { get; } = new();
    public List<string> Requests { get; } = new();

    public void Add(string url, byte[] body, string? finalUrl = null) => Routes[url] = (body, finalUrl);
    public void Add(string url, string body) => Add(url, Encoding.UTF8.GetBytes(body));

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        var url = request.RequestUri!.ToString();
        Requests.Add(url);
        if (!Routes.TryGetValue(url, out var r)) return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound) { RequestMessage = request });
        var final = r.FinalUrl is null ? request : new HttpRequestMessage(HttpMethod.Get, r.FinalUrl);
        return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(r.Body), RequestMessage = final });
    }
}

public class UpdaterTests : IDisposable
{
    private const string Api = "https://api.github.com/repos/carlosftb/sunset-boost/releases?per_page=20";
    private const string Dl = "https://github.com/carlosftb/sunset-boost/releases/download";
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "sb-upd-" + Guid.NewGuid().ToString("N"));

    // Par de chaves só dos testes: a "do mantenedor" e uma de outra pessoa
    private readonly ECDsa _key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
    private readonly ECDsa _otherKey = ECDsa.Create(ECCurve.NamedCurves.nistP256);
    private string PublicKey => Convert.ToBase64String(_key.ExportSubjectPublicKeyInfo());

    public void Dispose()
    {
        _key.Dispose();
        _otherKey.Dispose();
        try { Directory.Delete(_dir, true); } catch { }
    }

    /// <summary>Assina como o workflow de release: ECDSA P-256 + SHA-256, DER em base64.</summary>
    private static string Sign(ECDsa key, byte[] data) =>
        Convert.ToBase64String(key.SignData(data, HashAlgorithmName.SHA256, DSASignatureFormat.Rfc3279DerSequence));

    private static object Release(string tag, bool draft = false, long size = 1000, bool sha = true, string? assetName = null)
    {
        var name = assetName ?? $"SunsetBoost-Setup-{tag.TrimStart('v')}.exe";
        var assets = new List<object> { new { name, size, browser_download_url = "https://evil.example/x.exe" } };
        if (sha) assets.Add(new { name = name + ".sha256", size = 90 });
        return new { tag_name = tag, draft, prerelease = true, body = "Novidades\n\n---\n\nSHA", assets };
    }

    private static string Json(params object[] releases) => JsonSerializer.Serialize(releases);

    [Fact]
    public void Escolhe_a_mais_nova_acima_da_atual_e_monta_o_endereco_oficial()
    {
        var u = Updater.PickNewest(Json(Release("v0.1.0"), Release("v0.3.0"), Release("v0.2.0")), new Version(0, 1, 0));
        Assert.NotNull(u);
        Assert.Equal(new Version(0, 3, 0), u!.Version);
        // O endereço vem do app, nunca do browser_download_url da resposta
        Assert.Equal($"{Dl}/v0.3.0/SunsetBoost-Setup-0.3.0.exe", u.InstallerUrl.ToString());
        Assert.Equal($"{Dl}/v0.3.0/SunsetBoost-Setup-0.3.0.exe.sha256", u.ShaUrl.ToString());
        Assert.Equal($"{Dl}/v0.3.0/SunsetBoost-Setup-0.3.0.exe.sha256.sig", u.SigUrl.ToString());
    }

    [Fact]
    public void Em_dia_quando_nao_ha_nada_mais_novo() =>
        Assert.Null(Updater.PickNewest(Json(Release("v0.1.0"), Release("v0.0.9")), new Version(0, 1, 0)));

    [Fact]
    public void Ignora_rascunho_tag_fora_do_padrao_sem_sha_sem_instalador_e_grande_demais()
    {
        var json = Json(
            Release("v0.9.0", draft: true),
            Release("v0.8.0-beta"),
            Release("v0.7.0", sha: false),
            Release("v0.6.0", assetName: "outro.exe"),
            Release("v0.5.0", size: Updater.MaxInstallerBytes + 1),
            Release("../v0.4.0"),
            Release("v0.2.0"));
        Assert.Equal(new Version(0, 2, 0), Updater.PickNewest(json, new Version(0, 1, 0))!.Version);
    }

    [Theory]
    [InlineData("ABCDEF0123456789abcdef0123456789ABCDEF0123456789abcdef0123456789  SunsetBoost-Setup-0.2.0.exe", true)]
    [InlineData("abcdef0123456789abcdef0123456789abcdef0123456789abcdef0123456789 *SunsetBoost-Setup-0.2.0.exe\n", true)]
    [InlineData("abcdef0123456789abcdef0123456789abcdef0123456789abcdef0123456789", false)]                                 // sem nome
    [InlineData("abcdef0123456789abcdef0123456789abcdef0123456789abcdef0123456789  SunsetBoost-Setup-0.1.0.exe", false)]  // outra versão
    [InlineData("abcdef0123456789abcdef0123456789abcdef0123456789abcdef0123456789  SunsetBoost-Setup-0.2.0.exe\nmais", false)]
    [InlineData("abc123  SunsetBoost-Setup-0.2.0.exe", false)]
    [InlineData("", false)]
    public void Le_o_sha256_assinado_so_do_instalador_certo(string content, bool ok) =>
        Assert.Equal(ok, Updater.ParseSignedSha256(content, "SunsetBoost-Setup-0.2.0.exe") is not null);

    [Theory]
    [InlineData("https://github.com/x", true)]
    [InlineData("https://objects.githubusercontent.com/x", true)]
    [InlineData("https://release-assets.githubusercontent.com/x", true)]
    [InlineData("http://github.com/x", false)]
    [InlineData("https://github.com.evil.example/x", false)]
    [InlineData("https://evil.example/x", false)]
    public void So_aceita_o_GitHub_por_HTTPS(string url, bool ok)
    {
        if (ok) Updater.EnsureAllowed(new Uri(url));
        else Assert.Throws<InvalidDataException>(() => Updater.EnsureAllowed(new Uri(url)));
    }

    public enum Sig { Valid, OtherKey, Garbage, Missing }

    /// <summary>
    /// Monta uma release v0.2.0 no GitHub falso. <paramref name="shaLine"/> troca o conteúdo do .sha256;
    /// a assinatura é feita sobre o conteúdo final, com a chave que <paramref name="sig"/> pedir.
    /// </summary>
    private (FakeGitHub Gh, UpdateInfo Info, byte[] Installer) Setup(byte[]? served = null, string? sha = null, string? finalUrl = null,
        Sig sig = Sig.Valid, string? shaLine = null)
    {
        var installer = Encoding.ASCII.GetBytes(new string('x', 1000));
        var gh = new FakeGitHub();
        gh.Add(Api, Json(Release("v0.2.0", size: installer.Length)));
        var hex = sha ?? Convert.ToHexString(SHA256.HashData(installer)).ToLowerInvariant();
        var shaBytes = Encoding.ASCII.GetBytes(shaLine ?? $"{hex}  SunsetBoost-Setup-0.2.0.exe\n");
        gh.Add($"{Dl}/v0.2.0/SunsetBoost-Setup-0.2.0.exe.sha256", shaBytes);
        var sigUrl = $"{Dl}/v0.2.0/SunsetBoost-Setup-0.2.0.exe.sha256.sig";
        switch (sig)
        {
            case Sig.Valid: gh.Add(sigUrl, Sign(_key, shaBytes)); break;
            case Sig.OtherKey: gh.Add(sigUrl, Sign(_otherKey, shaBytes)); break;
            case Sig.Garbage: gh.Add(sigUrl, "isto não é base64!"); break;
        }
        gh.Add($"{Dl}/v0.2.0/SunsetBoost-Setup-0.2.0.exe", served ?? installer, finalUrl ?? "https://release-assets.githubusercontent.com/abc");
        var info = Updater.CheckAsync(Updater.CreateClient(gh), new Version(0, 1, 0)).GetAwaiter().GetResult()!;
        return (gh, info, installer);
    }

    private Task<string> Download(FakeGitHub gh, UpdateInfo info) =>
        Updater.DownloadAsync(Updater.CreateClient(gh), info, _dir, new[] { PublicKey });

    [Fact]
    public async Task Baixa_e_confere_o_instalador()
    {
        var (gh, info, installer) = Setup();
        var path = await Download(gh, info);
        Assert.Equal(installer, await File.ReadAllBytesAsync(path));
        Assert.Equal("SunsetBoost-Setup-0.2.0.exe", Path.GetFileName(path));
        Assert.DoesNotContain(gh.Requests, r => r.Contains("evil"));
    }

    [Fact]
    public async Task SHA_diferente_apaga_e_recusa()
    {
        var (gh, info, _) = Setup(sha: new string('0', 64));
        var ex = await Assert.ThrowsAsync<InvalidDataException>(() => Download(gh, info));
        Assert.Contains("SHA-256", ex.Message);
        Assert.Empty(Directory.GetFiles(_dir));
    }

    [Fact]
    public async Task Arquivo_maior_que_o_anunciado_e_recusado()
    {
        var (gh, info, installer) = Setup(served: Encoding.ASCII.GetBytes(new string('x', 5000)));
        await Assert.ThrowsAsync<InvalidDataException>(() => Download(gh, info));
        Assert.Empty(Directory.GetFiles(_dir));
    }

    [Fact]
    public async Task Redirecionamento_pra_fora_do_GitHub_e_recusado()
    {
        var (gh, info, _) = Setup(finalUrl: "https://evil.example/setup.exe");
        var ex = await Assert.ThrowsAsync<InvalidDataException>(() => Download(gh, info));
        Assert.Contains("evil.example", ex.Message);
        Assert.Empty(Directory.GetFiles(_dir));
    }

    [Fact]
    public async Task Apaga_downloads_antigos_da_pasta()
    {
        Directory.CreateDirectory(_dir);
        File.WriteAllText(Path.Combine(_dir, "SunsetBoost-Setup-0.1.5.exe"), "velho");
        var (gh, info, _) = Setup();
        await Download(gh, info);
        Assert.Equal(new[] { "SunsetBoost-Setup-0.2.0.exe" }, Directory.GetFiles(_dir).Select(Path.GetFileName));
    }

    [Fact]
    public async Task Sem_assinatura_e_recusado_sem_baixar_o_instalador()
    {
        var (gh, info, _) = Setup(sig: Sig.Missing);
        var ex = await Assert.ThrowsAsync<InvalidDataException>(() => Download(gh, info));
        Assert.Contains(".sig", ex.Message);
        Assert.DoesNotContain(gh.Requests, r => r.EndsWith(".exe"));
        Assert.Empty(Directory.GetFiles(_dir));
    }

    [Theory]
    [InlineData(Sig.OtherKey)]
    [InlineData(Sig.Garbage)]
    public async Task Assinatura_errada_e_recusada_sem_baixar_o_instalador(Sig sig)
    {
        var (gh, info, _) = Setup(sig: sig);
        var ex = await Assert.ThrowsAsync<InvalidDataException>(() => Download(gh, info));
        Assert.Contains("assinatura", ex.Message);
        Assert.DoesNotContain(gh.Requests, r => r.EndsWith(".exe"));
        Assert.Empty(Directory.GetFiles(_dir));
    }

    [Fact]
    public async Task Sha256_trocado_depois_de_assinado_e_recusado()
    {
        // Quem tem acesso ao repositório troca o instalador e o .sha256, mas só tem o .sig antigo
        var (gh, info, installer) = Setup();
        var evil = Encoding.ASCII.GetBytes(new string('y', installer.Length));
        gh.Add($"{Dl}/v0.2.0/SunsetBoost-Setup-0.2.0.exe.sha256", $"{Convert.ToHexString(SHA256.HashData(evil)).ToLowerInvariant()}  SunsetBoost-Setup-0.2.0.exe\n");
        gh.Add($"{Dl}/v0.2.0/SunsetBoost-Setup-0.2.0.exe", evil, "https://release-assets.githubusercontent.com/abc");
        var ex = await Assert.ThrowsAsync<InvalidDataException>(() => Download(gh, info));
        Assert.Contains("assinatura", ex.Message);
        Assert.Empty(Directory.GetFiles(_dir));
    }

    [Fact]
    public async Task Assinatura_de_outra_versao_nao_serve()
    {
        // .sha256 e .sig verdadeiros da v0.1.0 republicados na tag v0.2.0: a assinatura confere, o nome não
        var old = Convert.ToHexString(SHA256.HashData(Encoding.ASCII.GetBytes(new string('x', 1000)))).ToLowerInvariant();
        var (gh, info, _) = Setup(shaLine: $"{old}  SunsetBoost-Setup-0.1.0.exe\n");
        var ex = await Assert.ThrowsAsync<InvalidDataException>(() => Download(gh, info));
        Assert.Contains("SunsetBoost-Setup-0.2.0.exe", ex.Message);
        Assert.DoesNotContain(gh.Requests, r => r.EndsWith(".exe"));
        Assert.Empty(Directory.GetFiles(_dir));
    }

    [Fact]
    public async Task Aceita_qualquer_das_chaves_durante_a_troca()
    {
        var (gh, info, _) = Setup();
        var other = Convert.ToBase64String(_otherKey.ExportSubjectPublicKeyInfo());
        var path = await Updater.DownloadAsync(Updater.CreateClient(gh), info, _dir, new[] { other, "lixo", PublicKey });
        Assert.True(File.Exists(path));
    }

    [Fact]
    public async Task Sem_chave_publica_no_app_nao_aceita_nada()
    {
        var (gh, info, _) = Setup();
        await Assert.ThrowsAsync<InvalidDataException>(() => Updater.DownloadAsync(Updater.CreateClient(gh), info, _dir, Array.Empty<string>()));
        Assert.DoesNotContain(gh.Requests, r => r.Contains("/download/"));
    }

    [Fact]
    public void Chave_que_nao_e_P256_e_ignorada()
    {
        using var p384 = ECDsa.Create(ECCurve.NamedCurves.nistP384);
        var data = Encoding.ASCII.GetBytes("abc");
        var sig = Encoding.ASCII.GetBytes(Sign(p384, data));
        Assert.False(Updater.VerifySignature(data, sig, new[] { Convert.ToBase64String(p384.ExportSubjectPublicKeyInfo()) }));
    }

    [Theory]
    [InlineData(true)]   // PKCS#8 ("BEGIN PRIVATE KEY"): pwsh e openssl genpkey
    [InlineData(false)]  // SEC1 ("BEGIN EC PRIVATE KEY"): openssl ecparam -genkey
    public void Assinatura_feita_como_no_workflow_confere_no_app(bool pkcs8)
    {
        // Mesmas chamadas do passo "Assinar o SHA-256" do release.yml
        var pem = pkcs8 ? _key.ExportPkcs8PrivateKeyPem() : _key.ExportECPrivateKeyPem();
        using var fromSecret = ECDsa.Create();
        fromSecret.ImportFromPem(pem);
        var data = Encoding.ASCII.GetBytes($"{new string('a', 64)}  SunsetBoost-Setup-0.2.0.exe");
        var sigFile = Encoding.ASCII.GetBytes(Convert.ToBase64String(
            fromSecret.SignData(data, HashAlgorithmName.SHA256, DSASignatureFormat.Rfc3279DerSequence)));
        Assert.True(Updater.VerifySignature(data, sigFile, new[] { PublicKey }));

        // O workflow acha as chaves no UpdateKeys.cs com esta expressão: a chave pública gerada tem que bater
        Assert.Matches(@"^MFkwEwYHKoZIzj0CAQYIKoZIzj0DAQcDQgAE[A-Za-z0-9+/]{86}==$", PublicKey);
    }

    [Fact]
    public void Chaves_embutidas_sao_P256()
    {
        foreach (var k in UpdateKeys.Trusted)
        {
            using var ecdsa = ECDsa.Create();
            ecdsa.ImportSubjectPublicKeyInfo(Convert.FromBase64String(k), out _);
            Assert.Equal("1.2.840.10045.3.1.7", ecdsa.ExportParameters(false).Curve.Oid.Value);
        }
    }

    [Fact]
    public void Versao_atual_vem_do_Directory_Build_props() => Assert.True(Updater.Current >= new Version(0, 1, 0));
}
