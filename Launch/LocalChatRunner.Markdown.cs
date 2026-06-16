using System.Text;
using System.Text.RegularExpressions;
using Markdig;
using Markdig.Extensions.Tables;
using Markdig.Syntax;
using Markdig.Syntax.Inlines;
using MarkdigTable = Markdig.Extensions.Tables.Table;
using MarkdigTableCell = Markdig.Extensions.Tables.TableCell;
using MarkdigTableRow = Markdig.Extensions.Tables.TableRow;
using Spectre.Console;

namespace Copilocal.Launch;

internal sealed partial class LocalChatRunner
{
    static void RenderAssistantContent(string content)
    {
        AnsiConsole.MarkupLine("[springgreen3]assistant>[/]");
        var doc = Markdown.Parse(content ?? "", ChatMarkdownPipeline);
        if (doc.Count == 0)
        {
            AnsiConsole.WriteLine();
            return;
        }
        foreach (var block in doc)
            RenderMarkdownBlock(block);
    }

    static void RenderMarkdownBlock(Block block)
    {
        switch (block)
        {
            case MarkdigTable table:
                if (TryConvertMarkdigTable(table, out var parsed))
                    RenderMarkdownTable(parsed);
                break;
            case HeadingBlock heading:
                AnsiConsole.MarkupLine($"[bold]{RenderInlineMarkup(heading.Inline)}[/]");
                AnsiConsole.WriteLine();
                break;
            case ParagraphBlock paragraph:
                AnsiConsole.MarkupLine(RenderInlineMarkup(paragraph.Inline));
                AnsiConsole.WriteLine();
                break;
            case ListBlock list:
                RenderListBlock(list, 0);
                AnsiConsole.WriteLine();
                break;
            case QuoteBlock quote:
                RenderQuoteBlock(quote);
                AnsiConsole.WriteLine();
                break;
            case FencedCodeBlock fenced:
                RenderCodeLines(fenced.Lines.ToString());
                AnsiConsole.WriteLine();
                break;
            case CodeBlock code:
                RenderCodeLines(code.Lines.ToString());
                AnsiConsole.WriteLine();
                break;
            case ThematicBreakBlock:
                AnsiConsole.MarkupLine("[grey46]────────────────────────[/]");
                AnsiConsole.WriteLine();
                break;
            case LinkReferenceDefinitionGroup _:
                break;
            default:
                RenderFallbackBlock(block);
                break;
        }
    }

    static void RenderListBlock(ListBlock list, int depth)
    {
        int index = 1;
        foreach (var child in list)
        {
            if (child is not ListItemBlock item) continue;
            string indent = new(' ', depth * 2);
            string marker = list.IsOrdered ? $"{index}." : "•";
            bool wroteLead = false;
            foreach (var block in item)
            {
                if (block is ListBlock nested)
                {
                    RenderListBlock(nested, depth + 1);
                    continue;
                }

                string line = RenderBlockSingleLine(block);
                if (line.Length == 0) continue;
                if (!wroteLead)
                {
                    AnsiConsole.MarkupLine($"{Markup.Escape(indent)}{Markup.Escape(marker)} {line}");
                    wroteLead = true;
                }
                else
                    AnsiConsole.MarkupLine($"{Markup.Escape(indent)}  {line}");
            }

            if (!wroteLead)
                AnsiConsole.MarkupLine($"{Markup.Escape(indent)}{Markup.Escape(marker)}");

            index++;
        }
    }

    static void RenderQuoteBlock(QuoteBlock quote)
    {
        foreach (var child in quote)
        {
            string line = RenderBlockSingleLine(child);
            if (line.Length == 0) continue;
            AnsiConsole.MarkupLine($"[grey70]│[/] {line}");
        }
    }

    static void RenderCodeLines(string code)
    {
        string normalized = (code ?? "").Replace("\r\n", "\n");
        foreach (string line in normalized.Split('\n'))
        {
            if (line.Length == 0) continue;
            AnsiConsole.MarkupLine($"[grey70]{Markup.Escape(line)}[/]");
        }
    }

