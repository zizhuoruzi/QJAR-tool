using System;
using System.Drawing;
using System.Windows.Forms;

/* 自绘控件库：只需本文件里的几种，够两页界面用。
   规格见 Docs/astryx-ui-spec.md 第 8 节“组件对照”。 */

internal abstract class AnimatedControl : Control
{
    private readonly Timer timer = new Timer { Interval = 15 };
    private float hover, press;
    private bool hovered, pressed;
    private int lastTick;

    protected AnimatedControl()
    {
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint |
            ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw |
            ControlStyles.Selectable, true);
        TabStop = true;
        timer.Tick += delegate { Step(); };
        lastTick = Environment.TickCount;
    }

    protected float Hover { get { return hover; } }
    protected float Press { get { return press; } }
    protected bool Hovered { get { return hovered && Enabled; } }
    protected bool Pressed { get { return pressed && Enabled; } }

    protected override void OnMouseEnter(EventArgs e)
    {
        hovered = true;
        Animate();
        base.OnMouseEnter(e);
    }

    protected override void OnMouseLeave(EventArgs e)
    {
        hovered = false;
        pressed = false;
        Animate();
        base.OnMouseLeave(e);
    }

    protected override void OnMouseDown(MouseEventArgs e)
    {
        if (e.Button == MouseButtons.Left && Enabled)
        {
            pressed = true;
            if (CanFocus) { Focus(); }
            Animate();
        }
        base.OnMouseDown(e);
    }

    protected override void OnMouseUp(MouseEventArgs e)
    {
        if (pressed && e.Button == MouseButtons.Left)
        {
            pressed = false;
            Animate();
        }
        base.OnMouseUp(e);
    }

    protected override void OnEnabledChanged(EventArgs e)
    {
        if (!Enabled) { hovered = pressed = false; }
        Animate();
        base.OnEnabledChanged(e);
    }

    private void Animate()
    {
        if (Theme.ReduceMotion())
        {
            hover = Hovered ? 1f : 0f;
            press = Pressed ? 1f : 0f;
            Invalidate();
            return;
        }
        lastTick = Environment.TickCount;
        timer.Start();
    }

    private void Step()
    {
        int now = Environment.TickCount;
        float delta = Math.Min(0.1f, (now - lastTick) / (float)Theme.DurationFast);
        if (delta < 0.01f) { delta = 0.01f; }
        lastTick = now;
        hover = Approach(hover, Hovered ? 1f : 0f, delta);
        press = Approach(press, Pressed ? 1f : 0f, delta);
        Invalidate();
        if (Math.Abs(hover - (Hovered ? 1f : 0f)) < 0.01f && Math.Abs(press - (Pressed ? 1f : 0f)) < 0.01f)
        {
            hover = Hovered ? 1f : 0f;
            press = Pressed ? 1f : 0f;
            timer.Stop();
        }
    }

    private static float Approach(float value, float target, float step)
    {
        if (value < target) { return Math.Min(target, value + step); }
        if (value > target) { return Math.Max(target, value - step); }
        return value;
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) { timer.Dispose(); }
        base.Dispose(disposing);
    }
}

internal enum ButtonKind { Primary, Secondary, Quiet, DangerQuiet }

/* 主/次/文字/危险文字四档按钮，高度 36（size-element-lg）。 */
internal sealed class ActionButton : AnimatedControl
{
    private ButtonKind kind = ButtonKind.Primary;

    internal ActionButton(string text, ButtonKind kind)
    {
        Text = text;
        Kind = kind;
        Height = Theme.S(Theme.SizeLg);
        Cursor = Cursors.Hand;
        AccessibleRole = AccessibleRole.PushButton;
        AccessibleName = text;
    }

    internal ButtonKind Kind
    {
        get { return kind; }
        set { kind = value; Invalidate(); }
    }

