using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Drawing.Text;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace PCMonitor;

/// <summary>
/// Petit affichage animé dans un coin de l'écran du PC quand on change un réglage depuis l'appli ou
/// l'icône (ventilation, profil CPU, micro…) : le logo Keystone se dessine, puis le choix s'affiche.
/// La fenêtre laisse passer les clics et ne prend jamais le focus : le jeu garde la main.
/// </summary>
public static class Osd
{
    public static readonly Color Red = Color.FromArgb(0xFF, 0x4D, 0x5E);
    public static readonly Color Green = Color.FromArgb(0x5B, 0xE0, 0x8A);
    static readonly Color Blue = Color.FromArgb(0x7A, 0xA2, 0xFF);
    static readonly Color Hot = Color.FromArgb(0xFF, 0x6A, 0x3D);
    static readonly Color Grey = Color.FromArgb(0x8A, 0x93, 0xA0);

    static Control ui;
    static AppConfig cfg;
    static OsdWindow win;

    public static void Init(Control uiThread, AppConfig config)
    {
        ui = uiThread;
        cfg = config;
    }

    static bool Enabled => cfg != null && ui != null && cfg.Osd != null && cfg.Osd.On;

    /// <summary>Couleur d'un niveau : bleu (calme), accent (équilibré), orangé (à fond).</summary>
    static Color? Level(string id) => id switch { "silent" => Blue, "turbo" => Hot, _ => null };

    public static bool Mode(string id) =>
        Show("Ventilation", cfg?.ModeLabel(id) ?? ModeDefs.Label(id), Level(id), Array.IndexOf(ModeDefs.Order, id), 3);

    public static bool CpuProfile(string id) =>
        Show("Profil CPU", CpuTuner.ProfileLabel(id), Level(id), Array.IndexOf(ModeDefs.Order, id), 3);

    /// <summary>Réglage à deux états. Couleur : celle donnée, sinon accent si actif et gris sinon.</summary>
    public static bool Toggle(string what, string state, bool on, Color? color = null) =>
        Show(what, state, color ?? (on ? (Color?)null : Grey), -1, 0, true);

    public static void Test() => Mode(ModeDefs.Known(cfg?.LastMode) ? cfg.LastMode : "balanced");

    static bool Show(string caption, string value, Color? color, int seg, int segs, bool tint = false)
    {
        if (!Enabled || string.IsNullOrEmpty(value)) return false;
        var accent = Parse(cfg.Osd.Accent) ?? Color.FromArgb(0x4F, 0xC3, 0xE0);
        var item = new OsdItem
        {
            Caption = caption.ToUpperInvariant(),
            Value = value,
            Accent = accent,
            Color = color ?? accent,
            Seg = seg,
            Segs = segs,
            TintValue = tint,
        };
        var corner = cfg.Osd.Corner;
        try
        {
            ui.BeginInvoke(new Action(() =>
            {
                try { (win ??= new OsdWindow()).Play(item, corner); }
                catch (Exception e) { Log.Error("Affichage sur l'écran", e); }
            }));
            return true;
        }
        catch { return false; }
    }

    static Color? Parse(string hex)
    {
        try { return hex != null && hex.Length == 7 && hex[0] == '#' ? ColorTranslator.FromHtml(hex) : null; }
        catch { return null; }
    }
}

sealed class OsdItem
{
    public string Caption, Value;
    public Color Accent, Color;
    public int Seg, Segs;
    public bool TintValue;
}

/// <summary>Fenêtre « calque » transparente (pixels semi-transparents, coins arrondis lissés).</summary>
sealed class OsdWindow : NativeWindow
{
    // durées en millisecondes
    const int In = 220, Hold = 2100, Out = 380;
    const int W = 300, H = 76, M = 18; // carte, et marge pour l'ombre (en pixels à 100 %)

    readonly System.Windows.Forms.Timer timer = new() { Interval = 15 };
    OsdItem item;
    string corner = "tr";
    long start;
    bool intro, shown;
    float s = 1;
    Rectangle area;
    Bitmap bmp;
    Font capFont, valFont;

    public OsdWindow()
    {
        var cp = new CreateParams
        {
            Caption = "Keystone",
            Style = unchecked((int)0x80000000), // WS_POPUP
            ExStyle = 0x80000 | 0x20 | 0x80 | 0x08000000 | 0x8, // calque, transparent aux clics, outil, sans focus, au-dessus
        };
        CreateHandle(cp);
        timer.Tick += (sender, e) => Frame();
    }

    static long Now => Environment.TickCount64;

