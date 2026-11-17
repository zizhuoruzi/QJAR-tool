using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Text;
using System.Windows.Forms;

/* Astryx 设计 token 在本工具里的唯一落点：颜色、字号、间距、圆角、动效。
   取值见 Docs/astryx-ui-spec.md，不要在别处再写字面量。 */
internal static class Theme
{
    // ---- 颜色（浅色主题，token 见 astryx-ui-spec.md 第 1 节）----------------
    internal static readonly Color Body = Color.FromArgb(0xF1, 0xF4, 0xF7);          // background-body
    internal static readonly Color Surface = Color.White;                             // background-surface
    internal static readonly Color Border = Color.FromArgb(0xE6, 0xEB, 0xF0);         // rgba(5,54,89,.1) on white
    internal static readonly Color BorderStrong = Color.FromArgb(0xD5, 0xDE, 0xE6);

    internal static readonly Color TextPrimary = Color.FromArgb(0x0A, 0x13, 0x17);
    internal static readonly Color TextSecondary = Color.FromArgb(0x4E, 0x60, 0x6F);
    internal static readonly Color TextDisabled = Color.FromArgb(0xA4, 0xB0, 0xBC);

    internal static readonly Color Accent = Color.FromArgb(0x00, 0x64, 0xE0);
    internal static readonly Color OnAccent = Color.White;
    internal static readonly Color AccentMuted = Color.FromArgb(0xCC, 0xE6, 0xFE);    // #0082FB33 on white
    internal static readonly Color AccentDisabled = Color.FromArgb(0xBF, 0xCF, 0xE0);
    internal static readonly Color AccentBorder = Color.FromArgb(0xB2, 0xD0, 0xF5);   // accent 30% on white

    internal static readonly Color Success = Color.FromArgb(0x0D, 0x86, 0x26);
    internal static readonly Color SuccessMuted = Color.FromArgb(0xCE, 0xEB, 0xD2);   // #0B991F33 on white
    internal static readonly Color Warning = Color.FromArgb(0x9A, 0x6B, 0x00);        // 文本用，保证 4.5:1
    internal static readonly Color WarningMuted = Color.FromArgb(0xF9, 0xED, 0xCC);   // #E2A40033 on white
    internal static readonly Color Error = Color.FromArgb(0xE3, 0x19, 0x3B);
    internal static readonly Color ErrorMuted = Color.FromArgb(0xF9, 0xD1, 0xD8);     // #E3193B33 on white
    internal static readonly Color ErrorRing = Color.FromArgb(0x3D, 0xE3, 0x19, 0x3B); // inset 2px, 30%

    internal static readonly Color OverlayHover = Color.FromArgb(0xF3, 0xF6, 0xF7);   // #0536590C on white
    internal static readonly Color OverlayPressed = Color.FromArgb(0xEC, 0xEF, 0xF2); // #05365919 on white
    internal static readonly Color Scrim = Color.FromArgb(0x66, 0x01, 0x12, 0x28);    // #01122866

    // ---- 尺寸（96 DPI 下的 px）--------------------------------------------
    internal const int RadiusInner = 4;      // radius-inner
    internal const int RadiusElement = 8;    // radius-element
    internal const int RadiusContainer = 12; // radius-container

    internal const int SizeSm = 28;          // size-element-sm
    internal const int SizeMd = 32;          // size-element-md
    internal const int SizeLg = 36;          // size-element-lg

    internal const int Space1 = 4;
    internal const int Space2 = 8;
    internal const int Space3 = 12;
    internal const int Space4 = 16;
    internal const int Space5 = 20;
    internal const int Space6 = 24;
    internal const int Space8 = 32;

    internal const int SidebarWidth = 288;
    internal const int WindowWidth = 1024;
    internal const int WindowHeight = 640;
    internal const int WindowMinWidth = 880;
    internal const int WindowMinHeight = 560;

    internal const int FontSm = 12;          // font-size-sm
    internal const int FontBase = 14;        // font-size-base
    internal const int FontLg = 17;          // font-size-lg
    internal const int FontXl = 20;          // font-size-xl

    internal const int DurationFast = 175;   // duration-fast
    internal const int DurationMedium = 410; // duration-medium

    // ---- DPI 缩放 -----------------------------------------------------------
    private static float scale = 1f;

    internal static float Scale { get { return scale; } }

