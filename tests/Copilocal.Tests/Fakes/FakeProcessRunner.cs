using Copilocal.Infrastructure;

namespace Copilocal.Tests.Fakes;

internal sealed class FakeProcessRunner : IProcessRunner
{
    private readonly Dictionary<(string File, string Args), Queue<RunResult>> _runs = [];
    private readonly Queue<RunResult> _runQueue = [];

    internal List<RunCall> RunCalls { get; } = [];
    internal List<RunInheritCall> RunInheritCalls { get; } = [];
    internal List<string> WhichCalls { get; } = [];
    internal Dictionary<string, string?> WhichResults { get; } = new(StringComparer.OrdinalIgnoreCase);
    internal RunResult DefaultRunResult { get; set; } = new(0, "", "");
    internal int RunInheritExitCode { get; set; }

    internal void AddRun(
        string file,
        string args,
        int code = 0,
        string stdout = "",
        string stderr = "",
        int? timeoutMs = null)
    {
        var key = (file, args);
        if (!_runs.TryGetValue(key, out var queue))
        {
            queue = new Queue<RunResult>();
            _runs[key] = queue;
        }

        queue.Enqueue(new RunResult(code, stdout, stderr));
    }

    internal void QueueRun(int code = 0, string stdout = "", string stderr = "") =>
        _runQueue.Enqueue(new RunResult(code, stdout, stderr));

    public (int Code, string Out, string Err) Run(string file, string args, int timeoutMs = 30_000)
    {
        RunCalls.Add(new RunCall(file, args, timeoutMs));

        if (_runs.TryGetValue((file, args), out var queue) && queue.Count > 0)
            return queue.Dequeue().ToTuple();

        if (_runQueue.Count > 0)
            return _runQueue.Dequeue().ToTuple();

        return DefaultRunResult.ToTuple();
    }

    public int RunInherit(string file, IEnumerable<string> args, IReadOnlyDictionary<string, string> env)
    {
        RunInheritCalls.Add(new RunInheritCall(file, args.ToArray(), new Dictionary<string, string>(env)));
        return RunInheritExitCode;
    }

    public string? Which(string exe)
    {
        WhichCalls.Add(exe);
        return WhichResults.TryGetValue(exe, out var path) ? path : null;
    }

    internal sealed record RunResult(int Code, string Out, string Err)
    {
        internal (int Code, string Out, string Err) ToTuple() => (Code, Out, Err);
    }

    internal sealed record RunCall(string File, string Args, int TimeoutMs);

    internal sealed record RunInheritCall(
        string File,
        IReadOnlyList<string> Args,
        IReadOnlyDictionary<string, string> Env);
}