    public void Play(OsdItem next, string where)
    {
        var t = Now - start;
        // déjà à l'écran : on change le contenu sans refaire l'apparition
        intro = !(shown && t < In + Hold + Out / 2);
        item = next;
        if (intro || where != corner) Place(where);
        corner = where;
        start = intro ? Now : Now - In;
        if (!shown)
        {
            ShowWindow(Handle, 4); // SW_SHOWNOACTIVATE
            shown = true;
        }
        SetWindowPos(Handle, new IntPtr(-1), 0, 0, 0, 0, 0x1 | 0x2 | 0x10 | 0x40); // au premier plan, sans prendre le focus
        Frame();
        timer.Start();
    }

    /// <summary>Écran où se trouve le jeu / la fenêtre active, à sa résolution réelle.</summary>
    void Place(string where)
    {
        var fg = GetForegroundWindow();
        var scr = fg != IntPtr.Zero ? Screen.FromHandle(fg) : Screen.PrimaryScreen;
        area = scr.WorkingArea;
        try
        {
            var mon = MonitorFromWindow(fg, 1);
            if (GetDpiForMonitor(mon, 0, out var dx, out _) == 0 && dx > 0) s = dx / 96f;
        }
        catch { s = 1; }
        var bw = (int)Math.Ceiling((W + 2 * M) * s);
        var bh = (int)Math.Ceiling((H + 2 * M) * s);
        if (bmp == null || bmp.Width != bw || bmp.Height != bh)
        {
            bmp?.Dispose();
            bmp = new Bitmap(bw, bh, PixelFormat.Format32bppArgb);
        }
        capFont?.Dispose();
        valFont?.Dispose();
        capFont = new Font("Segoe UI", 11f * s, FontStyle.Bold, GraphicsUnit.Pixel);
        valFont = new Font("Segoe UI Semibold", 23f * s, FontStyle.Regular, GraphicsUnit.Pixel);
    }

    static float Clamp01(float v) => v < 0 ? 0 : v > 1 ? 1 : v;
    static float EaseOut(float v) { v = Clamp01(v); return 1 - (1 - v) * (1 - v) * (1 - v); }
    static float Back(float v) { v = Clamp01(v); const float c = 1.9f; return 1 + (c + 1) * MathF.Pow(v - 1, 3) + c * MathF.Pow(v - 1, 2); }

    void Frame()
    {
        if (item == null || bmp == null) return;
        var t = Now - start;
        if (t >= In + Hold + Out)
        {
            timer.Stop();
            ShowWindow(Handle, 0); // SW_HIDE
            shown = false;
            return;
        }
        // apparition / disparition : fondu + léger glissement depuis le bord
        var alpha = t < In ? EaseOut(t / (float)In) : t > In + Hold ? 1 - EaseOut((t - In - Hold) / (float)Out) : 1f;
        var slide = (1 - (t < In ? EaseOut(t / (float)In) : 1f)) * 18 * s + (t > In + Hold ? EaseOut((t - In - Hold) / (float)Out) * 8 * s : 0);
        var right = corner == "tr" || corner == "br";
        var bottom = corner == "bl" || corner == "br";
        var gap = 22 * s;
        var x = right ? area.Right - gap - (W + M) * s + slide : area.Left + gap - M * s - slide;
        var y = bottom ? area.Bottom - gap - (H + M) * s : area.Top + gap - M * s;

        Draw(t);
        Push((int)Math.Round(x), (int)Math.Round(y), (byte)Math.Round(255 * Clamp01(alpha)));
    }

    // ---------------------------------------------------------------- dessin
    static GraphicsPath Round(RectangleF r, float rad)
    {
        var p = new GraphicsPath();
        var d = rad * 2;
        p.AddArc(r.X, r.Y, d, d, 180, 90);
        p.AddArc(r.Right - d, r.Y, d, d, 270, 90);
        p.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
        p.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
        p.CloseFigure();
        return p;
    }

    static Color A(Color c, float a) => Color.FromArgb((int)Math.Round(255 * Clamp01(a) * c.A / 255f), c.R, c.G, c.B);

    static Color Mix(Color a, Color b, float k) => Color.FromArgb(
        (int)(a.R + (b.R - a.R) * k), (int)(a.G + (b.G - a.G) * k), (int)(a.B + (b.B - a.B) * k));