    internal static void SetScale(float value)
    {
        scale = value <= 0 ? 1f : value;
        fonts.Clear();
    }

    internal static int S(int value)
    {
        return (int)Math.Round(value * scale);
    }

    // ---- 内嵌标题字体（源流明体裁剪版 QJToolDisplay.otf，见 Docs/字体说明.md）----
    private static readonly System.Drawing.Text.PrivateFontCollection DisplayFonts =
        new System.Drawing.Text.PrivateFontCollection();

    private static string displayFamily;

    private static System.Runtime.InteropServices.GCHandle displayBytes;

    static Theme()
    {
        LoadDisplayFont();
    }

    /* 用私有字体集合加载内嵌字体：AddFontMemResourceEx 注册的字体 GDI+ 常常看不到。 */
    private static void LoadDisplayFont()
    {
        try
        {
            using (System.IO.Stream stream = typeof(Theme).Assembly.GetManifestResourceStream("QJToolDisplay.otf"))
            {
                if (stream == null) { return; }
                var bytes = new byte[stream.Length];
                int read = 0;
                while (read < bytes.Length)
                {
                    int count = stream.Read(bytes, read, bytes.Length - read);
                    if (count <= 0) { break; }
                    read += count;
                }
                if (read < bytes.Length) { Array.Resize(ref bytes, read); }
                displayBytes = System.Runtime.InteropServices.GCHandle.Alloc(bytes,
                    System.Runtime.InteropServices.GCHandleType.Pinned);
                DisplayFonts.AddMemoryFont(displayBytes.AddrOfPinnedObject(), bytes.Length);
                if (DisplayFonts.Families.Length > 0) { displayFamily = DisplayFonts.Families[0].Name; }
            }
        }
        catch (Exception)
        {
            displayFamily = null;
        }
    }

    internal static Font Display(int pixels)
    {
        string key = "display/" + pixels;
        Font font;
        if (!fonts.TryGetValue(key, out font))
        {
            if (!string.IsNullOrEmpty(displayFamily) && DisplayFonts.Families.Length > 0)
            {
                font = new Font(DisplayFonts.Families[0], pixels * 0.75f * scale, FontStyle.Regular,
                    GraphicsUnit.Point);
            }
            else
            {
                font = Font(pixels, FontStyle.Bold);
            }
            fonts.Add(key, font);
        }
        return font;
    }
    /* 文字行高：自绘和 Label 的高度都按它取，避免中文被裁。 */
    internal static int Line(Font font)
    {
        return font.Height + S(2);
    }
    // ---- 字体 ---------------------------------------------------------------
    private static readonly Dictionary<string, Font> fonts = new Dictionary<string, Font>();

    private static readonly string BodyFamily = Pick("PingFang SC", "Microsoft YaHei UI", "Microsoft YaHei", "Segoe UI");
    private static readonly string CodeFamily = Pick("Cascadia Mono", "Consolas", "Courier New");

    private static string Pick(params string[] candidates)
    {
        var installed = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        using (var collection = new InstalledFontCollection())
        {
            foreach (FontFamily family in collection.Families) { installed.Add(family.Name); }
        }
        foreach (string name in candidates)
        {
            if (installed.Contains(name)) { return name; }
        }
        return candidates[candidates.Length - 1];
    }

    /* 返回按 token 字号和字重取用的字体；调用方不要释放。 */
    internal static Font Font(int pixels, FontStyle style)
    {
        string key = pixels + "/" + (int)style;
        Font font;
        if (!fonts.TryGetValue(key, out font))
        {
            font = new Font(BodyFamily, pixels * 0.75f * scale, style, GraphicsUnit.Point);
            fonts.Add(key, font);
        }
        return font;
    }

    internal static Font Code(int pixels, FontStyle style)
    {
        string key = "c" + pixels + "/" + (int)style;
        Font font;
        if (!fonts.TryGetValue(key, out font))
        {
            font = new Font(CodeFamily, pixels * 0.75f * scale, style, GraphicsUnit.Point);
            fonts.Add(key, font);
        }
        return font;
    }

    internal static Font Body14 { get { return Font(FontBase, FontStyle.Regular); } }
    internal static Font Body14Medium { get { return Font(FontBase, FontStyle.Bold); } }
    internal static Font Caption12 { get { return Font(FontSm, FontStyle.Regular); } }
    internal static Font Heading17 { get { return Font(FontLg, FontStyle.Bold); } }
    internal static Font Heading20 { get { return Font(FontXl, FontStyle.Bold); } }
    internal static Font LogFont { get { return Code(FontSm, FontStyle.Regular); } }

