using System;
using System.Drawing;
using System.Windows;
using Forms = System.Windows.Forms;

namespace Relight.Services;

internal sealed class TrayService : IDisposable
{
    private readonly Forms.NotifyIcon _icon;
    private readonly Forms.ContextMenuStrip _menu;
    private readonly Icon _image;

    public TrayService(Action open, Action history, Action exit)
    {
        using var resource = Application.GetResourceStream(new Uri("pack://application:,,,/Assets/Relight.ico"))!.Stream;
        _image = new Icon(resource);
        _menu = new Forms.ContextMenuStrip();
        _menu.Items.Add("Open dashboard", null, (_, _) => open());
        _menu.Items.Add(Unavailable("Add application"));
        _menu.Items.Add(new Forms.ToolStripSeparator());
        _menu.Items.Add(Unavailable("Pause all"));
        _menu.Items.Add(Unavailable("Resume all"));
        _menu.Items.Add("View history", null, (_, _) => history());
        _menu.Items.Add(Unavailable("Start at sign-in"));
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

    private static Forms.ToolStripMenuItem Unavailable(string text) => new(text)
    {
        Enabled = false,
        ToolTipText = "Available in a later milestone."
    };

    public void UpdateStatus(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return;
        _icon.Text = text.Length > 63 ? text[..63] : text;
    }

    public void Dispose()
    {
        _icon.Visible = false;
        _icon.Dispose();
        _menu.Dispose();
        _image.Dispose();
    }
}