    void Draw(long t)
    {
        // temps de chaque animation (après un changement à l'écran : seulement l'impulsion et le texte)
        var stone = intro ? EaseOut((t - 40) / 480f) : 1f;          // le contour du logo se dessine
        var kT = intro ? t - 300 : t - In;                           // le K apparaît avec un rebond
        var ring = intro ? (t - 330) / 650f : (t - In) / 650f;       // onde qui s'élargit
        var txt = intro ? EaseOut((t - 110) / 320f) : EaseOut((t - In) / 260f);

        using var g = Graphics.FromImage(bmp);
        g.Clear(Color.Transparent);
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.PixelOffsetMode = PixelOffsetMode.HighQuality;
        g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
        g.ScaleTransform(s, s);

        var card = new RectangleF(M, M, W, H);
        // ombre douce
        for (var i = 6; i >= 1; i--)
        {
            using var sp = Round(RectangleF.Inflate(new RectangleF(card.X, card.Y + 3, card.Width, card.Height), i * 2.2f, i * 2.2f), 18 + i * 2.2f);
            using var sb = new SolidBrush(Color.FromArgb(9, 0, 0, 0));
            g.FillPath(sb, sp);
        }
        using (var cp = Round(card, 18))
        {
            using (var bg = new LinearGradientBrush(card, Color.FromArgb(246, 24, 27, 33), Color.FromArgb(246, 14, 16, 20), LinearGradientMode.Vertical))
                g.FillPath(bg, cp);
            using (var border = new Pen(A(item.Accent, .32f), 1f))
                g.DrawPath(border, cp);
            // barre de temps restant, en bas de la carte
            var left = t <= In ? 1f : 1 - Clamp01((t - In) / (float)Hold);
            if (left > 0)
            {
                var clip = g.Clip;
                g.SetClip(cp);
                using var bar = new SolidBrush(A(item.Color, .75f));
                g.FillRectangle(bar, card.X, card.Bottom - 2.5f, card.Width * left, 2.5f);
                g.Clip = clip;
            }
        }

        // tuile du logo
        var tile = new RectangleF(card.X + 14, card.Y + 14, 48, 48);
        var cx = tile.X + 24; var cy = tile.Y + 24;
        if (ring > 0 && ring < 1)
        {
            // onde qui part du logo, gardée à l'intérieur de la carte
            var r = 22 + 26 * EaseOut(ring);
            using var clipPath = Round(card, 18);
            var keep = g.Clip;
            g.SetClip(clipPath);
            using (var rp = new Pen(A(item.Color, (1 - ring) * .55f), 2f))
                g.DrawEllipse(rp, cx - r, cy - r, 2 * r, 2 * r);
            g.Clip = keep;
        }
        using (var tp = Round(tile, 13))
        {
            using (var tb = new SolidBrush(Color.FromArgb(255, 11, 13, 16)))
                g.FillPath(tb, tp);
            using (var tpen = new Pen(A(item.Color, .30f + .25f * (1 - Clamp01(ring))), 1.2f))
                g.DrawPath(tpen, tp);
        }
        // logo : clé de voûte (contour) + K, repère 48×48 comme l'icône
        var ox = tile.X; var oy = tile.Y + 2;
        PointF P(float px, float py) => new(ox + px, oy + py);
        var poly = new[] { P(10, 12), P(38, 12), P(34, 32), P(14, 32), P(10, 12) };
        var light = Mix(item.Accent, Color.White, .45f);
        using (var sg = new LinearGradientBrush(new RectangleF(ox + 8, oy + 10, 32, 24), light, item.Accent, LinearGradientMode.ForwardDiagonal))
        using (var spen = new Pen(sg, 2.6f) { LineJoin = LineJoin.Round, StartCap = LineCap.Round, EndCap = LineCap.Round })
            DrawPartial(g, spen, poly, stone);
        if (kT > 0)
        {
            var k = Clamp01(kT / 320f);
            var sc = .55f + .45f * Back(k);
            var st = g.Save();
            g.TranslateTransform(ox + 23.5f, oy + 22);
            g.ScaleTransform(sc, sc);
            g.TranslateTransform(-(ox + 23.5f), -(oy + 22));
            using var kp = new Pen(A(item.Color, EaseOut(k * 1.6f)), 2.6f) { StartCap = LineCap.Round, EndCap = LineCap.Round, LineJoin = LineJoin.Round };
            g.DrawLine(kp, P(19, 16.5f), P(19, 27.5f));
            g.DrawLines(kp, new[] { P(28, 16.5f), P(19, 22), P(28, 27.5f) });
            g.Restore(st);
        }

        // texte : rubrique + choix, qui glissent un peu en arrivant
        var tx = card.X + 76 + (1 - txt) * 10;
        var hasSegs = item.Segs > 1 && item.Seg >= 0;
        var capY = card.Y + (hasSegs ? 12 : 16);
        using (var cb = new SolidBrush(A(Color.FromArgb(150, 160, 172), txt)))
            g.DrawString(item.Caption, capFont, cb, tx, capY / 1f, StringFormat.GenericTypographic);
        var valColor = item.TintValue ? Mix(item.Color, Color.White, .12f) : Color.FromArgb(240, 243, 246);
        using (var vb = new SolidBrush(A(valColor, txt)))
        using (var fmt = new StringFormat(StringFormat.GenericTypographic) { Trimming = StringTrimming.EllipsisCharacter, FormatFlags = StringFormatFlags.NoWrap })
            g.DrawString(item.Value, valFont, vb, new RectangleF(tx, capY + 14, card.Right - tx - 12, 32), fmt);

        // niveaux (3 barres : la choisie s'allume)
        if (hasSegs)
        {
            for (var i = 0; i < item.Segs; i++)
            {
                var on = i == item.Seg;
                var appear = Clamp01(txt * 1.4f - i * .15f);
                var bw = 26f * (on ? (.4f + .6f * appear) : 1);
                var rect = new RectangleF(card.X + 76 + i * 31, card.Y + 57, bw, 4.5f);
                using var bp = Round(rect, 2.25f);
                using var bb = new SolidBrush(on ? A(item.Color, appear) : A(Color.FromArgb(52, 58, 66), txt));
                g.FillPath(bb, bp);
            }
        }
    }

