using System;
using System.Drawing;

/* 验证绘制函数对最小化/布局过渡中零或很小的客户区安全。 */
internal static class UiPaintCheck
{
    [STAThread]
    private static int Main()
    {
        int failures = 0;
        using (var bitmap = new Bitmap(32, 32))
        using (Graphics g = Graphics.FromImage(bitmap))
        {
            foreach (Rectangle bounds in new[] {
                new Rectangle(0, 0, 0, 0), new Rectangle(0, 0, 0, 16),
                new Rectangle(0, 0, 16, 0), new Rectangle(0, 0, 1, 1),
                new Rectangle(0, 0, -1, 16), new Rectangle(0, 0, 16, -1) })
            {
                try
                {
                    Theme.Fill(g, bounds, Theme.RadiusElement, Theme.Surface);
                    Theme.Stroke(g, bounds, Theme.RadiusElement, Theme.Border, 1f);
                    Console.WriteLine("PASS safe painting " + bounds);
                }
                catch (Exception error)
                {
                    failures++;
                    Console.WriteLine("FAIL " + bounds + " : " + error.GetType().Name + " " + error.Message);
                }
            }
            Theme.Fill(g, new Rectangle(2, 2, 24, 24), Theme.RadiusElement, Theme.Accent);
            if (bitmap.GetPixel(14, 14).ToArgb() != Theme.Accent.ToArgb())
            {
                failures++;
                Console.WriteLine("FAIL normal-size paint changed");
            }
        }
        return failures == 0 ? 0 : 1;
    }
}
