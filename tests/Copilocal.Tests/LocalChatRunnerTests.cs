using System.Text.Json;

using Copilocal.Launch;
using FluentAssertions;

namespace Copilocal.Tests;

[TestClass]
public sealed class LocalChatRunnerTests
{
    [TestMethod]
    public void DefaultSystemPrompt_StatesLocalModelExperimentationIntent()
    {
        LocalChatRunner.DefaultSystemPrompt.Should().Contain("experimenting with local language models");
        LocalChatRunner.DefaultSystemPrompt.Should().Contain("does not execute tools or shell commands");
    }

    [TestMethod]
    public void BuildChatPayload_EncodesModelAndMessages()
    {
        var messages = new List<ChatMessage>
        {
            new("system", "You are a helper."),
            new("user", "Hello \"world\""),
        };

        string payload = ChatProtocol.BuildChatPayload("qwen/test", messages);
        using var doc = JsonDocument.Parse(payload);
        var root = doc.RootElement;

        root.GetProperty("model").GetString().Should().Be("qwen/test");
        root.GetProperty("stream").GetBoolean().Should().BeFalse();
        var encoded = root.GetProperty("messages");
        encoded.GetArrayLength().Should().Be(2);
        encoded[0].GetProperty("role").GetString().Should().Be("system");
        encoded[1].GetProperty("content").GetString().Should().Be("Hello \"world\"");
    }

    [TestMethod]
    public void ParseAssistantReply_StringContent_ReturnsOk()
    {
        var body = """
            {"choices":[{"message":{"content":"Ready to code."}}]}
            """;

        var result = ChatProtocol.ParseAssistantReply(body);

        result.Status.Should().Be(ReplyStatus.Ok);
        result.Content.Should().Be("Ready to code.");
    }

    [TestMethod]
    public void ParseAssistantReply_ArrayTextContent_ReturnsJoinedText()
    {
        var body = """
            {"choices":[{"message":{"content":[{"type":"text","text":"Line one."},{"type":"text","text":"Line two."}]}}]}
            """;

        var result = ChatProtocol.ParseAssistantReply(body);

        result.Status.Should().Be(ReplyStatus.Ok);
        result.Content.Should().Be("Line one.\nLine two.");
    }

    [TestMethod]
    public void ParseAssistantReply_ReasoningWithoutContent_ReturnsReasoningOnly()
    {
        var body = """
            {"choices":[{"message":{"content":"","reasoning":"thinking..."}}]}
            """;

        var result = ChatProtocol.ParseAssistantReply(body);

        result.Status.Should().Be(ReplyStatus.ReasoningOnly);
        result.Detail.Should().Contain("thinking");
    }

    [TestMethod]
    public void ParseAssistantReply_ToolCallsWithoutContent_ReturnsToolCallOnly()
    {
        var body = """
            {"choices":[{"message":{"tool_calls":[{"function":{"name":"list_files"}}]}}]}
            """;

        var result = ChatProtocol.ParseAssistantReply(body);

        result.Status.Should().Be(ReplyStatus.ToolCallOnly);
        result.Detail.Should().Contain("list_files");
    }

    [TestMethod]
    public void ParseAssistantReply_WrongShapeOrMalformed_ReturnsInvalid()
    {
        ChatProtocol.ParseAssistantReply("""{"choices":[]}""").Status
            .Should().Be(ReplyStatus.Invalid);
        ChatProtocol.ParseAssistantReply("{ not json").Status
            .Should().Be(ReplyStatus.Invalid);
    }

    [TestMethod]
    public void ParseUsage_WithAllTokenFields_ReturnsUsage()
    {
        var usage = ChatProtocol.ParseUsage("""{"usage":{"prompt_tokens":120,"completion_tokens":45,"total_tokens":165}}""");

        usage.Should().NotBeNull();
        usage!.PromptTokens.Should().Be(120);
        usage.CompletionTokens.Should().Be(45);
        usage.TotalTokens.Should().Be(165);
    }

    [TestMethod]
    public void ParseUsage_WithoutTotal_ComputesTotal()
    {
        var usage = ChatProtocol.ParseUsage("""{"usage":{"prompt_tokens":10,"completion_tokens":15}}""");

        usage.Should().NotBeNull();
        usage!.TotalTokens.Should().Be(25);
    }

    [TestMethod]
    public void ParseUsage_MissingOrMalformedUsage_ReturnsNull()
    {
        ChatProtocol.ParseUsage("""{"choices":[{"message":{"content":"ok"}}]}""").Should().BeNull();
        ChatProtocol.ParseUsage("""{"usage":"oops"}""").Should().BeNull();
        ChatProtocol.ParseUsage("{ not json").Should().BeNull();
    }