    protected override void OnTextChanged(EventArgs e)
    {
        AccessibleName = Text;
        base.OnTextChanged(e);
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        Graphics g = e.Graphics;
        Rectangle box = new Rectangle(0, 0, Width, Height);
        Color backdrop = Parent != null ? Parent.BackColor : Theme.Body;
        Color fill, text;
        if (!Enabled)
        {
            fill = kind == ButtonKind.Primary ? Theme.AccentDisabled : backdrop;
            text = kind == ButtonKind.Primary ? Theme.OnAccent : Theme.TextDisabled;
        }
        else if (kind == ButtonKind.Primary)
        {
            fill = Theme.Mix(Theme.Accent, Color.FromArgb(0x05, 0x36, 0x59), 0.05f * Hover + 0.10f * Press);
            text = Theme.OnAccent;
        }
        else if (kind == ButtonKind.Secondary)
        {
            fill = Theme.Mix(Theme.Surface, Theme.OverlayPressed, Hover * 0.6f + Press * 0.4f);
            text = Theme.TextPrimary;
        }
        else
        {
            fill = Theme.Mix(backdrop, Theme.OverlayPressed, Hover * 0.7f + Press * 0.3f);
            text = kind == ButtonKind.DangerQuiet ? Theme.Error : Theme.TextSecondary;
        }

        Theme.Fill(g, box, Theme.S(Theme.RadiusElement), fill);
        if (kind == ButtonKind.Secondary || (!Enabled && kind != ButtonKind.Primary))
        {
            Theme.Stroke(g, box, Theme.S(Theme.RadiusElement), Theme.Border, 1f);
        }
        Theme.Text(g, Text, box, Theme.Body14, text,
            TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine);
        if (Focused && Visible)
        {
            Theme.Stroke(g, box, Theme.S(Theme.RadiusElement), Theme.Accent, Math.Max(1f, 2f * Theme.Scale));
        }
    }


    protected override bool IsInputKey(Keys keyData)
    {
        return keyData == Keys.Enter || keyData == Keys.Space || base.IsInputKey(keyData);
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        if (e.KeyCode == Keys.Enter || e.KeyCode == Keys.Space)
        {
            e.Handled = true;
            OnClick(EventArgs.Empty);
        }
        base.OnKeyDown(e);
    }

    protected override void OnGotFocus(EventArgs e) { Invalidate(); base.OnGotFocus(e); }
    protected override void OnLostFocus(EventArgs e) { Invalidate(); base.OnLostFocus(e); }
}

/* 侧栏导航项：高 36，选中态是 accent-muted 底 + accent 文字。 */
internal sealed class NavButton : AnimatedControl
{
    private bool selected;

    internal NavButton(string text)
    {
        Text = text;
        Height = Theme.S(Theme.SizeLg);
        Cursor = Cursors.Hand;
        TabStop = false;
        AccessibleRole = AccessibleRole.PageTab;
    }

    internal bool Selected
    {
        get { return selected; }
        set { selected = value; Invalidate(); }
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        Graphics g = e.Graphics;
        Rectangle box = new Rectangle(0, 0, Width, Height);
        if (selected)
        {
            Theme.Fill(g, box, Theme.S(Theme.RadiusElement), Theme.AccentMuted);
        }
        else if (Hover > 0.01f)
        {
            Theme.Fill(g, box, Theme.S(Theme.RadiusElement),
                Theme.Mix(Theme.Surface, Theme.OverlayPressed, Hover));
        }
        Rectangle text = new Rectangle(box.X + Theme.S(Theme.Space3), box.Y,
            box.Width - Theme.S(Theme.Space3 * 2), box.Height);
        Theme.TextLeft(g, Text, text, selected ? Theme.Body14Medium : Theme.Body14,
            selected ? Theme.Accent : Theme.TextPrimary);
    }
}

internal enum Tone { Neutral, Info, Success, Warning, Error }

/* 状态徽标：muted 底 + 圆点 + 12px 文字（Badge + StatusDot）。 */
internal sealed class StatusPill : Control
{
    private Tone tone = Tone.Neutral;
    private string caption = "";

    internal StatusPill()
    {
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint |
            ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
        TabStop = false;
        Height = Theme.S(Theme.SizeSm);
    }

    internal Tone Tone
    {
        get { return tone; }
        set { tone = value; Invalidate(); }
    }

    internal string Caption
    {
        get { return caption; }
        set { caption = value; Fit(); Invalidate(); }
    }

    /* 按文字宽度自适应，避免“正在连接控…”这种截断。 */
    internal void Fit()
    {
        Size text = TextRenderer.MeasureText(string.IsNullOrEmpty(caption) ? " " : caption, Theme.Caption12);
        int padding = Theme.S(Theme.Space3) * 2 + Theme.S(8) + Theme.S(Theme.Space2);
        Width = Math.Max(Theme.S(64), text.Width + padding);
    }

    internal static Color ColorOf(Tone value)
    {
        switch (value)
        {
            case Tone.Info: return Theme.Accent;
            case Tone.Success: return Theme.Success;
            case Tone.Warning: return Theme.Warning;
            case Tone.Error: return Theme.Error;
            default: return Theme.TextSecondary;
        }
    }