    /// <summary>Trace la ligne brisée jusqu'à une fraction de sa longueur (dessin progressif).</summary>
    static void DrawPartial(Graphics g, Pen pen, PointF[] pts, float frac)
    {
        if (frac <= 0) return;
        float total = 0;
        for (var i = 1; i < pts.Length; i++) total += Dist(pts[i - 1], pts[i]);
        var left = total * Clamp01(frac);
        for (var i = 1; i < pts.Length && left > 0; i++)
        {
            var d = Dist(pts[i - 1], pts[i]);
            if (left >= d) { g.DrawLine(pen, pts[i - 1], pts[i]); left -= d; continue; }
            var k = left / d;
            g.DrawLine(pen, pts[i - 1], new PointF(pts[i - 1].X + (pts[i].X - pts[i - 1].X) * k, pts[i - 1].Y + (pts[i].Y - pts[i - 1].Y) * k));
            left = 0;
        }
    }

    static float Dist(PointF a, PointF b) => MathF.Sqrt((a.X - b.X) * (a.X - b.X) + (a.Y - b.Y) * (a.Y - b.Y));

    // ---------------------------------------------------------------- envoi à l'écran (pixels semi-transparents)
    void Push(int x, int y, byte alpha)
    {
        var screenDc = GetDC(IntPtr.Zero);
        var memDc = CreateCompatibleDC(screenDc);
        var hBmp = bmp.GetHbitmap(Color.FromArgb(0));
        var old = SelectObject(memDc, hBmp);
        try
        {
            var size = new SIZE { cx = bmp.Width, cy = bmp.Height };
            var src = new POINT { x = 0, y = 0 };
            var dst = new POINT { x = x, y = y };
            var blend = new BLENDFUNCTION { BlendOp = 0, BlendFlags = 0, SourceConstantAlpha = alpha, AlphaFormat = 1 };
            UpdateLayeredWindow(Handle, screenDc, ref dst, ref size, memDc, ref src, 0, ref blend, 2);
        }
        finally
        {
            SelectObject(memDc, old);
            DeleteObject(hBmp);
            DeleteDC(memDc);
            ReleaseDC(IntPtr.Zero, screenDc);
        }
    }

    [StructLayout(LayoutKind.Sequential)] struct POINT { public int x, y; }
    [StructLayout(LayoutKind.Sequential)] struct SIZE { public int cx, cy; }
    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    struct BLENDFUNCTION { public byte BlendOp, BlendFlags, SourceConstantAlpha, AlphaFormat; }

    [DllImport("user32.dll", SetLastError = true)]
    static extern bool UpdateLayeredWindow(IntPtr hwnd, IntPtr hdcDst, ref POINT pptDst, ref SIZE psize, IntPtr hdcSrc,
        ref POINT pprSrc, int crKey, ref BLENDFUNCTION pblend, int dwFlags);
    [DllImport("user32.dll")] static extern IntPtr GetDC(IntPtr hWnd);
    [DllImport("user32.dll")] static extern int ReleaseDC(IntPtr hWnd, IntPtr hDC);
    [DllImport("gdi32.dll")] static extern IntPtr CreateCompatibleDC(IntPtr hDC);
    [DllImport("gdi32.dll")] static extern bool DeleteDC(IntPtr hdc);
    [DllImport("gdi32.dll")] static extern IntPtr SelectObject(IntPtr hDC, IntPtr hObject);
    [DllImport("gdi32.dll")] static extern bool DeleteObject(IntPtr hObject);
    [DllImport("user32.dll")] static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);
    [DllImport("user32.dll")] static extern bool SetWindowPos(IntPtr hWnd, IntPtr after, int x, int y, int cx, int cy, uint flags);
    [DllImport("user32.dll")] static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] static extern IntPtr MonitorFromWindow(IntPtr hwnd, uint flags);
    [DllImport("shcore.dll")] static extern int GetDpiForMonitor(IntPtr hmonitor, int dpiType, out uint dpiX, out uint dpiY);
}
