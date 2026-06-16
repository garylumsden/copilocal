namespace Copilocal.Ui;

internal static class TerminalUi
{
    const string EnterAltScreen = "\u001b[?1049h";
    const string ExitAltScreen = "\u001b[?1049l";
    const string ClearAndHome = "\u001b[2J\u001b[H";

    internal static IDisposable StartSession(bool interactive)
    {
        if (!interactive || Console.IsOutputRedirected)
            return NoopScope.Instance;

        if (!SupportsAnsi())
        {
            ClearScreen();
            return NoopScope.Instance;
        }

        try
        {
            Console.Write(EnterAltScreen);
            ClearScreen();
            return new AltScreenScope();
        }
        catch (IOException)
        {
            ClearScreen();
            return NoopScope.Instance;
        }
        catch (InvalidOperationException)
        {
            ClearScreen();
            return NoopScope.Instance;
        }
        catch (PlatformNotSupportedException)
        {
            ClearScreen();
            return NoopScope.Instance;
        }
    }

    internal static void ClearScreen()
    {
        if (Console.IsOutputRedirected) return;

        if (SupportsAnsi())
        {
            try
            {
                Console.Write(ClearAndHome);
                return;
            }
            catch (IOException) { }
            catch (InvalidOperationException) { }
            catch (PlatformNotSupportedException) { }
        }

        try { Console.Clear(); }
        catch (IOException) { }
        catch (InvalidOperationException) { }
        catch (PlatformNotSupportedException) { }
    }

    static bool SupportsAnsi()
    {
        if (Console.IsOutputRedirected) return false;
        string term = Environment.GetEnvironmentVariable("TERM") ?? "";
        if (term.Equals("dumb", StringComparison.OrdinalIgnoreCase)) return false;
        return true;
    }

    sealed class AltScreenScope : IDisposable
    {
        bool _disposed;

        public void Dispose()
        {
            if (_disposed || Console.IsOutputRedirected) return;
            _disposed = true;
            try
            {
                Console.Write(ExitAltScreen);
            }
            catch (IOException) { }
            catch (InvalidOperationException) { }
            catch (PlatformNotSupportedException) { }
        }
    }

    sealed class NoopScope : IDisposable
    {
        internal static readonly NoopScope Instance = new();
        public void Dispose() { }
    }
}