    [TestMethod]
    public void BuildTokenUsageLine_FormatsWithAndWithoutLastUsage()
    {
        TokenUsageTracker.BuildTokenUsageLine("qwen2.5-coder:7b", 100, 25, 125, null)
            .Should().Contain("m:qwen2.5-coder:7b")
            .And.Contain("last:n/a");

        TokenUsageTracker.BuildTokenUsageLine("qwen2.5-coder:7b", 100, 25, 125, new TokenUsage(10, 5, 15))
            .Should().Contain("last:15 (p10/c5)");
    }

    [TestMethod]
    public void BuildTokenUsageRenderText_ReservesFinalConsoleColumn()
    {
        var rendered = TokenUsageTracker.BuildTokenUsageRenderText(new string('x', 80), 40);

        rendered.Text.Should().HaveLength(38);
        rendered.Left.Should().Be(1);
        (rendered.Left + rendered.Text.Length - 1).Should().BeLessThanOrEqualTo(38);
    }

    [TestMethod]
    public void AutocompleteChatCommand_UniquePrefix_ResolvesCommand()
    {
        var result = ChatCommandRouter.AutocompleteChatCommand("/h");

        result.Kind.Should().Be(ChatCommandRouter.CommandAutocompleteKind.Resolved);
        result.Command.Should().Be("/help");
    }

    [TestMethod]
    public void AutocompleteChatCommand_SlashOnly_ReturnsCommandChoices()
    {
        var result = ChatCommandRouter.AutocompleteChatCommand("/");

        result.Kind.Should().Be(ChatCommandRouter.CommandAutocompleteKind.Ambiguous);
        result.Matches.Should().Contain(new[] { "/help", "/clear", "/multi", "/exit", "/quit" });
    }

    [TestMethod]
    public void AutocompleteChatCommand_QuitAlias_NormalizesToExit()
    {
        var result = ChatCommandRouter.AutocompleteChatCommand("/quit");

        result.Kind.Should().Be(ChatCommandRouter.CommandAutocompleteKind.Resolved);
        result.Command.Should().Be("/exit");
    }

    [TestMethod]
    public void AutocompleteChatCommand_UnknownSlash_ReturnsUnknown()
    {
        var result = ChatCommandRouter.AutocompleteChatCommand("/doesnotexist");

        result.Kind.Should().Be(ChatCommandRouter.CommandAutocompleteKind.Unknown);
    }

    [TestMethod]
    public void TryExtractFirstMarkdownTable_WithPipeTable_ReturnsParsedTable()
    {
        const string content = """
            Intro line

            | Model | Tokens |
            | --- | --- |
            | qwen2.5-coder:7b | 32768 |
            | phi-4-mini | 16384 |

            Outro line
            """;

        bool ok = ChatMarkdownRenderer.TryExtractFirstMarkdownTable(content, out var table);

        ok.Should().BeTrue();
        table.Headers.Should().Equal("Model", "Tokens");
        table.Rows.Should().HaveCount(2);
    }

    [TestMethod]
    public void TryExtractFirstMarkdownTable_PreservesBracketedCellText()
    {
        const string content = """
            | Type | Access | Maybe |
            | --- | --- | --- |
            | List[int] | arr[0] | Optional[str] |
            """;

        bool ok = ChatMarkdownRenderer.TryExtractFirstMarkdownTable(content, out var table);

        ok.Should().BeTrue();
        table.Rows.Should().ContainSingle()
            .Which.Should().Equal("List[int]", "arr[0]", "Optional[str]");
    }

    [TestMethod]
    public void TryExtractFirstMarkdownTable_WithNoDataRows_ReturnsFalse()
    {
        const string markdown = """
            | A | B |
            | --- | --- |
            """;

        bool ok = ChatMarkdownRenderer.TryExtractFirstMarkdownTable(markdown, out _);

        ok.Should().BeFalse();
    }

    [TestMethod]
    public void RenderMarkdownLine_RendersBoldCodeAndMarkdownLink()
    {
        string rendered = ChatMarkdownRenderer.RenderMarkdownLine("Use **bold** and `code` and [docs](https://example.com).");

        rendered.Should().Contain("[bold]bold[/]");
        rendered.Should().Contain("[grey70]code[/]");
        rendered.Should().Contain("[link=https://example.com]docs[/]");
    }

    [TestMethod]
    public void RenderMarkdownLine_PreservesBracketsInLinkLabel()
    {
        string rendered = ChatMarkdownRenderer.RenderMarkdownLine("[a[b]](https://x)");

        rendered.Should().Contain("[link=https://x]a[[b]][/]");
    }

    [TestMethod]
    public void RenderMarkdownLine_HeadingAndBareUrl_AreRendered()
    {
        string heading = ChatMarkdownRenderer.RenderMarkdownLine("## Quick start");
        string url = ChatMarkdownRenderer.RenderMarkdownLine("See https://example.com/docs.");

        heading.Should().Contain("[bold]Quick start[/]");
        url.Should().Contain("[link=https://example.com/docs]https://example.com/docs[/]");
        url.Should().EndWith(".");
    }
}
