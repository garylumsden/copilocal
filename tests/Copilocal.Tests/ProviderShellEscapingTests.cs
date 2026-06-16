using System.Text;

using Copilocal.Providers;
using FluentAssertions;

namespace Copilocal.Tests;

[TestClass]
public sealed class ProviderShellEscapingTests
{
    private const string DangerousValue = "sk-\"quote'apos`tick$dollar\r\nnext";

    [TestMethod]
    public void PsSingleQuoted_EscapesOuterQuotesAndRoundTripsValue()
    {
        string quoted = ProviderInstaller.PsSingleQuoted(DangerousValue);

        quoted.Should().StartWith("'");
        quoted.Should().EndWith("'");
        AssertNoUnescapedDoubleQuote(quoted);
        RecoverPowerShellSingleQuotedAfterOuterParse(quoted).Should().Be(DangerousValue);
    }

    [TestMethod]
    public void ShArg_EscapesOuterQuotesAndRoundTripsValue()
    {
        string quoted = ProviderInstaller.ShArg(DangerousValue);

        quoted.Should().StartWith("'");
        quoted.Should().EndWith("'");
        AssertNoUnescapedDoubleQuote(quoted);
        RecoverShellArgAfterOuterParse(quoted).Should().Be(DangerousValue);
    }

    [TestMethod]
    public void PsArrayLiteral_EscapesEveryElementForOuterCommandQuote()
    {
        string array = ProviderInstaller.PsArrayLiteral([DangerousValue, "C:\\path\"with'quotes"]);

        array.Should().StartWith("@(");
        array.Should().EndWith(")");
        AssertNoUnescapedDoubleQuote(array);
    }

    private static void AssertNoUnescapedDoubleQuote(string value)
    {
        for (int i = 0; i < value.Length; i++)
        {
            if (value[i] != '"') continue;
            (i > 0 && value[i - 1] == '\\').Should().BeTrue($"quote at index {i} must be escaped");
        }
    }

    private static string RecoverPowerShellSingleQuotedAfterOuterParse(string quoted)
    {
        string parsedByOuterQuote = quoted.Replace("\\\"", "\"", StringComparison.Ordinal);
        parsedByOuterQuote.Should().StartWith("'");
        parsedByOuterQuote.Should().EndWith("'");
        return parsedByOuterQuote[1..^1].Replace("''", "'", StringComparison.Ordinal);
    }

    private static string RecoverShellArgAfterOuterParse(string quoted)
    {
        string parsedByOuterQuote = quoted.Replace("\\\"", "\"", StringComparison.Ordinal);
        var sb = new StringBuilder();
        int i = 0;
        while (i < parsedByOuterQuote.Length)
        {
            if (parsedByOuterQuote[i] == '\'')
            {
                i++;
                while (i < parsedByOuterQuote.Length && parsedByOuterQuote[i] != '\'')
                    sb.Append(parsedByOuterQuote[i++]);
                i.Should().BeLessThan(parsedByOuterQuote.Length, "single-quoted shell segment must close");
                i++;
                continue;
            }

            if (parsedByOuterQuote[i] == '\\' && i + 1 < parsedByOuterQuote.Length)
            {
                sb.Append(parsedByOuterQuote[i + 1]);
                i += 2;
                continue;
            }

            throw new InvalidOperationException($"unexpected shell token at index {i}");
        }

        return sb.ToString();
    }
}
