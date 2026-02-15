using System.Diagnostics;
using System.Windows.Forms;

namespace ReefCams.Processor.Launcher;

internal static class Program
{
    [STAThread]
    private static int Main(string[] args)
    {
        var appRoot = AppContext.BaseDirectory;
        var processorExe = Path.Combine(appRoot, "_internal", "ReefCams.Processor.exe");
        if (!File.Exists(processorExe))
        {
            MessageBox.Show(
                $"ReefCams.Processor.exe was not found at:\n{processorExe}",
                "ReefCams",
                MessageBoxButtons.OK,
                MessageBoxIcon.Error);
            return 1;
        }

        var psi = new ProcessStartInfo
        {
            FileName = processorExe,
            Arguments = BuildArgumentString(args),
            WorkingDirectory = appRoot,
            UseShellExecute = false
        };

        using var process = Process.Start(psi);
        if (process is null)
        {
            MessageBox.Show(
                "Failed to start ReefCams.Processor.",
                "ReefCams",
                MessageBoxButtons.OK,
                MessageBoxIcon.Error);
            return 1;
        }

        process.WaitForExit();
        return process.ExitCode;
    }

    private static string BuildArgumentString(IEnumerable<string> args)
    {
        return string.Join(" ", args.Select(QuoteArgument));
    }

    private static string QuoteArgument(string value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return "\"\"";
        }

        if (!value.Any(ch => char.IsWhiteSpace(ch) || ch == '"' || ch == '\\'))
        {
            return value;
        }

        var sb = new System.Text.StringBuilder();
        sb.Append('"');

        var backslashes = 0;
        foreach (var ch in value)
        {
            if (ch == '\\')
            {
                backslashes++;
                continue;
            }

            if (ch == '"')
            {
                sb.Append('\\', backslashes * 2 + 1);
                sb.Append('"');
                backslashes = 0;
                continue;
            }

            if (backslashes > 0)
            {
                sb.Append('\\', backslashes);
                backslashes = 0;
            }

            sb.Append(ch);
        }

        if (backslashes > 0)
        {
            sb.Append('\\', backslashes * 2);
        }

        sb.Append('"');
        return sb.ToString();
    }
}
