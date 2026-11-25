using System.Text;
using System.Windows.Forms;

namespace Lab4Contrast;

internal static class Program
{
    [STAThread]
    private static void Main()
    {
        InstallCrashHandlers();

        Application.SetHighDpiMode(HighDpiMode.PerMonitorV2);
        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);
        Application.Run(new MainForm());
    }

    private static void InstallCrashHandlers()
    {
        Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);
        Application.ThreadException += (_, e) => Report(e.Exception);
        AppDomain.CurrentDomain.UnhandledException += (_, e) => Report(e.ExceptionObject as Exception);
        TaskScheduler.UnobservedTaskException += (_, e) =>
        {
            Report(e.Exception);
            e.SetObserved();
        };
    }

    private static void Report(Exception? error)
    {
        if (error is null) return;

        string path = Path.Combine(AppContext.BaseDirectory, "error.log");
        try
        {
            File.AppendAllText(
                path,
                $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {error.GetType().FullName}: {error.Message}{Environment.NewLine}" +
                $"{error}{Environment.NewLine}{Environment.NewLine}",
                Encoding.UTF8);
        }
        catch (Exception)
        {
        }

        MessageBox.Show(
            $"Произошла ошибка:{Environment.NewLine}{error.Message}{Environment.NewLine}{Environment.NewLine}" +
            $"Подробности записаны в:{Environment.NewLine}{path}",
            "Лабораторная работа 4",
            MessageBoxButtons.OK,
            MessageBoxIcon.Error);
    }
}
