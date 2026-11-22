namespace SunsetBoost.Core.Edge;

/// <summary>Navegador baseado no Chromium que o modo leve sabe enxugar (mesmas janelas, abas e tipos de processo).</summary>
/// <param name="AppId">Id do app na lista da limpeza (AppCatalog).</param>
/// <param name="Process">Nome do .exe, em minúsculas e sem extensão.</param>
/// <param name="TabsClosable">O "x" das abas aparece pro Windows (UI Automation). O Opera GX esconde: lá as abas fecham com clique do meio.</param>
public sealed record BrowserDef(string AppId, string Name, string Process, bool TabsClosable = true);

public static class Browsers
{
    public static readonly BrowserDef Edge = new("edge", "Edge", "msedge");
    public static readonly BrowserDef Chrome = new("chrome", "Chrome", "chrome");
    /// <summary>Opera e Opera GX usam o mesmo opera.exe. As abas não mostram o "x" pro Windows: fecham com clique do meio.</summary>
    public static readonly BrowserDef Opera = new("opera", "Opera GX", "opera", TabsClosable: false);

    /// <summary>
    /// Os que têm modo leve (1ª aba + limite de RAM) em vez de serem fechados. O Opera GX fica de fora:
    /// em algumas sessões ele não mostra as abas pro Windows, então no Modo Jogo ele sempre é fechado.
    /// </summary>
    public static readonly IReadOnlyList<BrowserDef> Lite = new[] { Edge, Chrome };

    public static string Names => string.Join(", ", Lite.Take(Lite.Count - 1).Select(b => b.Name)) + " e " + Lite[^1].Name;

    /// <summary>Pelo id ("edge", "chrome", "opera") ou apelido ("operagx", "gx").</summary>
    public static BrowserDef? Find(string? key) => key?.Trim().ToLowerInvariant() switch
    {
        "edge" or "msedge" => Edge,
        "chrome" or "google" => Chrome,
        "opera" or "operagx" or "opera-gx" or "gx" => Opera,
        _ => null,
    };
}
