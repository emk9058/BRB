using System.Drawing;
using System.Runtime.InteropServices;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;

public static class TrayHost
{
    public static void Open(WebApplication app, bool pageReady)
    {
        var thread = new Thread(() =>
        {
            Application.SetHighDpiMode(HighDpiMode.PerMonitorV2);
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            Application.Run(new TrayContext(app, ListenPort(app), pageReady));
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.IsBackground = true;
        thread.Start();
    }

    private static int ListenPort(WebApplication app)
    {
        var addresses = app.Services.GetRequiredService<IServer>()
            .Features.Get<IServerAddressesFeature>()?.Addresses;
        if (addresses is null)
            return 5105;

        foreach (var address in addresses)
        {
            if (Uri.TryCreate(address, UriKind.Absolute, out var uri) && uri.Port > 0)
                return uri.Port;
        }

        return 5105;
    }
}

sealed class TrayContext : ApplicationContext
{
    private readonly NotifyIcon icon;
    private readonly DeskWindow window;
    private bool quitting;

    public TrayContext(WebApplication app, int port, bool pageReady)
    {
        var iconImage = DeskWindow.AppIcon();
        window = new DeskWindow(port, pageReady) { Icon = iconImage };
        window.FormClosing += (_, e) =>
        {
            if (quitting)
                return;
            e.Cancel = true;
            window.Hide();
        };

        var menu = new ContextMenuStrip();
        menu.Items.Add("Open", null, (_, _) => ShowWindow());
        menu.Items.Add("Quit", null, (_, _) => Quit(app));

        icon = new NotifyIcon
        {
            Icon = iconImage,
            Text = "BRB",
            Visible = true,
            ContextMenuStrip = menu
        };
        icon.MouseClick += (_, e) =>
        {
            if (e.Button == MouseButtons.Left)
                ShowWindow();
        };
        icon.BalloonTipClicked += (_, _) => ShowWindow();

        ShowWindow();
        icon.BalloonTipTitle = "BRB";
        icon.BalloonTipText = "BRB is in the taskbar. Scan the code with a phone on the same Wi-Fi.";
        icon.ShowBalloonTip(4000);
    }

    private void ShowWindow()
    {
        window.Show();
        if (window.WindowState == FormWindowState.Minimized)
            window.WindowState = FormWindowState.Normal;
        window.Activate();
    }

    private void Quit(WebApplication app)
    {
        quitting = true;
        icon.Visible = false;
        icon.Dispose();
        window.Close();
        ExitThread();
        app.Lifetime.StopApplication();
    }
}

sealed class DeskWindow : Form
{
    private readonly int port;
    private readonly bool pageReady;
    private readonly PictureBox qr = new() { SizeMode = PictureBoxSizeMode.Zoom, BackColor = Color.White };
    private readonly Label address = new();
    private readonly Label extra = new();
    private readonly StatusCard phoneCard = new("Phone page");
    private readonly StatusCard leagueCard = new("League");
    private readonly System.Windows.Forms.Timer timer = new() { Interval = 2000 };
    private string? shownUrl;
    private int ticking;

    public DeskWindow(int port, bool pageReady)
    {
        this.port = port;
        this.pageReady = pageReady;

        Text = "BRB";
        FormBorderStyle = FormBorderStyle.FixedSingle;
        MaximizeBox = false;
        StartPosition = FormStartPosition.CenterScreen;
        ClientSize = new Size(440, 568);
        BackColor = Color.FromArgb(1, 10, 19);
        ForeColor = Color.FromArgb(240, 230, 210);
        Font = new Font("Segoe UI", 10);

        var kicker = Label("THIS PC", 11, FontStyle.Regular, Color.FromArgb(200, 170, 110), 28);
        var title = Label("BRB", 28, FontStyle.Regular, ForeColor, 50);
        title.Font = new Font("Times New Roman", 28, FontStyle.Regular);
        var lead = Label("Scan with a phone on the same Wi-Fi.", 11, FontStyle.Regular, Color.FromArgb(160, 155, 140), 108);

        var frame = new Panel
        {
            BackColor = Color.White,
            Size = new Size(248, 248),
            Location = new Point((ClientSize.Width - 248) / 2, 146)
        };
        qr.Dock = DockStyle.Fill;
        qr.Margin = new Padding(14);
        frame.Padding = new Padding(14);
        frame.Controls.Add(qr);

        address.AutoSize = false;
        address.TextAlign = ContentAlignment.MiddleCenter;
        address.ForeColor = Color.FromArgb(205, 190, 145);
        address.Font = new Font("Segoe UI", 12);
        address.Bounds = new Rectangle(24, 408, 392, 28);
        address.Text = "Looking for this PC’s address…";

        extra.AutoSize = false;
        extra.TextAlign = ContentAlignment.MiddleCenter;
        extra.ForeColor = Color.FromArgb(160, 155, 140);
        extra.Font = new Font("Segoe UI", 8.5f);
        extra.Bounds = new Rectangle(24, 438, 392, 22);

        phoneCard.Bounds = new Rectangle(36, 476, 176, 64);
        leagueCard.Bounds = new Rectangle(228, 476, 176, 64);

        address.Cursor = Cursors.Hand;
        address.Click += (_, _) => CopyAddress();

        Controls.Add(kicker);
        Controls.Add(title);
        Controls.Add(lead);
        Controls.Add(frame);
        Controls.Add(address);
        Controls.Add(extra);
        Controls.Add(phoneCard);
        Controls.Add(leagueCard);

        timer.Tick += OnTick;
        Shown += async (_, _) => await RefreshStatus();
        timer.Start();
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        var dark = 1;
        DwmSetWindowAttribute(Handle, 20, ref dark, sizeof(int));
    }

    private async void OnTick(object? sender, EventArgs e)
    {
        if (Interlocked.Exchange(ref ticking, 1) == 1)
            return;
        try
        {
            await RefreshStatus();
        }
        finally
        {
            ticking = 0;
        }
    }

    private async Task RefreshStatus()
    {
        var urls = HostDesk.PhoneUrls(port);
        var url = urls.FirstOrDefault();
        if (url is null)
        {
            address.Text = "No Wi-Fi address yet.";
            extra.Text = "";
            shownUrl = null;
            qr.Image = null;
        }
        else
        {
            address.Text = url;
            var others = urls.Where(item => !string.Equals(item, url, StringComparison.OrdinalIgnoreCase)).ToList();
            extra.Text = others.Count == 0 ? "Click the address to copy it." : "Also works: " + string.Join("   ·   ", others);
            if (!string.Equals(shownUrl, url, StringComparison.Ordinal))
            {
                shownUrl = url;
                var png = HostDesk.Qr(url);
                if (png is not null)
                {
                    using var stream = new MemoryStream(png);
                    using var loaded = Image.FromStream(stream);
                    var previous = qr.Image;
                    qr.Image = new Bitmap(loaded);
                    previous?.Dispose();
                }
            }
        }

        phoneCard.SetValue(pageReady ? "Ready" : "Not built", pageReady);

        try
        {
            var screen = await LeagueClient.Current();
            var open = screen.Phase != "closed";
            leagueCard.SetValue(open ? screen.Title : "Closed", open);
        }
        catch
        {
            leagueCard.SetValue("Unknown", false);
        }
    }

    private void CopyAddress()
    {
        if (string.IsNullOrWhiteSpace(shownUrl))
            return;
        try
        {
            Clipboard.SetText(shownUrl);
            extra.Text = "Copied.";
        }
        catch
        {
        }
    }

    private Label Label(string text, float size, FontStyle style, Color color, int top)
    {
        return new Label
        {
            Text = text,
            AutoSize = false,
            TextAlign = ContentAlignment.MiddleCenter,
            Font = new Font(Font.FontFamily, size, style),
            ForeColor = color,
            Bounds = new Rectangle(16, top, ClientSize.Width - 32, (int)(size + 18))
        };
    }

    public static Icon AppIcon()
    {
        try
        {
            var path = Environment.ProcessPath;
            if (!string.IsNullOrEmpty(path))
            {
                var associated = Icon.ExtractAssociatedIcon(path);
                if (associated is not null)
                    return associated;
            }
        }
        catch
        {
        }

        return DrawIcon();
    }

    private static Icon DrawIcon()
    {
        using var bmp = new Bitmap(32, 32, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
        using var graphics = Graphics.FromImage(bmp);
        graphics.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
        graphics.Clear(Color.Transparent);
        using var gold = new SolidBrush(Color.FromArgb(200, 170, 110));
        graphics.FillEllipse(gold, 1, 1, 30, 30);
        using var font = new Font("Segoe UI", 15, FontStyle.Bold, GraphicsUnit.Pixel);
        using var ink = new SolidBrush(Color.FromArgb(1, 10, 19));
        var measured = graphics.MeasureString("B", font);
        graphics.DrawString("B", font, ink, (32 - measured.Width) / 2f, (32 - measured.Height) / 2f - 1);
        return Icon.FromHandle(bmp.GetHicon());
    }

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);
}

sealed class StatusCard : Control
{
    private string valueText = "Checking";
    private bool good = true;

    public StatusCard(string caption)
    {
        Caption = caption;
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint | ControlStyles.OptimizedDoubleBuffer, true);
        BackColor = Color.FromArgb(1, 10, 19);
        Font = new Font("Segoe UI", 9);
    }

    public string Caption { get; }

    public void SetValue(string text, bool ok)
    {
        valueText = text;
        good = ok;
        Invalidate();
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var graphics = e.Graphics;
        graphics.Clear(BackColor);
        using var pen = new Pen(Color.FromArgb(115, 200, 170, 110));
        graphics.DrawRectangle(pen, 0, 0, Width - 1, Height - 1);

        using var captionBrush = new SolidBrush(Color.FromArgb(200, 170, 110));
        using var captionFont = new Font("Segoe UI", 8, FontStyle.Regular);
        var caption = graphics.MeasureString(Caption.ToUpperInvariant(), captionFont);
        graphics.DrawString(Caption.ToUpperInvariant(), captionFont, captionBrush, (Width - caption.Width) / 2, 8);

        using var valueBrush = new SolidBrush(good ? Color.FromArgb(10, 200, 185) : Color.FromArgb(255, 180, 176));
        using var valueFont = new Font("Segoe UI", 12, FontStyle.Regular);
        var valueSize = graphics.MeasureString(valueText, valueFont);
        graphics.DrawString(valueText, valueFont, valueBrush, (Width - valueSize.Width) / 2, 30);
    }
}
