using System.Text;
using System.Windows.Forms;

namespace Lab4Contrast;

internal static class Program
{
    [STAThread]
    /// Точка входа программы.
    /// [STAThread] обязателен для WinForms: делает главный поток UI-потоком,
    /// единственным, кто может трогать контролы.
    /// Application.Run блокирует этот поток, крутя цикл сообщений до закрытия формы.
    private static void Main()
    {
        InstallCrashHandlers();

        Application.SetHighDpiMode(HighDpiMode.PerMonitorV2);
        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);
        Application.Run(new MainForm());
    }

    /// Регистрирует глобальные перехватчики необработанных исключений.
    /// ПОТОКИ: каждый перехватчик вызывается в том потоке, где упало исключение,
    /// поэтому сюда могут прийти и из фоновых воркеров ContrastProcessor.
    /// e.SetObserved() гасит исключение недосмотренной Task, чтобы оно не убило процесс.
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

    /// Записывает исключение в error.log рядом с exe и показывает MessageBox.
    /// Вызывается из разных потоков (см. InstallCrashHandlers); File.AppendAllText
    /// потокобезопасен для дозаписи, а кор-лог помещён в try/catch, чтобы логгер сам не падал.
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