    internal static Color BackgroundOf(Tone value)
    {
        switch (value)
        {
            case Tone.Info: return Theme.AccentMuted;
            case Tone.Success: return Theme.SuccessMuted;
            case Tone.Warning: return Theme.WarningMuted;
            case Tone.Error: return Theme.ErrorMuted;
            default: return Theme.OverlayPressed;
        }
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        Graphics g = e.Graphics;
        Rectangle box = new Rectangle(0, 0, Width, Height);
        Theme.Fill(g, box, box.Height / 2, BackgroundOf(tone));
        int dot = Theme.S(8);
        Color color = ColorOf(tone);
        using (var brush = new SolidBrush(color))
        {
            g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
            g.FillEllipse(brush, Theme.S(Theme.Space3), (Height - dot) / 2, dot, dot);
        }
        Rectangle text = new Rectangle(Theme.S(Theme.Space3) + dot + Theme.S(Theme.Space2), 0,
            Width - Theme.S(Theme.Space3) * 2 - dot - Theme.S(Theme.Space2), Height);
        Theme.TextLeft(g, caption, text, Theme.Caption12, color);
    }
}

/* 进度条：高 6，全圆角；数值变化按 410ms 缓动。 */
internal sealed class ProgressBar : Control
{
    private float value;
    private float target;
    private readonly Timer timer = new Timer { Interval = 15 };
    private int lastTick;

    internal ProgressBar()
    {
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint |
            ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
        TabStop = false;
        Height = Theme.S(6);
        timer.Tick += delegate { Step(); };
    }

    internal int Value
    {
        get { return (int)Math.Round(target); }
        set
        {
            target = Math.Max(0, Math.Min(100, value));
            if (Theme.ReduceMotion()) { this.value = target; Invalidate(); return; }
            lastTick = Environment.TickCount;
            timer.Start();
        }
    }

    internal Tone Tone { get; set; }

    private void Step()
    {
        int now = Environment.TickCount;
        float delta = Math.Min(1f, (now - lastTick) / (float)Theme.DurationMedium);
        lastTick = now;
        value += (target - value) * Math.Max(0.08f, delta);
        if (Math.Abs(target - value) < 0.4f)
        {
            value = target;
            timer.Stop();
        }
        Invalidate();
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        Graphics g = e.Graphics;
        Rectangle box = new Rectangle(0, 0, Width, Height);
        Theme.Fill(g, box, Height / 2, Theme.Border);
        int width = (int)Math.Round(Width * (value / 100f));
        if (width > 1)
        {
            Rectangle filled = new Rectangle(0, 0, Math.Max(Height, width), Height);
            Color color = Tone == Tone.Success ? Theme.Success
                : Tone == Tone.Error ? Theme.Error
                : Tone == Tone.Warning ? Theme.Warning
                : Theme.Accent;
            Theme.Fill(g, filled, Height / 2, color);
        }
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) { timer.Dispose(); }
        base.Dispose(disposing);
    }
}

/* 输入框：原生 TextBox 负责编辑，外壳负责圆角、描边、焦点与空态提示。 */
internal sealed class TextField : Panel
{
    private readonly PlaceholderBox edit = new PlaceholderBox();
    private int innerPad = Theme.Space3;
    private bool invalid;
    private bool readOnly;

    internal TextField(string placeholder)
    {
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint |
            ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
        Height = Theme.S(Theme.SizeMd);
        BackColor = Theme.Surface;
        edit.Hint = placeholder;
        edit.BorderStyle = BorderStyle.None;
        edit.Font = Theme.Body14;
        edit.BackColor = Theme.Surface;
        edit.ForeColor = Theme.TextPrimary;
        edit.TextChanged += delegate
        {
            if (invalid) { Invalid = false; }
            OnTextChanged(EventArgs.Empty);
        };
        edit.GotFocus += delegate { Invalidate(); };
        edit.LostFocus += delegate { Invalidate(); };
        edit.KeyDown += delegate(object source, KeyEventArgs args) { OnEditKeyDown(args); };
        Controls.Add(edit);
    }

    internal event KeyEventHandler EditKeyDown;

    internal TextBox Edit { get { return edit; } }

    /* 四段 IP 这类窄输入框把内边距收窄，避免数字被截断。 */
    internal int InnerPad
    {
        get { return innerPad; }
        set { innerPad = value; PerformLayout(); }
    }

    /* 只读态用浅灰底表示“要改先点修改”。 */
    internal bool IsReadOnly
    {
        get { return readOnly; }
        set
        {
            readOnly = value;
            edit.ReadOnly = value;
            edit.BackColor = value ? Theme.Body : Theme.Surface;
            Invalidate();
        }
    }

    internal bool Invalid
    {
        get { return invalid; }
        set { invalid = value; Invalidate(); }
    }

    public override string Text
    {
        get { return edit.Text; }
        set { edit.Text = value; }
    }

    internal bool UseSystemPasswordChar
    {
        get { return edit.UseSystemPasswordChar; }
        set { edit.UseSystemPasswordChar = value; }
    }

