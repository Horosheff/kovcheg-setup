namespace KovchegVPN;

public static class Program
{
    // Elevated worker идёт БЕЗ WPF: никакого Dispatcher/SynchronizationContext,
    // значит синхронные ожидания в ElevatedWorker не дедлочатся.
    [STAThread]
    public static int Main(string[] args)
    {
        if (args.Contains("--elevated"))
        {
            var mode = args.SkipWhile(a => a != "--elevated").Skip(1).FirstOrDefault() ?? "";
            return ElevatedWorker.Run(mode);
        }

        // App.xaml пустой (без StartupUri/ресурсов) — InitializeComponent не нужен.
        var app = new App();
        return app.Run();
    }
}
