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
        var messages = new List<LocalChatRunner.ChatMessage>
        {
            new("system", "You are a helper."),
            new("user", "Hello \"world\""),
        };

        string payload = LocalChatRunner.BuildChatPayload("qwen/test", messages);
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

        var result = LocalChatRunner.ParseAssistantReply(body);

        result.Status.Should().Be(LocalChatRunner.ReplyStatus.Ok);
        result.Content.Should().Be("Ready to code.");
    }

    [TestMethod]
    public void ParseAssistantReply_ArrayTextContent_ReturnsJoinedText()
    {
        var body = """
            {"choices":[{"message":{"content":[{"type":"text","text":"Line one."},{"type":"text","text":"Line two."}]}}]}
            """;

        var result = LocalChatRunner.ParseAssistantReply(body);

        result.Status.Should().Be(LocalChatRunner.ReplyStatus.Ok);
        result.Content.Should().Be("Line one.\nLine two.");
    }

    [TestMethod]
    public void ParseAssistantReply_ReasoningWithoutContent_ReturnsReasoningOnly()
    {
        var body = """
            {"choices":[{"message":{"content":"","reasoning":"thinking..."}}]}
            """;

        var result = LocalChatRunner.ParseAssistantReply(body);

        result.Status.Should().Be(LocalChatRunner.ReplyStatus.ReasoningOnly);
        result.Detail.Should().Contain("thinking");
    }

    [TestMethod]
    public void ParseAssistantReply_ToolCallsWithoutContent_ReturnsToolCallOnly()
    {
        var body = """
            {"choices":[{"message":{"tool_calls":[{"function":{"name":"list_files"}}]}}]}
            """;

        var result = LocalChatRunner.ParseAssistantReply(body);

        result.Status.Should().Be(LocalChatRunner.ReplyStatus.ToolCallOnly);
        result.Detail.Should().Contain("list_files");
    }

    [TestMethod]
    public void ParseAssistantReply_WrongShapeOrMalformed_ReturnsInvalid()
    {
        LocalChatRunner.ParseAssistantReply("""{"choices":[]}""").Status
            .Should().Be(LocalChatRunner.ReplyStatus.Invalid);
        LocalChatRunner.ParseAssistantReply("{ not json").Status
            .Should().Be(LocalChatRunner.ReplyStatus.Invalid);
    }
}
