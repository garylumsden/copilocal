using Spectre.Console;

namespace Copilocal.Launch;

internal sealed class TokenUsageTracker
{
    readonly string _model;
    int _promptTotal;
    int _completionTotal;
    int _total;
    TokenUsage? _last;
    int _renderWidth;
    int _lastLeft = -1;
    int _lastTop = -1;
    int _lastWindowWidth;
    int _lastWindowHeight;
    bool _enabled = true;

    internal TokenUsageTracker(string model)
    {
        _model = model;
    }

    internal static string BuildTokenUsageLine(string model, int promptTotal, int completionTotal, int total, TokenUsage? last)
    {
        string modelLabel = ShortModelLabel(model);
        return last is null
            ? $"m:{modelLabel} | tok sum:{total} p:{promptTotal} c:{completionTotal} | last:n/a"
            : $"m:{modelLabel} | tok sum:{total} p:{promptTotal} c:{completionTotal} | last:{last.TotalTokens} (p{last.PromptTokens}/c{last.CompletionTokens})";
    }

    internal static (string Text, int Left) BuildTokenUsageRenderText(string text, int width)
    {
        int maxLen = Math.Max(10, width - 2);
        if (text.Length > maxLen) text = text[..maxLen];
        int left = Math.Max(0, width - 1 - text.Length);
        return (text, left);
    }

    internal void Reset()
    {
        _promptTotal = 0;
        _completionTotal = 0;
        _total = 0;
        _last = null;
        Render();
    }

    internal void Update(TokenUsage? usage)
    {
        _last = usage;
        if (usage is null) return;
        _promptTotal += usage.PromptTokens;
        _completionTotal += usage.CompletionTokens;
        _total += usage.TotalTokens;
    }

    internal void Render()
    {
        if (!_enabled || Console.IsOutputRedirected) return;
        try
        {
            int width = Console.WindowWidth;
            int height = Console.WindowHeight;
            if (width < 20 || height < 2) return;

            string text = BuildTokenUsageLine(_model, _promptTotal, _completionTotal, _total, _last);
            (text, int left) = BuildTokenUsageRenderText(text, width);
            int top = height - 1;

            int saveLeft = Math.Clamp(Console.CursorLeft, 0, Math.Max(0, width - 1));
            int saveTop = Math.Clamp(Console.CursorTop, 0, Math.Max(0, height - 1));

            if (_lastTop >= 0 &&
                (_lastTop != top || _lastLeft != left || _lastWindowWidth != width || _lastWindowHeight != height))
                ClearAt(_lastLeft, _lastTop, _renderWidth, width, height);

            Console.SetCursorPosition(left, top);
            Console.Write(text);
            _renderWidth = text.Length;
            _lastLeft = left;
            _lastTop = top;
            _lastWindowWidth = width;
            _lastWindowHeight = height;
            Console.SetCursorPosition(saveLeft, saveTop);
        }
        catch (IOException)
        {
            Disable("I/O unavailable");
        }
        catch (InvalidOperationException)
        {
            Disable("cursor control unavailable");
        }
        catch (PlatformNotSupportedException)
        {
            Disable("platform unsupported");
        }
        catch (ArgumentOutOfRangeException)
        {
            // Terminal resize race; skip this render and continue.
        }
    }

    internal void Hide()
    {
        if (!_enabled || Console.IsOutputRedirected || _lastTop < 0 || _renderWidth <= 0) return;
        try
        {
            int width = Console.WindowWidth;
            int height = Console.WindowHeight;
            int saveLeft = Math.Clamp(Console.CursorLeft, 0, Math.Max(0, width - 1));
            int saveTop = Math.Clamp(Console.CursorTop, 0, Math.Max(0, height - 1));
            ClearAt(_lastLeft, _lastTop, _renderWidth, width, height);
            Console.SetCursorPosition(saveLeft, saveTop);
            _lastLeft = -1;
            _lastTop = -1;
        }
        catch (IOException)
        {
            Disable("I/O unavailable");
        }
        catch (InvalidOperationException)
        {
            Disable("cursor control unavailable");
        }
        catch (PlatformNotSupportedException)
        {
            Disable("platform unsupported");
        }
        catch (ArgumentOutOfRangeException)
        {
            // Terminal resize race; skip and keep running.
        }
    }

    static string ShortModelLabel(string model)
    {
        string trimmed = (model ?? "").Trim();
        if (trimmed.Length <= 28) return trimmed;
        return trimmed[..18] + "..." + trimmed[^7..];
    }

    static void ClearAt(int left, int top, int widthToClear, int windowWidth, int windowHeight)
    {
        if (left < 0 || top < 0 || top >= windowHeight || left >= windowWidth) return;
        int clearWidth = Math.Min(widthToClear, windowWidth - left);
        if (clearWidth <= 0) return;
        Console.SetCursorPosition(left, top);
        Console.Write(new string(' ', clearWidth));
    }

    void Disable(string reason)
    {
        if (!_enabled) return;
        _enabled = false;
        AnsiConsole.MarkupLine($"[dim]Token tracker disabled ({Markup.Escape(reason)}).[/]");
    }
}