    // ---- 绘制工具 -----------------------------------------------------------
    internal static Color Mix(Color a, Color b, float t)
    {
        t = Math.Max(0f, Math.Min(1f, t));
        return Color.FromArgb(
            (int)Math.Round(a.R + (b.R - a.R) * t),
            (int)Math.Round(a.G + (b.G - a.G) * t),
            (int)Math.Round(a.B + (b.B - a.B) * t));
    }
    internal static GraphicsPath Round(Rectangle bounds, int radius)
    {
        var path = new GraphicsPath();
        int d = Math.Max(1, radius * 2);
        if (radius <= 0)
        {
            path.AddRectangle(bounds);
            return path;
        }
        d = Math.Min(d, Math.Min(bounds.Width, bounds.Height));
        path.AddArc(bounds.X, bounds.Y, d, d, 180, 90);
        path.AddArc(bounds.Right - d, bounds.Y, d, d, 270, 90);
        path.AddArc(bounds.Right - d, bounds.Bottom - d, d, d, 0, 90);
        path.AddArc(bounds.X, bounds.Bottom - d, d, d, 90, 90);
        path.CloseFigure();
        return path;
    }

    internal static void Fill(Graphics g, Rectangle bounds, int radius, Color color)
    {
        var old = g.SmoothingMode;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        using (GraphicsPath path = Round(bounds, radius))
        using (var brush = new SolidBrush(color))
        {
            g.FillPath(brush, path);
        }
        g.SmoothingMode = old;
    }

    internal static void Stroke(Graphics g, Rectangle bounds, int radius, Color color, float width)
    {
        var old = g.SmoothingMode;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        Rectangle edge = new Rectangle(bounds.X, bounds.Y, bounds.Width - 1, bounds.Height - 1);
        using (GraphicsPath path = Round(edge, radius))
        using (var pen = new Pen(color, width))
        {
            g.DrawPath(pen, path);
        }
        g.SmoothingMode = old;
    }

    /* 焦点环：2px accent，外偏 3px（tokens-focus）。 */
    internal static void FocusRing(Graphics g, Rectangle bounds, int radius)
    {
        int offset = S(3);
        Rectangle ring = new Rectangle(bounds.X - offset, bounds.Y - offset,
            bounds.Width + offset * 2, bounds.Height + offset * 2);
        Stroke(g, ring, radius + offset, Accent, Math.Max(1f, 2f * scale));
    }

    internal static void Text(Graphics g, string value, Rectangle bounds, Font font, Color color,
        TextFormatFlags flags)
    {
        if (string.IsNullOrEmpty(value)) { return; }
        TextRenderer.DrawText(g, value, font, bounds, color,
            flags | TextFormatFlags.NoPrefix | TextFormatFlags.NoPadding);
    }

    internal static void TextLeft(Graphics g, string value, Rectangle bounds, Font font, Color color)
    {
        Text(g, value, bounds, font, color, TextFormatFlags.Left | TextFormatFlags.VerticalCenter |
            TextFormatFlags.EndEllipsis | TextFormatFlags.SingleLine);
    }

    /* 由 0..1 进度取动画中间值，供自绘控件使用。 */
    internal static float Ease(float t)
    {
        // cubic-bezier(0.24, 1, 0.4, 1) 的近似：起步快、收尾缓。
        t = Math.Max(0f, Math.Min(1f, t));
        return 1f - (1f - t) * (1f - t) * (1f - t);
    }

    internal static bool ReduceMotion()
    {
        bool animations;
        if (NativeMethods.SystemParametersInfo(NativeMethods.SPI_GETCLIENTAREAANIMATION, 0, out animations, 0))
        {
            return !animations;
        }
        return false;
    }
}

internal static class NativeMethods
{
    [System.Runtime.InteropServices.DllImport("gdi32.dll", ExactSpelling = true)]
    internal static extern IntPtr AddFontMemResourceEx(byte[] data, uint size, IntPtr reserved, out uint count);

    internal const int SPI_GETCLIENTAREAANIMATION = 0x1042;

    [System.Runtime.InteropServices.DllImport("user32.dll", SetLastError = true)]
    [return: System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.Bool)]
    internal static extern bool SystemParametersInfo(int action, int param, out bool value, int update);
}