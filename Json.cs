namespace Copilocal;

internal static class Json
{
    internal static string Escape(string s) => s.Replace("\\", "\\\\").Replace("\"", "\\\"");
}