    static void RenderFallbackBlock(Block block)
    {
        if (block is ContainerBlock container)
        {
            foreach (var child in container)
                RenderMarkdownBlock(child);
            return;
        }

        if (block is LeafBlock leaf)
        {
            if (leaf.Inline is not null)
                AnsiConsole.MarkupLine(RenderInlineMarkup(leaf.Inline));
            else
                RenderCodeLines(leaf.Lines.ToString());
            AnsiConsole.WriteLine();
        }
    }

    static string RenderBlockSingleLine(Block block) =>
        block switch
        {
            ParagraphBlock p => RenderInlineMarkup(p.Inline),
            HeadingBlock h => $"[bold]{RenderInlineMarkup(h.Inline)}[/]",
            LeafBlock { Inline: not null } leaf => RenderInlineMarkup(leaf.Inline),
            LeafBlock leaf => Markup.Escape(leaf.Lines.ToString().Replace("\r\n", " ").Replace('\n', ' ').Trim()),
            _ => Markup.Escape((block.ToString() ?? "").Trim()),
        };

    internal static bool TryExtractFirstMarkdownTable(string markdown, out MarkdownTable table)
    {
        var doc = Markdown.Parse(markdown ?? "", ChatMarkdownPipeline);
        foreach (var block in doc)
            if (block is MarkdigTable markdigTable && TryConvertMarkdigTable(markdigTable, out table))
                return true;

        table = new MarkdownTable(Array.Empty<string>(), Array.Empty<IReadOnlyList<string>>());
        return false;
    }

    static bool TryConvertMarkdigTable(MarkdigTable markdigTable, out MarkdownTable table)
    {
        var rows = markdigTable.OfType<MarkdigTableRow>().ToList();
        if (rows.Count == 0)
        {
            table = new MarkdownTable(Array.Empty<string>(), Array.Empty<IReadOnlyList<string>>());
            return false;
        }

        var headerRow = rows.FirstOrDefault(r => r.IsHeader) ?? rows[0];
        var dataRows = rows.Where(r => !ReferenceEquals(r, headerRow)).ToList();
        var headers = headerRow.OfType<MarkdigTableCell>().Select(RenderTableCellText).ToList();
        if (headers.Count < 2)
        {
            table = new MarkdownTable(Array.Empty<string>(), Array.Empty<IReadOnlyList<string>>());
            return false;
        }

        var parsedRows = new List<IReadOnlyList<string>>();
        foreach (var row in dataRows)
        {
            var cells = row.OfType<MarkdigTableCell>().Select(RenderTableCellText).ToList();
            parsedRows.Add(NormalizeRow(cells, headers.Count));
        }

        if (parsedRows.Count == 0)
        {
            table = new MarkdownTable(Array.Empty<string>(), Array.Empty<IReadOnlyList<string>>());
            return false;
        }

        table = new MarkdownTable(headers, parsedRows);
        return true;
    }

    static void RenderMarkdownTable(MarkdownTable source)
    {
        var table = new Spectre.Console.Table().Border(TableBorder.Rounded).BorderColor(Color.Grey);
        table.Expand();
        foreach (var header in source.Headers)
            table.AddColumn(Markup.Escape(header.Length == 0 ? " " : header));

        foreach (var row in source.Rows)
            table.AddRow(row.Select(cell => Markup.Escape(cell)).ToArray());

        AnsiConsole.Write(table);
        AnsiConsole.WriteLine();
    }

    static string RenderTableCellText(MarkdigTableCell cell)
    {
        var parts = new List<string>();
        foreach (var child in cell)
        {
            string line = RenderBlockSingleLine(child);
            if (line.Length > 0) parts.Add(StripMarkupTags(line));
        }
        return string.Join(" ", parts).Trim();
    }

    static string StripMarkupTags(string value)
    {
        if (value.Length == 0) return value;
        var sb = new StringBuilder(value.Length);
        bool inTag = false;
        foreach (char ch in value)
        {
            if (ch == '[') { inTag = true; continue; }
            if (inTag && ch == ']') { inTag = false; continue; }
            if (!inTag) sb.Append(ch);
        }
        return sb.ToString();
    }