    internal int MaxLength
    {
        get { return edit.MaxLength; }
        set { edit.MaxLength = value; }
    }

    internal event EventHandler Accepted;

    private void OnEditKeyDown(KeyEventArgs args)
    {
        KeyEventHandler handler = EditKeyDown;
        if (handler != null) { handler(this, args); }
        if (args.KeyCode == Keys.Enter && !args.Handled)
        {
            args.Handled = true;
            args.SuppressKeyPress = true;
            EventHandler accepted = Accepted;
            if (accepted != null) { accepted(this, EventArgs.Empty); }
        }
    }

    protected override void OnResize(EventArgs e)
    {
        base.OnResize(e);
        PerformLayout();
    }

    protected override void OnLayout(LayoutEventArgs e)
    {
        base.OnLayout(e);
        int pad = Theme.S(innerPad);
        int top = (Height - edit.PreferredHeight) / 2;
        edit.SetBounds(pad, Math.Max(Theme.S(2), top), Math.Max(Theme.S(10), Width - pad * 2),
            edit.PreferredHeight);
    }

    protected override void OnPaintBackground(PaintEventArgs e)
    {
        e.Graphics.Clear(Parent != null ? Parent.BackColor : Theme.Body);
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        Graphics g = e.Graphics;
        Rectangle box = new Rectangle(0, 0, Width - 1, Height - 1);
        Theme.Fill(g, box, Theme.S(Theme.RadiusElement), readOnly ? Theme.Body : Theme.Surface);
        if (invalid)
        {
            Theme.Stroke(g, box, Theme.S(Theme.RadiusElement), Theme.Error, Math.Max(1f, 2f * Theme.Scale));
        }
        else if (edit.Focused && !readOnly)
        {
            Theme.Stroke(g, box, Theme.S(Theme.RadiusElement), Theme.Accent, Math.Max(1f, 2f * Theme.Scale));
        }
        else
        {
            Theme.Stroke(g, box, Theme.S(Theme.RadiusElement), Theme.Border, 1f);
        }
    }
}

/* 空态提示画在 TextBox 自己的 WM_PAINT 之后，避开子窗口遮挡问题。 */
internal sealed class PlaceholderBox : TextBox
{
    private const int WM_PAINT = 0x000F;

    internal string Hint;

    protected override void WndProc(ref Message message)
    {
        base.WndProc(ref message);
        if (message.Msg != WM_PAINT || !string.IsNullOrEmpty(Text) || Focused ||
            string.IsNullOrEmpty(Hint))
        {
            return;
        }
        using (Graphics g = Graphics.FromHwnd(Handle))
        {
            TextRenderer.DrawText(g, Hint, Font, ClientRectangle, Theme.TextDisabled,
                TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);
        }
    }
}
/* 只读日志区：白底 + 1px 描边 + 圆角 12，等宽 12px，行高 1.5。 */
internal sealed class LogView : Panel
{
    private readonly RichTextBox box = new RichTextBox();
    private bool hintShown;

