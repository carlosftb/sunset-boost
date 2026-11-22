using System.Diagnostics;
using System.Windows.Automation;
using SunsetBoost.Core.Infrastructure;

namespace SunsetBoost.Core.Edge;

public sealed record EdgeTabsResult(int WindowsClosed, int TabsClosed, int TabsKept, string? Error);

/// <summary>
/// Deixa o navegador (Edge, Chrome ou Opera GX) com uma aba só: mantém a janela usada por último e a primeira aba dela,
/// fecha as outras abas (clicando no "x" de cada uma, via UI Automation) e as outras janelas.
/// Abas fixadas não têm "x" e ficam. As fechadas voltam com Ctrl+Shift+T ou pelo Histórico.
/// </summary>
public static class EdgeTabs
{
    /// <param name="browser">Navegador (padrão: Edge).</param>
    /// <param name="onlyPids">Se informado, só mexe em janelas desses processos (usado nos testes com um navegador isolado).</param>
    /// <param name="forceMiddleClick">Testes: usa o clique do meio mesmo em navegador que tem o "x".</param>
    public static EdgeTabsResult KeepFirstTab(BrowserDef? browser = null, ISet<int>? onlyPids = null, bool forceMiddleClick = false)
    {
        browser ??= Browsers.Edge;
        try
        {
            var windows = FindWindows(browser.Process, onlyPids);
            if (windows.Count == 0)
            {
                if (!TabsHidden(browser, onlyPids)) return new(0, 0, 0, null);
                AppLog.Info($"{browser.Name} leve: o {browser.Name} está aberto mas não mostra as abas pro Windows agora; vale só o limite de RAM");
                return new(0, 0, 0, HiddenMessage(browser));
            }

            // A primeira da lista é a que está mais em cima (a usada por último)
            var keep = windows[0];
            int windowsClosed = 0;
            foreach (var w in windows.Skip(1))
            {
                if (w.TryGetCurrentPattern(WindowPattern.Pattern, out var wp)) { ((WindowPattern)wp).Close(); windowsClosed++; }
            }

            bool middle = forceMiddleClick || !browser.TabsClosable;
            int before = RegularTabs(keep).Count;
            int closed = middle ? CloseByMiddleClick(keep) : CloseByButton(keep);
            int kept = BrowserTabs(keep).Count;
            if (middle) closed = Math.Max(0, before - RegularTabs(keep).Count); // conta o que de fato sumiu
            AppLog.Info($"{browser.Name} leve: {closed} abas e {windowsClosed} janelas fechadas, mantida a 1ª aba" + (middle ? " (clique do meio)" : ""));
            return new(windowsClosed, closed, kept,
                middle && closed == 0 && before > 1 ? $"o {browser.Name} não fechou as abas (janela minimizada?); vale o limite de RAM" : null);
        }
        catch (Exception ex)
        {
            AppLog.Error($"{browser.Name} leve: não consegui fechar as abas", ex);
            return new(0, 0, 0, ex.Message);
        }
    }

    /// <summary>Edge e Chrome: aperta o "x" de cada aba (do fim pro começo, pra posição das outras não mudar).</summary>
    private static int CloseByButton(AutomationElement window)
    {
        var tabs = RegularTabs(window);
        int closed = 0;
        for (int i = tabs.Count - 1; i >= 1; i--)
        {
            var close = CloseButton(tabs[i]);
            if (close is null) continue; // aba fixada
            if (close.TryGetCurrentPattern(InvokePattern.Pattern, out var ip)) { ((InvokePattern)ip).Invoke(); closed++; Thread.Sleep(60); }
        }
        return closed;
    }

