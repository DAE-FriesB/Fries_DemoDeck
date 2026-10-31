using System.Diagnostics;
using System.Text;

namespace Ex.Core.Git;

public sealed record GitResult(int ExitCode, string StdOut, string StdErr)
{
    public bool Success => ExitCode == 0;

    /// <summary>Non-empty stdout lines, without trailing carriage returns.</summary>
    public IReadOnlyList<string> Lines =>
        StdOut.Split('\n').Select(l => l.TrimEnd('\r')).Where(l => l.Length > 0).ToList();
}

public enum LogKind { Command, Query, Output, Error, Info }

public sealed record LogEntry(DateTime Time, LogKind Kind, string? Repo, string Text);

public sealed class GitException(string message, GitResult result) : Exception(message)
{
    public GitResult Result { get; } = result;
}

/// <summary>
/// Runs the installed git command line. Every command is reported through <see cref="Logged"/>,
/// so the UI can show exactly what was executed.
/// </summary>
public sealed class GitRunner
{
    public string Executable { get; init; } = "git";

    public event Action<LogEntry>? Logged;

    public void Info(string text, string? repo = null) =>
        Logged?.Invoke(new LogEntry(DateTime.Now, LogKind.Info, repo, text));

    public void Error(string text, string? repo = null) =>
        Logged?.Invoke(new LogEntry(DateTime.Now, LogKind.Error, repo, text));

    /// <summary>Runs a command that changes something. Command and output are logged.</summary>
    public Task<GitResult> RunAsync(string workingDir, params string[] args) =>
        RunAsync(workingDir, args, query: false);

    /// <summary>Runs a read-only command. Only the command line is logged (as a query).</summary>
    public Task<GitResult> QueryAsync(string workingDir, params string[] args) =>
        RunAsync(workingDir, args, query: true);

    /// <summary>Like <see cref="RunAsync(string, string[])"/> but throws <see cref="GitException"/> on a non-zero exit code.</summary>
    public async Task<GitResult> RunCheckedAsync(string workingDir, params string[] args)
    {
        var result = await RunAsync(workingDir, args, query: false);
        if (!result.Success)
            throw new GitException($"git {args.FirstOrDefault()} failed: {FirstLine(result.StdErr, result.StdOut)}", result);
        return result;
    }

    public async Task<GitResult> QueryCheckedAsync(string workingDir, params string[] args)
    {
        var result = await RunAsync(workingDir, args, query: true);
        if (!result.Success)
            throw new GitException($"git {args.FirstOrDefault()} failed: {FirstLine(result.StdErr, result.StdOut)}", result);
        return result;
    }

    /// <summary>Returns the git version string, or null when git cannot be started.</summary>
    public async Task<string?> GetVersionAsync()
    {
        try
        {
            var result = await RunAsync(Environment.CurrentDirectory, ["--version"], query: true);
            return result.Success ? result.StdOut.Trim() : null;
        }
        catch (Exception)
        {
            return null;
        }
    }

    public async Task<GitResult> RunAsync(string workingDir, IReadOnlyList<string> args, bool query)
    {
        var repo = Path.GetFileName(workingDir.TrimEnd('\\', '/'));
        Logged?.Invoke(new LogEntry(DateTime.Now, query ? LogKind.Query : LogKind.Command, repo, FormatCommand(args)));

        var psi = new ProcessStartInfo(Executable)
        {
            WorkingDirectory = workingDir,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };
        // Keep paths readable (no octal escapes) and never wait for an editor or a credential prompt.
        psi.ArgumentList.Add("-c");
        psi.ArgumentList.Add("core.quotepath=off");
        foreach (var arg in args)
            psi.ArgumentList.Add(arg);
        psi.Environment["GIT_TERMINAL_PROMPT"] = "0";
        psi.Environment["GIT_MERGE_AUTOEDIT"] = "no";
        psi.Environment["GIT_EDITOR"] = "true";

        using var process = Process.Start(psi) ?? throw new InvalidOperationException("Could not start git.");
        process.StandardInput.Close();
        var stdOutTask = process.StandardOutput.ReadToEndAsync();
        var stdErrTask = process.StandardError.ReadToEndAsync();
        await Task.WhenAll(stdOutTask, stdErrTask);
        await process.WaitForExitAsync();

        var result = new GitResult(process.ExitCode, stdOutTask.Result, stdErrTask.Result);
        if (!query)
        {
            var output = (result.StdOut + result.StdErr).TrimEnd();
            if (output.Length > 0)
                Logged?.Invoke(new LogEntry(DateTime.Now, result.Success ? LogKind.Output : LogKind.Error, repo, output));
            else if (!result.Success)
                Logged?.Invoke(new LogEntry(DateTime.Now, LogKind.Error, repo, $"exit code {result.ExitCode}"));
        }
        else if (!result.Success && result.StdErr.Length > 0)
        {
            Logged?.Invoke(new LogEntry(DateTime.Now, LogKind.Query, repo, $"  -> exit {result.ExitCode}: {FirstLine(result.StdErr)}"));
        }
        return result;
    }

    public static string FormatCommand(IEnumerable<string> args) =>
        "git " + string.Join(' ', args.Select(Quote));

    private static string Quote(string arg) =>
        arg.Length == 0 || arg.Any(c => char.IsWhiteSpace(c) || c == '"')
            ? "\"" + arg.Replace("\"", "\\\"") + "\""
            : arg;

    private static string FirstLine(params string[] texts)
    {
        foreach (var text in texts)
        {
            var line = text.Split('\n').Select(l => l.Trim()).FirstOrDefault(l => l.Length > 0);
            if (line != null)
                return line;
        }
        return "(no output)";
    }
}
