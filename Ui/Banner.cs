using Spectre.Console;

namespace Copilocal.Ui;

/// <summary>Renders the startup ASCII banner.</summary>
internal static class Banner
{
    static readonly string[] Wordmark =
    {
        " ██████╗ ██████╗ ██████╗ ██╗██╗      ██████╗  ██████╗ █████╗ ██╗     ",
        "██╔════╝██╔═══██╗██╔══██╗██║██║     ██╔═══██╗██╔════╝██╔══██╗██║     ",
        "██║     ██║   ██║██████╔╝██║██║     ██║   ██║██║     ███████║██║     ",
        "██║     ██║   ██║██╔═══╝ ██║██║     ██║   ██║██║     ██╔══██║██║     ",
        "╚██████╗╚██████╔╝██║     ██║███████╗╚██████╔╝╚██████╗██║  ██║███████╗",
        " ╚═════╝ ╚═════╝ ╚═╝     ╚═╝╚══════╝ ╚═════╝  ╚═════╝╚═╝  ╚═╝╚══════╝",
    };

    const int Gap = 3;
    const int FrameStep = 2;
    const int FrameDelayMs = 14;
    const int PulseDelayMs = 45;
    const int IconInnerWidth = 14;
    const int IconWidth = 20;
    static readonly ((int R, int G, int B) Top, (int R, int G, int B) Bottom)[] GradientSets =
    [
        ((45, 212, 191), (99, 102, 241)),   // teal -> indigo
        ((255, 170, 72), (214, 51, 132)),   // amber -> pink
        ((124, 246, 96), (63, 181, 255)),   // lime -> azure
        ((255, 96, 149), (122, 79, 255)),   // rose -> violet
        ((255, 216, 92), (64, 145, 255)),   // gold -> blue
    ];

    static readonly int Lines = Wordmark.Length;
    static readonly int WordWidth = Wordmark.Max(x => x.Length);
    static readonly int StartIconX = -IconWidth;
    static readonly int FinalIconX = WordWidth + Gap;

    /// <summary>Draw the banner with icon fly-in and vertical teal→indigo gradient.</summary>
    internal static void Draw(bool animate = true)
    {
        var (top, bot) = GradientSets[Random.Shared.Next(GradientSets.Length)];

        if (!animate || Console.IsOutputRedirected || !TerminalUi.SupportsAnsi())
        {
            DrawFrame(BuildFrame(FinalIconX, showTrail: false), top, bot);
            return;
        }

        bool drewFrame = false;
        for (int x = StartIconX; x <= FinalIconX; x += FrameStep)
        {
            if (drewFrame)
                Console.Write($"\u001b[{Lines}F");

            DrawFrame(BuildFrame(x, showTrail: true), top, bot);
            drewFrame = true;
            Thread.Sleep(FrameDelayMs);
        }

        // Ensure we always land exactly in the final resting position.
        if ((FinalIconX - StartIconX) % FrameStep != 0)
        {
            Console.Write($"\u001b[{Lines}F");
            DrawFrame(BuildFrame(FinalIconX, showTrail: true), top, bot);
        }

        // Quick post-landing beat: pulse nodes + prompt nudge + scan sweep.
        foreach (var beat in new (char leftNode, char rightNode, string prompt, int scanCol)[]
                 {
                     ('◉', '●', ">_", 1),
                     ('●', '◉', ">>_", 5),
                     ('◉', '◉', ">_", 9),
                     ('●', '●', ">_", -1),
                 })
        {
            Console.Write($"\u001b[{Lines}F");
            DrawFrame(BuildFrame(FinalIconX, showTrail: false, beat.leftNode, beat.rightNode, beat.prompt, beat.scanCol), top, bot);
            Thread.Sleep(PulseDelayMs);
        }
    }

    static string[] BuildFrame(
        int iconX,
        bool showTrail,
        char leftNode = '●',
        char rightNode = '●',
        string prompt = ">_",
        int scanCol = -1)
    {
        int revealWidth = Math.Clamp(iconX + IconWidth, 0, WordWidth);
        int canvasWidth = FinalIconX + IconWidth;
        var frame = new string[Lines];
        string[] iconRows = BuildIconRows(leftNode, rightNode, prompt, scanCol);

        for (int i = 0; i < Lines; i++)
        {
            var row = new char[canvasWidth];
            Array.Fill(row, ' ');

            string word = Wordmark[i];
            int visible = Math.Min(revealWidth, word.Length);
            for (int j = 0; j < visible; j++)
                row[j] = word[j];

            string icon = iconRows[i].PadRight(IconWidth);
            for (int j = 0; j < icon.Length; j++)
            {
                int at = iconX + j;
                if (at < 0 || at >= row.Length)
                    continue;
                // The icon should occlude the wordmark as it flies over it, then reveal it behind.
                row[at] = ' ';
                if (icon[j] != ' ')
                    row[at] = icon[j];
            }

            if (showTrail)
                PaintTrail(row, i, iconX, revealWidth);

            frame[i] = new string(row);
        }

        return frame;
    }

    static string[] BuildIconRows(char leftNode, char rightNode, string prompt, int scanCol)
    {
        int mirrored = scanCol < 0 ? -1 : Math.Clamp(IconInnerWidth - scanCol - 2, 0, IconInnerWidth - 2);
        return
        [
            "  ╭─════════════─╮",
            $"  │{BuildScan(scanCol)}├─{rightNode}",
            $"{leftNode}─┤{BuildPrompt(prompt)}│",
            $"  │{BuildScan(mirrored)}├─{rightNode}",
            "  ╰─════════════─╯",
            "                  ",
        ];
    }

    static string BuildPrompt(string prompt)
    {
        var inner = new char[IconInnerWidth];
        Array.Fill(inner, ' ');

        int start = 3;
        for (int i = 0; i < prompt.Length && (start + i) < inner.Length; i++)
            inner[start + i] = prompt[i];

        return new string(inner);
    }

    static string BuildScan(int scanCol)
    {
        var inner = new char[IconInnerWidth];
        Array.Fill(inner, ' ');
        for (int i = 2; i <= 11; i++)
            inner[i] = '┄';

        if (scanCol >= 0)
        {
            int c = Math.Clamp(scanCol, 0, IconInnerWidth - 2);
            inner[c] = '═';
            inner[c + 1] = '═';
        }

        return new string(inner);
    }

    static void PaintTrail(char[] row, int line, int iconX, int revealWidth)
    {
        int lead = iconX - 1;
        if (lead < 0)
            return;

        if (line == 2)
        {
            int[] offsets = [0, 2, 4, 6];
            char[] chars = ['═', '─', '·', '·'];
            for (int i = 0; i < offsets.Length; i++)
            {
                int at = lead - offsets[i];
                if (at >= 0 && at < row.Length && at < revealWidth)
                    row[at] = chars[i];
            }
        }
        else if (line == 1 || line == 3)
        {
            int at = lead - 2;
            if (at >= 0 && at < row.Length && at < revealWidth)
                row[at] = '·';
        }
    }

    static void DrawFrame(
        string[] frame,
        (int R, int G, int B) top,
        (int R, int G, int B) bot)
    {
        for (int i = 0; i < frame.Length; i++)
        {
            double t = i / (double)(frame.Length - 1);
            int r = (int)(top.R + ((bot.R - top.R) * t));
            int g = (int)(top.G + ((bot.G - top.G) * t));
            int b = (int)(top.B + ((bot.B - top.B) * t));
            AnsiConsole.Write(new Markup($"[#{r:X2}{g:X2}{b:X2}]{Markup.Escape(frame[i])}[/]\n"));
        }
    }
}
