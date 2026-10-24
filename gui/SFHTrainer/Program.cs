using System.Windows.Forms;
using SFHTrainer.Protocol;

namespace SFHTrainer;

internal static class Program
{
    [STAThread]
    private static int Main(string[] args)
    {
        try { Application.SetHighDpiMode(HighDpiMode.PerMonitorV2); } catch { /* 老系统忽略 */ }
        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);

        var headless = HasFlag(args, "--selftest");
        var uiSmoke = HasFlag(args, "--selftest-ui");
        var reportPath = GetOption(args, "--selftest-out");
        var holdSeconds = int.TryParse(GetOption(args, "--hold"), out var hold) ? Math.Max(0, hold) : 0;
        var screenshotPath = GetOption(args, "--screenshot");

        // 生产固定用协议规定的 SFHTrainer；只有显式 --pipe-name 才换管道（自测指向 mock）。
        // 自测模式下默认用 SFHTrainerMock，避免误连/误改游戏里正在运行的代理。
        var explicitPipe = GetOption(args, "--pipe-name");
        var smokePipe = string.IsNullOrWhiteSpace(explicitPipe) ? SelfTest.MockPipeName : explicitPipe!;
        var normalPipe = string.IsNullOrWhiteSpace(explicitPipe) ? AgentClient.DefaultPipeName : explicitPipe!;

        if (headless)
        {
            var path = string.IsNullOrWhiteSpace(reportPath) ? SelfTest.DefaultReportPath : reportPath!;
            return SelfTest.RunHeadlessAsync(path, smokePipe).GetAwaiter().GetResult();
        }

        if (uiSmoke)
        {
            var path = string.IsNullOrWhiteSpace(reportPath)
                ? Path.Combine(AppContext.BaseDirectory, "selftest-ui-report.txt")
                : reportPath!;
            var form = new MainForm(smokeTest: true, reportPath: path, pipeName: smokePipe, holdSeconds: holdSeconds, screenshotPath: screenshotPath);
            Application.Run(form);
            return form.SmokePassed ? 0 : 1;
        }

        Application.Run(new MainForm(pipeName: normalPipe));
        return 0;
    }

    static bool HasFlag(string[] args, string name)
        => args.Any(a => a.Equals(name, StringComparison.OrdinalIgnoreCase));

    static string? GetOption(string[] args, string name)
    {
        for (var i = 0; i < args.Length; i++)
        {
            if (args[i].Equals(name, StringComparison.OrdinalIgnoreCase) && i + 1 < args.Length)
                return args[i + 1];
            var prefix = name + "=";
            if (args[i].StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                return args[i][prefix.Length..];
        }
        return null;
    }
}
