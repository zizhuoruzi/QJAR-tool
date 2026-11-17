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
            bool guide = false;
            foreach (string arg in args)
            {
                if (arg == "--page=command") { page = 1; }
                if (arg == "--page=build") { page = 2; }
                if (arg == "--guide") { guide = true; }
            }
            if (guide)
            {
                string project, distribution;
                bool package;
                BuildSettings.Load(out project, out distribution, out package);
                Application.Run(new WslGuideForm(distribution));
                return;
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