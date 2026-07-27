using BsgoBot.Ui;

namespace BsgoBot;

internal static class Program
{
    [STAThread]
    private static void Main(string[] args)
    {
        for (int i = 0; i + 1 < args.Length; i++)
            if (args[i] is "--config" or "-c")
                Config.FilePath = Path.IsPathRooted(args[i + 1])
                    ? args[i + 1]
                    : Path.Combine(AppContext.BaseDirectory, args[i + 1]);

        ApplicationConfiguration.Initialize();
        Application.Run(new MainForm());
    }
}
