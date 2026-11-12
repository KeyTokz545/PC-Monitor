using System;
using System.Drawing;
using System.IO;
using System.Windows.Forms;
using QRCoder;

namespace PCMonitor;

/// <summary>Fenêtre « Connecter un téléphone » : QR code (iPhone) et code à 6 chiffres (appli Android).</summary>
public sealed class PairForm : Form
{
    readonly AppConfig cfg;
    readonly PictureBox qr = new() { SizeMode = PictureBoxSizeMode.Zoom, Size = new Size(260, 260), BackColor = Color.White };
    readonly Label code = new() { AutoSize = false, TextAlign = ContentAlignment.MiddleCenter };
    readonly Label url = new() { AutoSize = false, TextAlign = ContentAlignment.MiddleCenter };

    static readonly Color Ink = Color.FromArgb(14, 24, 34);
    static readonly Color PanelC = Color.FromArgb(21, 33, 45);
    static readonly Color TextC = Color.FromArgb(228, 236, 242);
    static readonly Color Muted = Color.FromArgb(127, 147, 166);
    static readonly Color Cool = Color.FromArgb(79, 195, 224);

    public PairForm(AppConfig cfg, Icon icon)
    {
        this.cfg = cfg;
        Text = "Keystone : connecter un téléphone";
        Icon = icon;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        MinimizeBox = false;
        StartPosition = FormStartPosition.CenterScreen;
        BackColor = Ink;
        ForeColor = TextC;
        Font = new Font("Segoe UI", 10f);
        AutoScaleMode = AutoScaleMode.Dpi;
        ClientSize = new Size(460, 640);

        var title = new Label
        {
            Text = "Connecter un téléphone",
            Font = new Font("Segoe UI Semibold", 15f),
            AutoSize = false,
            Size = new Size(420, 36),
            Location = new Point(20, 18),
        };
        qr.Location = new Point((460 - 260) / 2, 64);

        var codeTitle = new Label
        {
            Text = "Code de connexion",
            ForeColor = Muted,
            AutoSize = false,
            TextAlign = ContentAlignment.MiddleCenter,
            Size = new Size(420, 22),
            Location = new Point(20, 336),
        };
        code.Font = new Font("Segoe UI Semibold", 26f);
        code.Size = new Size(420, 48);
        code.Location = new Point(20, 358);
        url.ForeColor = Muted;
        url.Size = new Size(420, 22);
        url.Location = new Point(20, 408);

        var help = new Label
        {
            AutoSize = false,
            Size = new Size(420, 120),
            Location = new Point(20, 440),
            ForeColor = TextC,
            Text = "iPhone : ouvre l'appareil photo et vise le QR code, puis dans Safari touche Partager → « Sur l'écran d'accueil ».\n\n" +
                   "Android (appli Keystone) : choisis ce PC dans la liste et tape le code ci-dessus.\n\n" +
                   "Le téléphone doit être sur le même Wi-Fi que le PC.",
        };

        var renew = new Button
        {
            Text = "Nouveau code",
            FlatStyle = FlatStyle.Flat,
            ForeColor = Cool,
            BackColor = PanelC,
            Size = new Size(200, 40),
            Location = new Point(20, 580),
        };
        renew.FlatAppearance.BorderColor = PanelC;
        renew.Click += (s, e) =>
        {
            var ok = MessageBox.Show(this,
                "Les téléphones déjà connectés devront scanner le nouveau QR code ou taper le nouveau code.\nContinuer ?",
                "Nouveau code", MessageBoxButtons.YesNo, MessageBoxIcon.Question);
            if (ok != DialogResult.Yes) return;
            cfg.Token = AppConfig.NewToken();
            cfg.Save();
            Fill();
        };

        var close = new Button
        {
            Text = "Fermer",
            FlatStyle = FlatStyle.Flat,
            ForeColor = Ink,
            BackColor = Cool,
            Size = new Size(200, 40),
            Location = new Point(240, 580),
            DialogResult = DialogResult.OK,
        };
        close.FlatAppearance.BorderColor = Cool;
        AcceptButton = close;

        Controls.AddRange(new Control[] { title, qr, codeTitle, code, url, help, renew, close });
        Fill();
    }

    void Fill()
    {
        var link = $"http://{NetInfo.LocalIp()}:{cfg.Port}/?k={cfg.Token}";
        url.Text = link;
        code.Text = cfg.Token.Length == 6 ? cfg.Token[..3] + " " + cfg.Token[3..] : cfg.Token;
        try
        {
            using var gen = new QRCodeGenerator();
            using var data = gen.CreateQrCode(link, QRCodeGenerator.ECCLevel.M);
            var png = new PngByteQRCode(data).GetGraphic(10);
            var old = qr.Image;
            qr.Image = Image.FromStream(new MemoryStream(png));
            old?.Dispose();
        }
        catch (Exception e)
        {
            Log.Error("QR code", e);
        }
    }
}