    /// <summary>
    /// Opera GX: o "x" só existe com o mouse em cima, então o app manda um clique do meio (que fecha a aba)
    /// direto pra janela do navegador. Não move o seu mouse e não traz o navegador pra frente do jogo.
    /// Sempre fecha a última aba e lê as posições de novo, porque as abas mudam de lugar a cada fechamento.
    /// </summary>
    private static int CloseByMiddleClick(AutomationElement window)
    {
        var hwnd = new IntPtr(window.Current.NativeWindowHandle);
        if (hwnd == IntPtr.Zero || IsIconic(hwnd)) return 0; // minimizada: as abas não têm posição na tela

        int sent = 0, stuck = 0;
        int last = int.MaxValue;
        for (int guard = 0; guard < 60 && stuck < 3; guard++)
        {
            var tabs = RegularTabs(window);
            if (tabs.Count <= 1) break;
            if (tabs.Count >= last) stuck++; else stuck = 0;
            last = tabs.Count;

            var r = tabs[^1].Current.BoundingRectangle;
            if (r.IsEmpty || r.Width < 10 || r.Height < 10) break;
            // Um pouco à esquerda do meio: longe do "x" e do ícone de som
            var pt = new POINT { X = (int)(r.Left + r.Width * 0.4), Y = (int)(r.Top + r.Height / 2) };
            if (!ScreenToClient(hwnd, ref pt)) break;
            var lp = (IntPtr)(((pt.Y & 0xFFFF) << 16) | (pt.X & 0xFFFF));
            PostMessageW(hwnd, WM_MOUSEMOVE, IntPtr.Zero, lp);
            PostMessageW(hwnd, WM_MBUTTONDOWN, (IntPtr)MK_MBUTTON, lp);
            PostMessageW(hwnd, WM_MBUTTONUP, IntPtr.Zero, lp);
            sent++;
            Thread.Sleep(220);
        }
        // Tira o "mouse" de cima da barra de abas, pra elas voltarem ao tamanho normal
        PostMessageW(hwnd, WM_MOUSELEAVE, IntPtr.Zero, IntPtr.Zero);
        return sent;
    }

    /// <summary>
    /// As abas que contam pra "manter a 1ª e fechar o resto": sem as páginas iniciais do navegador (GX Corner,
    /// Speed Dial, Nova guia). Elas ficam abertas e não ocupam o lugar da 1ª aba de verdade.
    /// </summary>
    private static List<AutomationElement> RegularTabs(AutomationElement window) =>
        BrowserTabs(window).Where(t => !IsStartPage(SafeName(t))).ToList();

    private static string? SafeName(AutomationElement el) { try { return el.Current.Name; } catch { return null; } }

    private static readonly string[] StartPages =
    {
        "GX Corner", "Speed Dial", "Discagem rápida", "Nova guia", "Nova aba", "New Tab", "Página inicial", "Start page",
    };