    [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
    private struct ScrollInfo
    {
        internal uint Size, Mask;
        internal int Min, Max;
        internal uint Page;
        internal int Position, Track;
    }

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern bool GetScrollInfo(IntPtr window, int bar, ref ScrollInfo info);
    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern IntPtr SendMessage(IntPtr window, int message, IntPtr wparam, ref Point point);

    private bool AtBottom()
    {
        if (!box.IsHandleCreated || IsEmpty) { return true; }
        var info = new ScrollInfo { Size = 28, Mask = 7 };
        return !GetScrollInfo(box.Handle, 1, ref info) ||
            info.Position + Math.Max(0, (long)info.Page - 1) >= info.Max - 2;
    }

    private void ScrollToLatest()
    {
        if (box.IsHandleCreated && box.Visible && !IsEmpty)
        {
            NativeMethods.SendMessage(box.Handle, 0x0115, new IntPtr(7), IntPtr.Zero);
        }
    }

    internal LogView(string emptyHint)
    {
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint |
            ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
        BackColor = Theme.Surface;
        EmptyHint = emptyHint;
        box.BorderStyle = BorderStyle.None;
        box.ReadOnly = true;
        box.BackColor = Theme.Surface;
        box.ForeColor = Theme.TextPrimary;
        box.Font = Theme.LogFont;
        box.HideSelection = false;
        box.DetectUrls = false;
        box.WordWrap = true;
        box.ScrollBars = RichTextBoxScrollBars.Vertical;
        box.TabStop = true;
        Controls.Add(box);
    }

    internal string EmptyHint { get; set; }

    internal bool IsEmpty { get { return hintShown || box.TextLength == 0; } }

    internal string AllText { get { return hintShown ? string.Empty : box.Text; } }

    internal RichTextBox Box { get { return box; } }

    /* 空态提示直接写进只读框里：RichTextBox 是子窗口，会把父控件的绘制盖住。 */
    private void ShowHint()
    {
        box.Clear();
        hintShown = false;
        if (string.IsNullOrEmpty(EmptyHint)) { return; }
        hintShown = true;
        box.Text = EmptyHint;
        box.SelectAll();
        box.SelectionColor = Theme.TextDisabled;
        box.Select(0, 0);
        box.SelectionColor = Theme.TextPrimary;
    }

    internal void Append(string line, Color color)
    {
        bool follow = AtBottom();
        var position = Point.Empty;
        int selectedAt = box.SelectionStart, selectedLength = box.SelectionLength;
        if (!follow) { SendMessage(box.Handle, 0x04DD, IntPtr.Zero, ref position); }
        if (hintShown) { box.Clear(); hintShown = false; }
        if (box.TextLength > 0)
        {
            box.AppendText(Environment.NewLine);
        }
        int start = box.TextLength;
        box.AppendText(line);
        box.Select(start, box.TextLength - start);
        box.SelectionColor = color;
        box.Select(box.TextLength, 0);
        box.SelectionColor = Theme.TextPrimary;
        if (follow) { ScrollToLatest(); }
        else
        {
            box.Select(selectedAt, selectedLength);
            SendMessage(box.Handle, 0x04DE, IntPtr.Zero, ref position);
        }
    }

    internal void TrimToRecentText(int maxLength)
    {
        if (IsEmpty || box.TextLength <= maxLength) { return; }
        int remove = box.TextLength - maxLength;
        box.Select(0, remove);
        box.SelectedText = string.Empty;
    }

    /* 导出到用户选定的文件；空日志直接返回 false。 */
    internal bool SaveTo(IWin32Window owner, string fileName)
    {
        if (IsEmpty) { return false; }
        using (var dialog = new SaveFileDialog())
        {
            dialog.Title = "导出日志";
            dialog.Filter = "文本文件 (*.txt)|*.txt|所有文件 (*.*)|*.*";
            dialog.FileName = fileName;
            if (dialog.ShowDialog(owner) != DialogResult.OK) { return false; }
            try
            {
                System.IO.File.WriteAllText(dialog.FileName, AllText, new System.Text.UTF8Encoding(true));
                return true;
            }
            catch (Exception)
            {
                return false;
            }
        }
    }
    internal void Clear()
    {
        box.Clear();
        ShowHint();
        Invalidate();
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        ShowHint();
    }

    protected override void OnResize(EventArgs e)
    {
        base.OnResize(e);
        int pad = Theme.S(Theme.Space3);
        bool follow = AtBottom();
        box.SetBounds(pad, pad, Math.Max(Theme.S(10), Width - pad * 2), Math.Max(Theme.S(10), Height - pad * 2));
        if (follow) { ScrollToLatest(); }
    }

    protected override void OnPaintBackground(PaintEventArgs e)
    {
        e.Graphics.Clear(Parent != null ? Parent.BackColor : Theme.Body);
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        Graphics g = e.Graphics;
        Rectangle boxRect = new Rectangle(0, 0, Width - 1, Height - 1);
        Theme.Fill(g, boxRect, Theme.S(Theme.RadiusContainer), Theme.Surface);
        Theme.Stroke(g, boxRect, Theme.S(Theme.RadiusContainer), Theme.Border, 1f);

    }
}

/* 白底圆角卡片：标题 17px/600，内容由页面自行布局。 */
internal sealed class Card : Panel
{
    internal Card(string title)
    {
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint |
            ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
        BackColor = Theme.Surface;
        Title = title;
    }

    internal string Title { get; set; }

    internal int ContentTop
    {
        get
        {
            return string.IsNullOrEmpty(Title)
                ? Theme.S(Theme.Space5)
                : Theme.S(Theme.Space5) + Theme.Line(Theme.Heading17) + Theme.S(Theme.Space3);
        }
    }

