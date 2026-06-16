namespace Copilocal.Ui;

internal static class TerminalUi
{
    const string EnterAltScreen = "\u001b[?1049h";
    const string ExitAltScreen = "\u001b[?1049l";
    const string ClearAndHome = "\u001b[2J\u001b[H";

    // Tracks whether the alternate screen buffer is currently active, so chat mode can
    // temporarily suspend it (the alt buffer has no scrollback) and the menu can resume it.
    static bool _altActive;

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
            Write(EnterAltScreen);
            _altActive = true;
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

    /// <summary>Temporarily leave the alternate screen so output renders on the normal buffer,
    /// which keeps the terminal's native scrollback (the alt buffer has none). Disposing the
    /// returned token re-enters and clears the alternate screen. No-op when no alt screen is active.</summary>
    internal static IDisposable SuspendAltScreen()
    {
        if (!_altActive) return NoopScope.Instance;
        Write(ExitAltScreen);
        _altActive = false;
        return new ResumeScope();
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

    internal static bool SupportsAnsi()
    {
        if (Console.IsOutputRedirected) return false;
        string term = Environment.GetEnvironmentVariable("TERM") ?? "";
        if (term.Equals("dumb", StringComparison.OrdinalIgnoreCase)) return false;
        return true;
    }

    // Writing ExitAltScreen/EnterAltScreen is idempotent at the terminal level (a no-op when
    // already in the requested buffer), so callers can write them freely without one-shot guards.
    static void Write(string sequence)
    {
        try { Console.Write(sequence); }
        catch (IOException) { }
        catch (InvalidOperationException) { }
        catch (PlatformNotSupportedException) { }
    }

    sealed class AltScreenScope : IDisposable
    {
        readonly ConsoleCancelEventHandler _cancelHandler;
        readonly EventHandler _processExitHandler;
        bool _disposed;

        internal AltScreenScope()
        {
            _cancelHandler = (_, _) => RestoreAltScreen();
            _processExitHandler = (_, _) => RestoreAltScreen();

            try { Console.CancelKeyPress += _cancelHandler; }
            catch (InvalidOperationException) { }
            catch (PlatformNotSupportedException) { }

            try { AppDomain.CurrentDomain.ProcessExit += _processExitHandler; }
            catch (InvalidOperationException) { }
            catch (PlatformNotSupportedException) { }
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            RestoreAltScreen();

            try { Console.CancelKeyPress -= _cancelHandler; }
            catch (InvalidOperationException) { }
            catch (PlatformNotSupportedException) { }

            try { AppDomain.CurrentDomain.ProcessExit -= _processExitHandler; }
            catch (InvalidOperationException) { }
            catch (PlatformNotSupportedException) { }
        }

        static void RestoreAltScreen()
        {
            // Idempotent: harmless if the alt buffer is already suspended (chat mode) or exited.
            Write(ExitAltScreen);
            _altActive = false;
        }
    }

    sealed class ResumeScope : IDisposable
    {
        bool _disposed;

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            Write(EnterAltScreen);
            _altActive = true;
            ClearScreen();
        }
    }

    sealed class NoopScope : IDisposable
    {
        internal static readonly NoopScope Instance = new();
        public void Dispose() { }
    }
}
