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
    /// <summary>URLs que mandam os cabeçalhos e depois travam (nunca chega um byte do corpo).</summary>
    public HashSet<string> Stalled { get; } = new();

    public void Add(string url, byte[] body, string? finalUrl = null) => Routes[url] = (body, finalUrl);
    public void Add(string url, string body) => Add(url, Encoding.UTF8.GetBytes(body));

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        var url = request.RequestUri!.ToString();
        Requests.Add(url);
        if (!Routes.TryGetValue(url, out var r)) return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound) { RequestMessage = request });
        var final = r.FinalUrl is null ? request : new HttpRequestMessage(HttpMethod.Get, r.FinalUrl);
        HttpContent content = Stalled.Contains(url) ? new StreamContent(new StalledStream()) : new ByteArrayContent(r.Body);
        return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = content, RequestMessage = final });
    }

    private sealed class StalledStream : Stream
    {
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default)
        {
            await Task.Delay(Timeout.Infinite, ct);
            return 0;
        }
        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken ct) => ReadAsync(buffer.AsMemory(offset, count), ct).AsTask();
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => 0; set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}

public class UpdaterTests : IDisposable
{
    private const string Api = "https://api.github.com/repos/carlosftb/sunset-boost/releases?per_page=20";
    private const string Dl = "https://github.com/carlosftb/sunset-boost/releases/download";
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "sb-upd-" + Guid.NewGuid().ToString("N"));

    public void Dispose() { try { Directory.Delete(_dir, true); } catch { } }

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
    [InlineData("abcdef0123456789abcdef0123456789abcdef0123456789abcdef0123456789", true)]
    [InlineData("abc123  arquivo.exe", false)]
    [InlineData("", false)]
    public void Le_o_arquivo_sha256(string content, bool ok) => Assert.Equal(ok, Updater.ParseSha256(content) is not null);

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

    private (FakeGitHub Gh, UpdateInfo Info, byte[] Installer) Setup(byte[]? served = null, string? sha = null, string? finalUrl = null)
    {
        var installer = Encoding.ASCII.GetBytes(new string('x', 1000));
        var gh = new FakeGitHub();
        gh.Add(Api, Json(Release("v0.2.0", size: installer.Length)));
        var hex = sha ?? Convert.ToHexString(SHA256.HashData(installer)).ToLowerInvariant();
        gh.Add($"{Dl}/v0.2.0/SunsetBoost-Setup-0.2.0.exe.sha256", $"{hex}  SunsetBoost-Setup-0.2.0.exe\n");
        gh.Add($"{Dl}/v0.2.0/SunsetBoost-Setup-0.2.0.exe", served ?? installer, finalUrl ?? "https://release-assets.githubusercontent.com/abc");
        var info = Updater.CheckAsync(Updater.CreateClient(gh), new Version(0, 1, 0)).GetAwaiter().GetResult()!;
        return (gh, info, installer);
    }

    [Fact]
    public async Task Baixa_e_confere_o_instalador()
    {
        var (gh, info, installer) = Setup();
        var d = await Updater.DownloadAsync(Updater.CreateClient(gh), info, _dir);
        Assert.Equal(installer, await File.ReadAllBytesAsync(d.Path));
        Assert.Equal("SunsetBoost-Setup-0.2.0.exe", Path.GetFileName(d.Path));
        Assert.Equal(Convert.ToHexString(SHA256.HashData(installer)), d.Sha256, ignoreCase: true);
        Assert.DoesNotContain(gh.Requests, r => r.Contains("evil"));
    }

    [Fact]
    public async Task SHA_diferente_apaga_e_recusa()
    {
        var (gh, info, _) = Setup(sha: new string('0', 64));
        var ex = await Assert.ThrowsAsync<InvalidDataException>(() => Updater.DownloadAsync(Updater.CreateClient(gh), info, _dir));
        Assert.Contains("SHA-256", ex.Message);
        Assert.Empty(Directory.GetFiles(_dir));
    }

    [Fact]
    public async Task Arquivo_maior_que_o_anunciado_e_recusado()
    {
        var (gh, info, installer) = Setup(served: Encoding.ASCII.GetBytes(new string('x', 5000)));
        await Assert.ThrowsAsync<InvalidDataException>(() => Updater.DownloadAsync(Updater.CreateClient(gh), info, _dir));
        Assert.Empty(Directory.GetFiles(_dir));
    }

    [Fact]
    public async Task Redirecionamento_pra_fora_do_GitHub_e_recusado()
    {
        var (gh, info, _) = Setup(finalUrl: "https://evil.example/setup.exe");
        var ex = await Assert.ThrowsAsync<InvalidDataException>(() => Updater.DownloadAsync(Updater.CreateClient(gh), info, _dir));
        Assert.Contains("evil.example", ex.Message);
        Assert.Empty(Directory.GetFiles(_dir));
    }

    [Fact]
    public async Task Apaga_downloads_antigos_da_pasta()
    {
        Directory.CreateDirectory(_dir);
        File.WriteAllText(Path.Combine(_dir, "SunsetBoost-Setup-0.1.5.exe"), "velho");
        var (gh, info, _) = Setup();
        await Updater.DownloadAsync(Updater.CreateClient(gh), info, _dir);
        Assert.Equal(new[] { "SunsetBoost-Setup-0.2.0.exe" }, Directory.GetFiles(_dir).Select(Path.GetFileName));
    }

    [Fact]
    public async Task Instalar_confere_o_SHA_de_novo_e_abre_o_arquivo_certo()
    {
        var (gh, info, _) = Setup();
        var d = await Updater.DownloadAsync(Updater.CreateClient(gh), info, _dir);
        var opened = new List<string>();
        var original = InstallerLauncher.Open;
        InstallerLauncher.Open = opened.Add;
        try { InstallerLauncher.Start(d); }
        finally { InstallerLauncher.Open = original; }
        Assert.Equal(new[] { d.Path }, opened);
    }

    [Fact]
    public async Task Instalador_trocado_depois_do_download_nao_abre_e_e_apagado()
    {
        var (gh, info, _) = Setup();
        var d = await Updater.DownloadAsync(Updater.CreateClient(gh), info, _dir);
        await File.WriteAllTextAsync(d.Path, "outro programa"); // trocado entre o download e o clique em INSTALAR
        var opened = new List<string>();
        var original = InstallerLauncher.Open;
        InstallerLauncher.Open = opened.Add;
        try { Assert.Throws<InvalidDataException>(() => InstallerLauncher.Start(d)); }
        finally { InstallerLauncher.Open = original; }
        Assert.Empty(opened);
        Assert.False(File.Exists(d.Path));
    }

    [Fact]
    public async Task Download_travado_desiste_e_apaga_o_pedaco()
    {
        var (gh, info, _) = Setup();
        gh.Stalled.Add($"{Dl}/v0.2.0/SunsetBoost-Setup-0.2.0.exe");
        var original = Updater.StallTimeout;
        Updater.StallTimeout = TimeSpan.FromMilliseconds(300);
        try
        {
            var ex = await Assert.ThrowsAsync<TimeoutException>(() => Updater.DownloadAsync(Updater.CreateClient(gh), info, _dir));
            Assert.Contains("parou", ex.Message);
        }
        finally { Updater.StallTimeout = original; }
        Assert.Empty(Directory.GetFiles(_dir));
    }

    [Fact]
    public void Versao_atual_vem_do_Directory_Build_props() => Assert.True(Updater.Current >= new Version(0, 1, 0));
}
