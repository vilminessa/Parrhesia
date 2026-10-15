using System.Drawing;
using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace Parrhesia.App;

/// <summary>
/// Иконка в трее (U3): «Открыть» по двойному клику, меню «Открыть/Выход».
/// Создаётся при первом сворачивании в трей (CloseToTray); умирает вместе
/// с закрытым главным окном.
/// </summary>
internal sealed class TrayService : IDisposable
{
    private readonly NotifyIcon _icon;

    public TrayService(Action onOpen, Action onExit)
    {
        _icon = new NotifyIcon
        {
            Icon = CreateAppIcon(),
            Text = "Parrhesia — модульный аудио-миксер",
            Visible = true,
        };

        var menu = new ContextMenuStrip();
        menu.Items.Add("Открыть Parrhesia", null, (_, _) => onOpen());
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("Выход", null, (_, _) => onExit());
        _icon.ContextMenuStrip = menu;
        _icon.DoubleClick += (_, _) => onOpen();
    }

    public void Dispose()
    {
        _icon.Visible = false;
        _icon.Dispose();
    }

    /// <summary>Иконка «P» на фирменном фоне (без ресурсов-файлов — кодоген).</summary>
    private static Icon CreateAppIcon()
    {
        using var bitmap = new Bitmap(32, 32);
        using (var graphics = Graphics.FromImage(bitmap))
        {
            graphics.SmoothingMode = SmoothingMode.AntiAlias;
            graphics.Clear(Color.Transparent);

            using var fill = new SolidBrush(Color.FromArgb(255, 53, 208, 200)); // Brush.Cyan
            graphics.FillEllipse(fill, 1, 1, 30, 30);

            using var text = new Font("Segoe UI", 15f, FontStyle.Bold, GraphicsUnit.Pixel);
            var format = new StringFormat
            {
                Alignment = StringAlignment.Center,
                LineAlignment = StringAlignment.Center,
            };
            graphics.DrawString("P", text, System.Drawing.Brushes.Black, new RectangleF(0, 0, 32, 32), format);
        }

        var handle = bitmap.GetHicon();
        try
        {
            using var temp = Icon.FromHandle(handle);
            return (Icon)temp.Clone(); // копия — hicon освободим
        }
        finally
        {
            DestroyIcon(handle);
        }
    }

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DestroyIcon(IntPtr handle);
}
