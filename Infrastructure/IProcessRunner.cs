namespace Copilocal.Infrastructure;

internal interface IProcessRunner
{
    (int Code, string Out, string Err) Run(string file, string args, int timeoutMs = 30_000);

    int RunInherit(string file, IEnumerable<string> args, IReadOnlyDictionary<string, string> env);

    string? Which(string exe);
}
