using System;
using System.Drawing;
using System.Windows;
using Forms = System.Windows.Forms;

namespace Relight.Services;

internal sealed class TrayService : IDisposable
{
    private readonly Forms.NotifyIcon _icon;
    private readonly Forms.ContextMenuStrip _menu;
    private readonly Forms.ToolStripMenuItem _startupItem;
    private readonly Forms.ToolStripMenuItem _pauseAllItem;
    private readonly Forms.ToolStripMenuItem _resumeAllItem;
    private readonly Icon _image;

    public TrayService(Action open, Action add, Action history, Action pauseAll,
        Action resumeAll, Action toggleStartup, Action exit)
    {
        using var resource = Application.GetResourceStream(new Uri("pack://application:,,,/Assets/Relight.ico"))!.Stream;
        _image = new Icon(resource);
        _menu = new Forms.ContextMenuStrip();
        _menu.Items.Add("Open dashboard", null, (_, _) => open());
        _menu.Items.Add("Add application", null, (_, _) => add());
        _menu.Items.Add(new Forms.ToolStripSeparator());
        _pauseAllItem = new Forms.ToolStripMenuItem("Pause all", null,
            (_, _) => pauseAll()) { Enabled = false };
        _resumeAllItem = new Forms.ToolStripMenuItem("Resume all", null,
            (_, _) => resumeAll()) { Enabled = false };
        _menu.Items.Add(_pauseAllItem);
        _menu.Items.Add(_resumeAllItem);
        _menu.Items.Add("View history", null, (_, _) => history());
        _startupItem = new Forms.ToolStripMenuItem("Start at sign-in", null,
            (_, _) => toggleStartup()) { Enabled = false };
        _menu.Items.Add(_startupItem);
        _menu.Items.Add(new Forms.ToolStripSeparator());
        _menu.Items.Add("Exit Relight", null, (_, _) => exit());

        // NotifyIcon handles TaskbarCreated to restore itself after Explorer restarts.
        _icon = new Forms.NotifyIcon
        {
            Icon = _image,
            Text = "Relight · Loading monitoring status",
            ContextMenuStrip = _menu,
            Visible = true
        };
        _icon.DoubleClick += (_, _) => open();
    }

    public void UpdateStatus(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return;
        _icon.Text = text.Length > 63 ? text[..63] : text;
    }

    public void UpdateStartupStatus(bool enabled, bool available, string explanation)
    {
        _startupItem.Checked = enabled;
        _startupItem.Enabled = available;
        _startupItem.ToolTipText = explanation;
    }

    public void UpdatePauseAvailability(bool canPause, bool canResume)
    {
        _pauseAllItem.Enabled = canPause;
        _resumeAllItem.Enabled = canResume;
    }

    public void Dispose()
    {
        _icon.Visible = false;
        _icon.Dispose();
        _menu.Dispose();
        _image.Dispose();
    }
}
