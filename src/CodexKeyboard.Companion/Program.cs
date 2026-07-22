namespace CodexKeyboard.Companion;

internal static class Program
{
    private const string SingleInstanceMutexName = @"Local\CodexKeyboard.Companion";

    [STAThread]
    private static void Main(string[] args)
    {
        using var mutex = new Mutex(true, SingleInstanceMutexName, out var isFirstInstance);
        if (!isFirstInstance)
        {
            return;
        }

        try
        {
            ApplicationConfiguration.Initialize();
            using var context = new CompanionApplicationContext(
                showTestsOnStartup: !args.Contains("--tray", StringComparer.OrdinalIgnoreCase));
            Application.Run(context);
        }
        finally
        {
            mutex.ReleaseMutex();
        }
    }
}