    protected override void OnPaintBackground(PaintEventArgs e)
    {
        e.Graphics.Clear(Parent != null ? Parent.BackColor : Theme.Body);
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        Graphics g = e.Graphics;
        Rectangle box = new Rectangle(0, 0, Width - 1, Height - 1);
        Theme.Fill(g, box, Theme.S(Theme.RadiusContainer), Theme.Surface);
        Theme.Stroke(g, box, Theme.S(Theme.RadiusContainer), Theme.Border, 1f);
        if (!string.IsNullOrEmpty(Title))
        {
            Rectangle title = new Rectangle(Theme.S(Theme.Space5), Theme.S(Theme.Space5),
                Width - Theme.S(Theme.Space5) * 2, Theme.Line(Theme.Heading17));
            Theme.TextLeft(g, Title, title, Theme.Heading17, Theme.TextPrimary);
        }
    }
}
/* 提示条：muted 底 + 12px 文字，用于“注册结果待确认”和错误说明（Banner）。 */
internal sealed class Banner : Control
{
    private Tone tone = Tone.Warning;

    internal Banner()
    {
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint |
            ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
        BackColor = Theme.Surface;
        Height = Theme.S(34);
        TabStop = false;
    }

    internal Tone Tone
    {
        get { return tone; }
        set { tone = value; Invalidate(); }
    }

    protected override void OnPaintBackground(PaintEventArgs e)
    {
        e.Graphics.Clear(Parent != null ? Parent.BackColor : Theme.Body);
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        Graphics g = e.Graphics;
        Rectangle box = new Rectangle(0, 0, Width, Height);
        Theme.Fill(g, box, Theme.S(Theme.RadiusElement), StatusPill.BackgroundOf(tone));
        Rectangle text = new Rectangle(Theme.S(Theme.Space3), 0,
            Width - Theme.S(Theme.Space3) * 2, Height);
        Theme.TextLeft(g, Text, text, Theme.Caption12, StatusPill.ColorOf(tone));
    }
}
/* 页面切换：侧栏里的竖排导航（或横向标签条）。
   竖排时每一行画成带描边的按钮：白底 + 1px 边框，带图标；选中 accent-muted 底 + 蓝色图标与文字。 */
internal sealed class SegmentedTabs : Control
{
    private readonly string[] labels;
    private int selected;
    private int hovered = -1;
    private bool vertical = true;
    private readonly Timer slide = new Timer { Interval = 15 };
    private readonly System.Diagnostics.Stopwatch slideClock = new System.Diagnostics.Stopwatch();
    private double slideFrom;
    private bool pointerFocus;
    internal bool SlideSelection { get; set; }

    private double SelectionPosition()
    {
        if (!slide.Enabled) { return selected; }
        double t = Math.Min(1, slideClock.Elapsed.TotalMilliseconds / 260);
        double delta = selected - slideFrom;
        if (t < 0.72)
        {
            double travel = t / 0.72;
            double reach = delta + Math.Sign(delta) * Math.Min(Math.Abs(delta) * 0.12, 0.09);
            return slideFrom + reach * (1 - Math.Pow(1 - travel, 3));
        }
        double settle = (t - 0.72) / 0.28;
        return selected + Math.Sign(delta) * Math.Min(Math.Abs(delta) * 0.12, 0.09) * Math.Pow(1 - settle, 2);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) { slide.Dispose(); }
        base.Dispose(disposing);
    }

