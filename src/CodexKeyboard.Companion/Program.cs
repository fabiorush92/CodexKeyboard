namespace CodexKeyboard.Companion;

internal static class Program
{
    private const string SingleInstanceMutexName = @"Local\CodexKeyboard.Companion";

    [STAThread]
    private static void Main(string[] args)
    {
        try
        {
            Run(args);
        }
        catch (Exception exception)
        {
            try
            {
                MessageBox.Show(
                    $"CodexKeyboard stopped after an unexpected application error.{Environment.NewLine}{Environment.NewLine}{exception.Message}",
                    "CodexKeyboard",
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Error);
            }
            catch
            {
            }
        }
    }

    private static void Run(string[] args)
    {
        using var mutex = new Mutex(true, SingleInstanceMutexName, out var isFirstInstance);
        if (!isFirstInstance)
        {
            return;
        }

        try
        {
            ApplicationConfiguration.Initialize();
            Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);
            using var context = new CompanionApplicationContext(
                showTestsOnStartup: !args.Contains("--tray", StringComparer.OrdinalIgnoreCase));
            Application.ThreadException += (_, eventArgs) =>
                context.HandleFatalException(eventArgs.Exception);
            Application.Run(context);
        }
        finally
        {
            mutex.ReleaseMutex();
        }
    }
}
