namespace Copilocal.Cli;

/// <summary>Parsed command-line arguments. copilocal consumes its own flags and forwards the
/// remainder (after an optional <c>--</c>) to <c>copilot</c>.</summary>
internal sealed record CommandLineArgs(
    bool DryRun,
    bool Offline,
    string? OffloadPrompt,
    string? SessionName,
    int Pick,
    List<string> CopilotArgs)
{
    /// <summary>True when no explicit <c>--pick N</c> was given (drive the picker UI).</summary>
    internal bool Interactive => Pick < 1;

    /// <summary>True when the forwarded args drive a copilot session themselves
    /// (<c>--resume</c>/<c>--continue</c>/<c>--session-id</c>/<c>--name</c>), so copilocal must
    /// not manage one for them.</summary>
    internal bool UserManagedSession { get; private init; }

    /// <summary>True when <c>--version</c>/<c>-V</c> was given: print the version and exit.</summary>
    internal bool ShowVersion { get; private init; }

    /// <summary>True when <c>--help</c>/<c>-h</c>/<c>-?</c> was given: print usage and exit.</summary>
    internal bool ShowHelp { get; private init; }

    /// <summary>True when copilocal should mint and manage a stable session id, enabling the
    /// "continue with a different model" loop.</summary>
    internal bool WantsManagedSession => Interactive && !DryRun && !UserManagedSession;

    internal static CommandLineArgs Parse(IEnumerable<string> argv)
    {
        var all = new List<string>(argv);

        // copilocal's own flags are parsed only from the segment BEFORE a "--" separator;
        // everything after "--" is forwarded to copilot verbatim (never consumed here).
        int sep = all.IndexOf("--");
        var own = sep >= 0 ? all.GetRange(0, sep) : all;
        var forwarded = sep >= 0 ? all.GetRange(sep + 1, all.Count - sep - 1) : new List<string>();

        bool dryRun = ExtractFlag(own, "--dry-run");
        bool offline = ExtractFlag(own, "--offline");
        // Non-short-circuit OR so every alias is removed from the forwarded args.
        bool showVersion = ExtractFlag(own, "--version") | ExtractFlag(own, "-V");
        bool showHelp = ExtractFlag(own, "--help") | ExtractFlag(own, "-h") | ExtractFlag(own, "-?");
        string? offloadPrompt = ExtractValue(own, "--offload");
        string? sessionName = ExtractValue(own, "--name");
        int pick = ExtractInt(own, "--pick");

        // Unrecognized args before "--" are forwarded too, ahead of the post-"--" args.
        var copilotArgs = own;
        copilotArgs.AddRange(forwarded);

        bool userSession = copilotArgs.Any(a =>
            a == "-r" || a == "--continue" ||
            a.StartsWith("--resume", StringComparison.Ordinal) ||
            a.StartsWith("--session-id", StringComparison.Ordinal) ||
            a == "-n" || a.StartsWith("--name", StringComparison.Ordinal));

        return new CommandLineArgs(dryRun, offline, offloadPrompt, sessionName, pick, copilotArgs)
        {
            UserManagedSession = userSession,
            ShowVersion = showVersion,
            ShowHelp = showHelp,
        };
    }

    static bool ExtractFlag(List<string> args, string flag)
    {
        int i = args.IndexOf(flag);
        if (i < 0) return false;
        args.RemoveAt(i);
        return true;
    }

    static int ExtractInt(List<string> args, string flag)
    {
        int i = args.IndexOf(flag);
        if (i < 0) return -1;
        if (i + 1 >= args.Count)
        {
            args.RemoveAt(i);
            return -1;
        }
        int.TryParse(args[i + 1], out int v);
        args.RemoveRange(i, 2);
        return v;
    }

    static string? ExtractValue(List<string> args, string flag)
    {
        int i = args.IndexOf(flag);
        if (i < 0) return null;
        if (i + 1 >= args.Count)
        {
            args.RemoveAt(i);
            return null;
        }
        string v = args[i + 1];
        args.RemoveRange(i, 2);
        return v;
    }
}
