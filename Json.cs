using System.Text;

namespace Copilocal;

internal static class Json
{
    /// <summary>Escape a string for embedding inside a JSON string literal. Handles the two
    /// structural characters (<c>\</c> and <c>"</c>) plus control characters, which a naive
    /// replace would leave to corrupt the document (e.g. a model name or user arg containing
    /// a newline or tab). Mirrors the escaping rules in RFC 8259 section 7.</summary>
    internal static string Escape(string s)
    {
        // Fast path: nothing to escape (the common case for model ids and paths).
        bool needsEscaping = false;
        foreach (char c in s)
            if (c is '\\' or '"' || c < ' ') { needsEscaping = true; break; }
        if (!needsEscaping) return s;

        var sb = new StringBuilder(s.Length + 8);
        foreach (char c in s)
        {
            switch (c)
            {
                case '\\': sb.Append("\\\\"); break;
                case '"': sb.Append("\\\""); break;
                case '\b': sb.Append("\\b"); break;
                case '\f': sb.Append("\\f"); break;
                case '\n': sb.Append("\\n"); break;
                case '\r': sb.Append("\\r"); break;
                case '\t': sb.Append("\\t"); break;
                default:
                    if (c < ' ') sb.Append("\\u").Append(((int)c).ToString("x4"));
                    else sb.Append(c);
                    break;
            }
        }
        return sb.ToString();
    }
}
