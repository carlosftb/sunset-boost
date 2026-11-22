using Forms = System.Windows.Forms;
using Drawing = System.Drawing;

namespace SunsetBoost.App;

/// <summary>Ícone perto do relógio, com menu rápido.</summary>
public sealed class TrayIcon : IDisposable
{
    private readonly Forms.NotifyIcon _icon;
    private readonly Forms.ToolStripMenuItem _boostItem;
    private readonly Drawing.Icon _image;

    public TrayIcon(Action open, Action toggleBoost, Action exit)
    {
        _image = DrawSunIcon();
        _boostItem = new Forms.ToolStripMenuItem("Entrar no Modo Jogo", null, (_, _) => toggleBoost());
        var menu = new Forms.ContextMenuStrip();
        menu.Items.Add(new Forms.ToolStripMenuItem("Abrir o Sunset Boost", null, (_, _) => open()));
        menu.Items.Add(_boostItem);
        menu.Items.Add(new Forms.ToolStripSeparator());
        menu.Items.Add(new Forms.ToolStripMenuItem("Sair", null, (_, _) => exit()));

        _icon = new Forms.NotifyIcon { Icon = _image, Text = "Sunset Boost", Visible = true, ContextMenuStrip = menu };
        _icon.DoubleClick += (_, _) => open();
    }

    public void SetBoostActive(bool active)
    {
        _boostItem.Text = active ? "Sair do Modo Jogo (restaurar)" : "Entrar no Modo Jogo";
        _icon.Text = active ? "Sunset Boost · Modo Jogo ATIVO" : "Sunset Boost";
    }

    public void Notify(string title, string text) => _icon.ShowBalloonTip(3000, title, text, Forms.ToolTipIcon.None);

    /// <summary>Desenha o ícone na hora: sol listrado de pôr do sol (nada de arquivo de imagem).</summary>
    private static Drawing.Icon DrawSunIcon()
    {
        using var bmp = new Drawing.Bitmap(32, 32);
        using (var g = Drawing.Graphics.FromImage(bmp))
        {
            g.SmoothingMode = Drawing.Drawing2D.SmoothingMode.AntiAlias;
            g.Clear(Drawing.Color.Transparent);
            using var brush = new Drawing.Drawing2D.LinearGradientBrush(new Drawing.Rectangle(0, 0, 32, 32),
                Drawing.Color.FromArgb(255, 227, 138), Drawing.Color.FromArgb(255, 79, 139), 90f);
            g.FillEllipse(brush, 1, 1, 30, 30);
            using var stripe = new Drawing.SolidBrush(Drawing.Color.FromArgb(20, 11, 46));
            foreach (var y in new[] { 17, 22, 26 }) g.FillRectangle(stripe, 0, y, 32, y == 17 ? 2 : 3);
        }
        return Drawing.Icon.FromHandle(bmp.GetHicon());
    }

    public void Dispose()
    {
        _icon.Visible = false;
        _icon.Dispose();
        _image.Dispose();
    }
}