    static IReadOnlyList<string> NormalizeRow(IReadOnlyList<string> row, int columnCount)
    {
        var values = row.Take(columnCount).ToList();
        if (row.Count > columnCount)
            values[columnCount - 1] = values[columnCount - 1] + " | " + string.Join(" | ", row.Skip(columnCount));
        while (values.Count < columnCount)
            values.Add("");
        return values;
    }

    internal static string RenderMarkdownLine(string line)
    {
        var doc = Markdown.Parse(line ?? "", ChatMarkdownPipeline);
        if (doc.Count == 0) return "";
        return doc[0] switch
        {
            HeadingBlock h => $"[bold]{RenderInlineMarkup(h.Inline)}[/]",
            ParagraphBlock p => RenderInlineMarkup(p.Inline),
            _ => RenderBlockSingleLine(doc[0]),
        };
    }

    static string RenderInlineMarkup(ContainerInline? container)
    {
        if (container is null) return "";
        var sb = new StringBuilder();
        for (Inline? current = container.FirstChild; current is not null; current = current.NextSibling)
            sb.Append(RenderInlineMarkup(current));
        return sb.ToString();
    }

    static string RenderInlineMarkup(Inline inline) =>
        inline switch
        {
            LiteralInline literal => RenderLiteralWithAutoLinks(literal.Content.ToString()),
            CodeInline code => $"[grey70]{Markup.Escape(code.Content)}[/]",
            EmphasisInline emphasis => emphasis.DelimiterCount >= 2
                ? $"[bold]{RenderInlineMarkup((ContainerInline)emphasis)}[/]"
                : $"[italic]{RenderInlineMarkup((ContainerInline)emphasis)}[/]",
            LinkInline link => RenderLinkInline(link),
            LineBreakInline => "\n",
            HtmlInline html => Markup.Escape(html.Tag),
            ContainerInline container => RenderInlineMarkup(container),
            _ => Markup.Escape(inline.ToString() ?? ""),
        };

    static string RenderLinkInline(LinkInline link)
    {
        string label = RenderInlineMarkup((ContainerInline)link);
        string url = link.GetDynamicUrl?.Invoke() ?? link.Url ?? "";
        if (TryTrimmedUrl(url, out var normalized))
        {
            if (label.Length == 0) return RenderHyperlink(normalized, normalized);
            string plainLabel = StripMarkupTags(label);
            string linkLabel = plainLabel.Length == 0 ? normalized : plainLabel;
            return RenderHyperlink(normalized, linkLabel);
        }
        return label.Length > 0 ? label : Markup.Escape(url);
    }

    static string RenderLiteralWithAutoLinks(string literal)
    {
        if (string.IsNullOrEmpty(literal)) return "";
        var sb = new StringBuilder();
        int cursor = 0;
        foreach (Match match in BareUrlRegex.Matches(literal))
        {
            if (!match.Success || match.Index < cursor) continue;
            sb.Append(Markup.Escape(literal[cursor..match.Index]));
            string candidate = match.Value;
            if (TryTrimmedUrl(candidate, out var url))
            {
                sb.Append(RenderHyperlink(url, url));
                if (url.Length < candidate.Length)
                    sb.Append(Markup.Escape(candidate[url.Length..]));
            }
            else
                sb.Append(Markup.Escape(candidate));
            cursor = match.Index + match.Length;
        }
        sb.Append(Markup.Escape(literal[cursor..]));
        return sb.ToString();
    }

    static bool TryTrimmedUrl(string value, out string url)
    {
        url = (value ?? "").Trim();
        if (!(url.StartsWith("http://", StringComparison.OrdinalIgnoreCase)
              || url.StartsWith("https://", StringComparison.OrdinalIgnoreCase)))
            return false;

        url = url.TrimEnd('.', ',', ';', '!', '?');
        return url.Length > 0;
    }

    static string RenderHyperlink(string url, string label) =>
        $"[link={Markup.Escape(url)}]{Markup.Escape(label)}[/]";
}