    /// <summary>
    /// Página inicial do navegador? O nome da aba pode vir com o uso de memória junto
    /// ("Nova guia - Uso de memória - 50 MB" no Edge, "Uso da memória em Nova guia: 50 MB" no Chrome).
    /// </summary>
    internal static bool IsStartPage(string? tabName)
    {
        if (string.IsNullOrWhiteSpace(tabName)) return false;
        var title = System.Text.RegularExpressions.Regex.Replace(tabName.Trim(), @"\s+-\s+Uso de mem[oó]ria\s+-\s+[\d.,]+\s*[KMG]B$", "", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        var m = System.Text.RegularExpressions.Regex.Match(title, @"^Uso da mem[oó]ria em (.+):\s*[\d.,]+\s*[KMG]B$", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        if (m.Success) title = m.Groups[1].Value;
        return StartPages.Any(sp => title.Trim().Equals(sp, StringComparison.OrdinalIgnoreCase));
    }

    public static int CountTabs(BrowserDef? browser = null, ISet<int>? onlyPids = null) =>
        FindWindows((browser ?? Browsers.Edge).Process, onlyPids).Sum(w => BrowserTabs(w).Count);

    /// <summary>
    /// Só as abas da barra de abas do navegador. Sites também têm "abas" (GitHub, YouTube, painéis com abas)
    /// e apps do Edge instalados (PWA) não têm barra de abas: nada disso pode contar como aba nem como janela do navegador.
    /// O que é de site fica dentro de um "documento"; a barra de abas do navegador não.
    /// </summary>
    private static List<AutomationElement> BrowserTabs(AutomationElement window) =>
        window.FindAll(TreeScope.Descendants, new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.TabItem))
              .Cast<AutomationElement>()
              .Where(t => IsNativeTab(Ancestors(t, window)))
              .ToList();

    private static IEnumerable<ControlType> Ancestors(AutomationElement el, AutomationElement window)
    {
        var walker = TreeWalker.ControlViewWalker;
        var stop = window.Current.NativeWindowHandle;
        AutomationElement? cur = el;
        for (int depth = 0; depth < 40; depth++)
        {
            try { cur = walker.GetParent(cur); } catch { yield break; }
            if (cur is null) yield break;
            ControlType? type = null;
            int handle = 0;
            try { type = cur.Current.ControlType; handle = cur.Current.NativeWindowHandle; } catch { }
            if (type is not null) yield return type;
            if (handle != 0 && handle == stop) yield break;
        }
    }

    /// <summary>Aba nativa = nenhum ancestral é um documento (página da web).</summary>
    internal static bool IsNativeTab(IEnumerable<ControlType> ancestors) => !ancestors.Contains(ControlType.Document);

    /// <summary>
    /// Acha as janelas do navegador só pela lista de janelas do Windows (classe + PID), sem a automação
    /// passar por nenhuma outra janela, nem a do jogo. A ordem é a de cima pra baixo (usada por último primeiro).
    /// Edge, Chrome e Opera usam a mesma classe de janela do Chromium.
    /// </summary>
    /// <summary>
    /// O navegador tem janela aberta, mas não mostra a barra de abas pro Windows (acontece com o Opera GX em
    /// algumas sessões: a acessibilidade dele fica desligada). Aí não dá pra achar as abas; o limite de RAM continua valendo.
    /// </summary>
    public static bool TabsHidden(BrowserDef browser, ISet<int>? onlyPids = null) =>
        WindowHandles(browser.Process, onlyPids).Count > 0 && FindWindows(browser.Process, onlyPids).Count == 0;

    public static string HiddenMessage(BrowserDef browser) =>
        $"o {browser.Name} está aberto mas não está mostrando as abas pro Windows agora, então elas ficam; o limite de RAM continua valendo";

    private static List<IntPtr> WindowHandles(string process, ISet<int>? onlyPids)
    {
        // PIDs do navegador pela lista de processos do Windows (sem abrir nenhum processo)
        var edgePids = Process.GetProcessesByName(process).Select(p => { using (p) return p.Id; }).ToHashSet();
        if (onlyPids is not null) edgePids.IntersectWith(onlyPids);

        var handles = new List<IntPtr>();
        var cls = new System.Text.StringBuilder(64);
        EnumWindows((hwnd, _) =>
        {
            GetWindowThreadProcessId(hwnd, out var pid);
            if (!edgePids.Contains((int)pid) || !IsWindowVisible(hwnd)) return true;
            cls.Clear();
            if (GetClassNameW(hwnd, cls, cls.Capacity) > 0 && cls.ToString() == "Chrome_WidgetWin_1") handles.Add(hwnd);
            return true;
        }, IntPtr.Zero);
        return handles;
    }

    private static List<AutomationElement> FindWindows(string process, ISet<int>? onlyPids)
    {
        var handles = WindowHandles(process, onlyPids);
        var result = new List<AutomationElement>();
        foreach (var hwnd in handles)
        {
            try
            {
                var w = AutomationElement.FromHandle(hwnd);
                // Janela de navegador tem barra de abas de verdade; popups, notificações e apps instalados (PWA) não
                if (BrowserTabs(w).Count == 0) continue;
                result.Add(w);
            }
            catch { }
        }
        return result;
    }

    private const uint WM_MOUSEMOVE = 0x0200, WM_MBUTTONDOWN = 0x0207, WM_MBUTTONUP = 0x0208, WM_MOUSELEAVE = 0x02A3;
    private const int MK_MBUTTON = 0x0010;
    [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
    private struct POINT { public int X; public int Y; }
    [System.Runtime.InteropServices.DllImport("user32.dll")] private static extern bool ScreenToClient(IntPtr hwnd, ref POINT pt);
    [System.Runtime.InteropServices.DllImport("user32.dll")] private static extern bool PostMessageW(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam);
    [System.Runtime.InteropServices.DllImport("user32.dll")] private static extern bool IsIconic(IntPtr hwnd);

    private delegate bool EnumWindowsProc(IntPtr hwnd, IntPtr lParam);
    [System.Runtime.InteropServices.DllImport("user32.dll")] private static extern bool EnumWindows(EnumWindowsProc cb, IntPtr lParam);
    [System.Runtime.InteropServices.DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr hwnd, out uint pid);
    [System.Runtime.InteropServices.DllImport("user32.dll")] private static extern bool IsWindowVisible(IntPtr hwnd);
    [System.Runtime.InteropServices.DllImport("user32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode)]
    private static extern int GetClassNameW(IntPtr hwnd, System.Text.StringBuilder name, int max);

    /// <summary>O "x" da aba ("Fechar guia", "Fechar", "Close tab"). Nunca aperta outro botão (ex.: o de som/mudo).</summary>
    private static AutomationElement? CloseButton(AutomationElement tab) =>
        tab.FindAll(TreeScope.Descendants, new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Button))
           .Cast<AutomationElement>()
           .LastOrDefault(b => IsCloseLabel(b.Current.Name));

    internal static bool IsCloseLabel(string? name) =>
        name is not null && (name.StartsWith("Fechar", StringComparison.OrdinalIgnoreCase)
                             || name.StartsWith("Close", StringComparison.OrdinalIgnoreCase));
}
