using System.Drawing;
using System.Windows.Forms;

namespace EffectApk.UI;

/// <summary>Минимальный системный трей — единственный постоянный UI приложения.</summary>
public sealed class TrayIconService : IDisposable
{
    private readonly NotifyIcon _icon;

    public TrayIconService(
        Action associateApk, Action openLog, Action stopRuntime, Action exit,
        Func<bool> isAutoStartEnabled, Action<bool> setAutoStart)
    {
        var autoStartItem = new ToolStripMenuItem("Запускать при входе в Windows")
        {
            CheckOnClick = true,
            Checked = isAutoStartEnabled(),
        };
        autoStartItem.CheckedChanged += (_, _) => setAutoStart(autoStartItem.Checked);

        var menu = new ContextMenuStrip();
        menu.Items.Add("Назначить обработчиком .apk", null, (_, _) => associateApk());
        menu.Items.Add(autoStartItem);
        menu.Items.Add("Открыть лог", null, (_, _) => openLog());
        menu.Items.Add("Остановить Android-рантайм", null, (_, _) => stopRuntime());
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("Выход", null, (_, _) => exit());

        _icon = new NotifyIcon
        {
            Icon = LoadIcon(),
            Text = "EffectAPK",
            Visible = true,
            ContextMenuStrip = menu,
        };
    }

    public void Balloon(string text) =>
        _icon.ShowBalloonTip(4000, "EffectAPK", text, ToolTipIcon.Info);

    private static Icon LoadIcon()
    {
        try
        {
            return Icon.ExtractAssociatedIcon(Environment.ProcessPath!) ?? SystemIcons.Application;
        }
        catch
        {
            return SystemIcons.Application;
        }
    }

    public void Dispose()
    {
        _icon.Visible = false;
        _icon.Dispose();
    }
}