    internal SegmentedTabs(params string[] labels)
    {
        this.labels = labels ?? new string[0];
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint |
            ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw |
            ControlStyles.Selectable, true);
        TabStop = true;
        Height = Theme.S(Theme.SizeLg);
        Cursor = Cursors.Hand;
        AccessibleRole = AccessibleRole.PageTabList;
        slide.Tick += delegate
        {
            if (slideClock.ElapsedMilliseconds >= 260) { slide.Stop(); }
            Invalidate();
        };
    }

    internal event EventHandler SelectedIndexChanged;

    /* 鼠标为主的选择控件不要抢焦点：否则点一下就会留下焦点框。 */
    internal bool FocusOnClick { get; set; } = true;

    internal bool Vertical
    {
        get { return vertical; }
        set { vertical = value; Invalidate(); }
    }

    internal int SelectedIndex
    {
        get { return selected; }
        set
        {
            int next = Math.Max(0, Math.Min(labels.Length - 1, value));
            if (next == selected) { return; }
            slideFrom = SelectionPosition();
            slide.Stop();
            selected = next;
            EventHandler handler = SelectedIndexChanged;
            if (handler != null) { handler(this, EventArgs.Empty); }
            // 创建目标页的原生控件可能占用一帧，等切页完成再开始底板动画。
            if (SlideSelection && vertical && IsHandleCreated && Visible && !Theme.ReduceMotion())
            {
                slideClock.Restart();
                slide.Start();
            }
            Invalidate();
        }
    }

    private Rectangle Segment(int index)
    {
        int count = Math.Max(1, labels.Length);
        if (vertical)
        {
            int height = Height / count;
            return new Rectangle(0, index * height, Width, height);
        }
        int width = Width / count;
        return new Rectangle(index * width, 0, width, Height);
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        int index = IndexAt(e.Location);
        if (index != hovered)
        {
            hovered = index;
            Cursor = index >= 0 ? Cursors.Hand : Cursors.Default;
            Invalidate();
        }
    }

    protected override void OnMouseLeave(EventArgs e)
    {
        hovered = -1;
        Invalidate();
        base.OnMouseLeave(e);
    }

    protected override void OnMouseDown(MouseEventArgs e)
    {
        int index = IndexAt(e.Location);
        if (index >= 0)
        {
            pointerFocus = true;
            if (FocusOnClick) { Focus(); }
            SelectedIndex = index;
            Invalidate();
        }
        base.OnMouseDown(e);
    }

    private int IndexAt(Point point)
    {
        for (int i = 0; i < labels.Length; i++)
        {
            if (Segment(i).Contains(point)) { return i; }
        }
        return -1;
    }

    protected override bool IsInputKey(Keys keyData)
    {
        return keyData == Keys.Left || keyData == Keys.Right || keyData == Keys.Up || keyData == Keys.Down ||
            base.IsInputKey(keyData);
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        pointerFocus = false;
        Invalidate();
        if (e.KeyCode == Keys.Left || e.KeyCode == Keys.Up) { SelectedIndex = selected - 1; e.Handled = true; }
        else if (e.KeyCode == Keys.Right || e.KeyCode == Keys.Down) { SelectedIndex = selected + 1; e.Handled = true; }
        else if (e.KeyCode == Keys.Home) { SelectedIndex = 0; e.Handled = true; }
        else if (e.KeyCode == Keys.End) { SelectedIndex = labels.Length - 1; e.Handled = true; }
        base.OnKeyDown(e);
    }

    protected override void OnGotFocus(EventArgs e)
    {
        base.OnGotFocus(e);
        Invalidate();
    }

    protected override void OnLostFocus(EventArgs e)
    {
        pointerFocus = false;
        base.OnLostFocus(e);
        Invalidate();
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        Graphics g = e.Graphics;
        g.Clear(Parent != null ? Parent.BackColor : Theme.Body);
        int inset = Theme.S(2);
        if (vertical && SlideSelection && labels.Length > 0)
        {
            Rectangle pill = Segment(0);
            pill.Y = (int)Math.Round(SelectionPosition() * pill.Height);
            pill.Inflate(-inset, -inset);
            Theme.Fill(g, pill, Theme.S(Theme.RadiusElement), Theme.AccentMuted);
        }
        for (int i = 0; i < labels.Length; i++)
        {
            Rectangle rect = Segment(i);
            bool isSelected = i == selected;
            Rectangle pill = new Rectangle(rect.X + inset, rect.Y + inset,
                rect.Width - inset * 2, rect.Height - inset * 2);
            if (vertical) { PaintRow(g, pill, i, isSelected); continue; }
            PaintTab(g, rect, i, isSelected);
        }
        if (Focused && ShowFocusCues && !pointerFocus && labels.Length > 0)
        {
            Rectangle focus = Segment(selected);
            focus.Inflate(-Theme.S(3), -Theme.S(3));
            Theme.Stroke(g, focus, Theme.S(Theme.RadiusElement), Theme.Accent, 1f);
        }
    }

    private void PaintRow(Graphics g, Rectangle pill, int index, bool isSelected)
    {
        Color fill = isSelected ? Theme.AccentMuted
            : index == hovered ? Theme.OverlayHover : Theme.Surface;
        Color edge = isSelected ? Theme.AccentBorder
            : index == hovered ? Theme.BorderStrong : Theme.Border;
        int radius = Theme.S(Theme.RadiusElement);
        if (!SlideSelection)
        {
            Theme.Fill(g, pill, radius, fill);
            Theme.Stroke(g, pill, radius, edge, 1f);
        }
        else if (!slide.Enabled && !isSelected && index == hovered)
        {
            Theme.Fill(g, pill, radius, Theme.OverlayHover);
        }

        int iconSize = Theme.S(16);
        var icon = new Rectangle(pill.X + Theme.S(Theme.Space3), pill.Y + (pill.Height - iconSize) / 2,
            iconSize, iconSize);
        DrawIcon(g, icon, SlideSelection && index < 3 ? (index + 2) % 3 : index,
            isSelected ? Theme.Accent : Theme.TextSecondary);

        var label = new Rectangle(icon.Right + Theme.S(Theme.Space2), pill.Y,
            pill.Right - icon.Right - Theme.S(Theme.Space2) - Theme.S(Theme.Space3), pill.Height);
        Theme.Text(g, labels[index], label,
            isSelected ? Theme.Font(Theme.FontBase, FontStyle.Bold) : Theme.Body14,
            isSelected ? Theme.Accent : Theme.TextPrimary,
            TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine);
    }

    private void PaintTab(Graphics g, Rectangle rect, int index, bool isSelected)
    {
        if (!isSelected && index == hovered)
        {
            Theme.Fill(g, new Rectangle(rect.X + Theme.S(4), rect.Y + Theme.S(2),
                rect.Width - Theme.S(8), rect.Height - Theme.S(6)), Theme.S(Theme.RadiusElement),
                Theme.OverlayPressed);
        }
        Theme.Text(g, labels[index], new Rectangle(rect.X, rect.Y, rect.Width, rect.Height - Theme.S(4)),
            isSelected ? Theme.Font(Theme.FontBase, FontStyle.Bold) : Theme.Body14,
            isSelected ? Theme.Accent : Theme.TextSecondary,
            TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine);
        if (isSelected)
        {
            int height = Math.Max(2, Theme.S(2));
            Theme.Fill(g, new Rectangle(rect.X + Theme.S(12), Height - height,
                rect.Width - Theme.S(24), height), height / 2, Theme.Accent);
        }
    }

    /* 四个内置图标分别表示注册、命令调试、编译和升级 */
    private void DrawIcon(Graphics g, Rectangle box, int index, Color color)
    {
        var old = g.SmoothingMode;
        g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
        using (var pen = new Pen(color, Math.Max(1.2f, 1.4f * Theme.Scale)))
        {
            pen.StartCap = System.Drawing.Drawing2D.LineCap.Round;
            pen.EndCap = System.Drawing.Drawing2D.LineCap.Round;
            int pad = Math.Max(1, box.Width / 8);
            var frame = new Rectangle(box.X + pad, box.Y + pad, box.Width - pad * 2, box.Height - pad * 2);
            if (index == 0)
            {
                using (System.Drawing.Drawing2D.GraphicsPath path = Theme.Round(frame, Theme.S(3))) { g.DrawPath(pen, path); }
                int cx = frame.X + frame.Width / 2, cy = frame.Y + frame.Height / 2;
                int arm = Math.Max(2, frame.Width / 4);
                g.DrawLine(pen, cx - arm, cy, cx + arm, cy);
                g.DrawLine(pen, cx, cy - arm, cx, cy + arm);
            }
            else if (index == 2)
            {
                // 代码括号 </>：编译页的意象，和命令调试的提示符区分开。
                int top = frame.Y + Theme.S(2);
                int bottom = frame.Bottom - Theme.S(2);
                int midY = (top + bottom) / 2;
                int arm = Math.Max(2, frame.Width / 5);
                g.DrawLines(pen, new[]
                {
                    new Point(frame.X + arm + Theme.S(2), top),
                    new Point(frame.X + Theme.S(1), midY),
                    new Point(frame.X + arm + Theme.S(2), bottom)
                });
                g.DrawLines(pen, new[]
                {
                    new Point(frame.Right - arm - Theme.S(2), top),
                    new Point(frame.Right - Theme.S(1), midY),
                    new Point(frame.Right - arm - Theme.S(2), bottom)
                });
                g.DrawLine(pen, frame.X + frame.Width / 2 + Theme.S(1), top,
                    frame.X + frame.Width / 2 - Theme.S(1), bottom);
            }
            else if (index == 3)
            {
                int center = frame.X + frame.Width / 2;
                int top = frame.Y + Theme.S(1);
                int bottom = frame.Bottom - Theme.S(1);
                int arm = Math.Max(2, frame.Width / 4);
                g.DrawLines(pen, new[]
                {
                    new Point(center - arm, top + arm), new Point(center, top),
                    new Point(center + arm, top + arm)
                });
                g.DrawLine(pen, center, top, center, bottom - Theme.S(4));
                g.DrawLines(pen, new[]
                {
                    new Point(frame.Left, bottom - Theme.S(4)), new Point(frame.Left, bottom),
                    new Point(frame.Right, bottom), new Point(frame.Right, bottom - Theme.S(4))
                });
            }
            else
            {
                int left = frame.X + Theme.S(1);
                int top = frame.Y + Math.Max(2, frame.Height / 5);
                int bottom = frame.Bottom - Math.Max(2, frame.Height / 5);
                g.DrawLines(pen, new[]
                {
                    new Point(left, top),
                    new Point(left + frame.Width / 3, frame.Y + frame.Height / 2),
                    new Point(left, bottom)
                });
                g.DrawLine(pen, left + frame.Width / 3 + Theme.S(2), bottom, frame.Right - Theme.S(1), bottom);
            }
        }
        g.SmoothingMode = old;
    }
}
