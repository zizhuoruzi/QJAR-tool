using System;
using System.IO;
using System.Windows.Forms;

internal static class Program
{
    [STAThread]
    private static void Main(string[] args)
    {
        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);
        Application.ThreadException += delegate(object sender, System.Threading.ThreadExceptionEventArgs e)
        {
            Report(e.Exception);
        };
        try
        {
            int page = 0;
            foreach (string arg in args)
            {
                if (arg == "--page=command") { page = 1; }
            }
            Application.Run(new MainForm(page));
        }
        catch (Exception error)
        {
            Report(error);
        }
    }

    /* 启动或绘制异常时留下痕迹，避免“窗口没出现”无从查起。 */
    private static void Report(Exception error)
    {
        try
        {
            string path = Path.Combine(Path.GetTempPath(), "QJAR-error.log");
            File.AppendAllText(path, DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") + Environment.NewLine +
                error + Environment.NewLine + Environment.NewLine);
        }
        catch (Exception)
        {
        }
    }
}