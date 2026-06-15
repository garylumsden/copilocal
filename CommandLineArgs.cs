namespace Copilocal;

/// <summary>Parsed command-line arguments. copilocal consumes its own flags and forwards the
/// remainder (after an optional <c>--</c>) to <c>copilot</c>.</summary>
internal sealed record CommandLineArgs(
    bool DryRun,
    bool Offline,
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

    /// <summary>True when copilocal should mint and manage a stable session id, enabling the
    /// "continue with a different model" loop.</summary>
    internal bool WantsManagedSession => Interactive && !DryRun && !UserManagedSession;

    internal static CommandLineArgs Parse(IEnumerable<string> argv)
    {
        var args = new List<string>(argv);
        bool dryRun = ExtractFlag(args, "--dry-run");
        bool offline = ExtractFlag(args, "--offline");
        string? sessionName = ExtractValue(args, "--name");
        int pick = ExtractInt(args, "--pick");

        // Anything after "--" (or whatever is left) is forwarded to copilot.
        if (args.Count > 0 && args[0] == "--") args.RemoveAt(0);

        bool userSession = args.Any(a =>
            a == "-r" || a == "--continue" ||
            a.StartsWith("--resume", StringComparison.Ordinal) ||
            a.StartsWith("--session-id", StringComparison.Ordinal) ||
            a == "-n" || a.StartsWith("--name", StringComparison.Ordinal));

        return new CommandLineArgs(dryRun, offline, sessionName, pick, args)
        {
            UserManagedSession = userSession,
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
        if (i < 0 || i + 1 >= args.Count) return -1;
        int.TryParse(args[i + 1], out int v);
        args.RemoveRange(i, 2);
        return v;
    }

    static string? ExtractValue(List<string> args, string flag)
    {
        int i = args.IndexOf(flag);
        if (i < 0 || i + 1 >= args.Count) return null;
        string v = args[i + 1];
        args.RemoveRange(i, 2);
        return v;
    }
}
