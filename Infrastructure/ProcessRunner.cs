using System.Diagnostics;
using System.Text;

namespace Copilocal.Infrastructure;

/// <summary>Thin wrapper for running CLI tools and capturing their output.</summary>
internal sealed class ProcessRunner : IProcessRunner
{
    const int DefaultTimeoutMs = 30_000;
    const int StreamDrainTimeoutMs = 3_000;

    public (int Code, string Out, string Err) Run(string file, string args, int timeoutMs = DefaultTimeoutMs)
    {
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = file,
                Arguments = args,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                StandardOutputEncoding = new UTF8Encoding(false),
                StandardErrorEncoding = new UTF8Encoding(false),
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            using var p = Process.Start(psi);
            if (p is null) return (-1, "", "failed to start");
            // Read both streams concurrently to avoid a full-pipe deadlock.
            var outTask = p.StandardOutput.ReadToEndAsync();
            var errTask = p.StandardError.ReadToEndAsync();
            bool exited = p.WaitForExit(timeoutMs);
            if (!exited)
            {
                try { p.Kill(true); }
                catch (InvalidOperationException)
                {
                    // best-effort: timed-out process may already have exited.
                }
                catch (System.ComponentModel.Win32Exception)
                {
                    // best-effort: the OS may refuse to kill an already-exiting process.
                }
            }
            // Bound the drain: a child the CLI spawned (e.g. a provider daemon) can
            // inherit our stdout handle and keep the pipe open, so ReadToEndAsync would
            // otherwise block forever even though the CLI itself has exited. Wait only
            // briefly, then take whatever completed.
            try { Task.WaitAll(new[] { outTask, errTask }, StreamDrainTimeoutMs); }
            catch (AggregateException)
            {
                // best-effort: return whatever output drained before a task faulted.
            }
            string o = outTask.Status == TaskStatus.RanToCompletion ? outTask.Result : "";
            string e = errTask.Status == TaskStatus.RanToCompletion ? errTask.Result : "";
            int code = exited ? SafeExitCode(p) : -2;
            return (code, o, e);
        }
        catch (System.ComponentModel.Win32Exception ex) { return (-1, "", ex.Message); }
        catch (InvalidOperationException ex) { return (-1, "", ex.Message); }
    }

    static int SafeExitCode(Process p)
    {
        try { return p.ExitCode; }
        catch (InvalidOperationException)
        {
            // best-effort: process exit code can be unavailable after races.
            return -1;
        }
    }

    /// <summary>Launch a child process that inherits this console, with extra env vars. Returns its exit code.</summary>
    public int RunInherit(string file, IEnumerable<string> args, IReadOnlyDictionary<string, string> env)
    {
        var psi = new ProcessStartInfo { FileName = file, UseShellExecute = false };
        foreach (var a in args) psi.ArgumentList.Add(a);
        foreach (var kv in env) psi.Environment[kv.Key] = kv.Value;
        using var p = Process.Start(psi);
        if (p is null) return -1;
        p.WaitForExit();
        return p.ExitCode;
    }

    public string? Which(string exe)
    {
        // `where` is Windows; `which` is the POSIX equivalent on macOS/Linux.
        string finder = OperatingSystem.IsWindows() ? "where" : "which";
        var (code, outp, _) = Run(finder, exe, 5000);
        if (code != 0) return null;
        var first = outp.Split('\n', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault()?.Trim();
        return string.IsNullOrWhiteSpace(first) ? null : first;
    }
}
