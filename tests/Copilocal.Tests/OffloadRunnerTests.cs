using Copilocal;
using Copilocal.Configuration;
using Copilocal.Launch;
using Copilocal.Providers;
using Copilocal.Tests.Fakes;
using FluentAssertions;

namespace Copilocal.Tests;

[TestClass]
public sealed class OffloadRunnerTests
{
    [TestMethod]
    public void Run_OkReply_ReturnsAssistantContent()
    {
        var proc = new FakeProcessRunner();
        var http = new FakeHttpGateway();
        const string chatUrl = "http://localhost:11434/v1/chat/completions";
        http.AddPost(chatUrl, ok: true, status: 200,
            body: """{"choices":[{"message":{"content":"Ready to assist."}}]}"""); // warm-up
        http.AddPost(chatUrl, ok: true, status: 200,
            body: """{"choices":[{"message":{"content":"offloaded result"}}]}""");
        var providers = new ProviderHub(proc, http);
        var runner = new OffloadRunner(providers, http);
        var model = new MenuItem { Kind = MenuItemKind.Model, Provider = "Ollama", Model = "qwen2.5-coder:7b" };

        var result = runner.Run(model, "analyze this", new LaunchConfig());

        result.Ok.Should().BeTrue();
        result.Output.Should().Be("offloaded result");
    }

    [TestMethod]
    public void Run_WarmupFails_ReturnsError()
    {
        var proc = new FakeProcessRunner();
        var http = new FakeHttpGateway();
        const string chatUrl = "http://localhost:11434/v1/chat/completions";
        http.AddPost(chatUrl, ok: false, status: 500, body: """{"error":{"message":"boom"}}""");
        var providers = new ProviderHub(proc, http);
        var runner = new OffloadRunner(providers, http);
        var model = new MenuItem { Kind = MenuItemKind.Model, Provider = "Ollama", Model = "qwen2.5-coder:7b" };

        var result = runner.Run(model, "analyze this", new LaunchConfig());

        result.Ok.Should().BeFalse();
        result.Error.Should().Contain("Model warm-up failed");
    }

    [TestMethod]
    public void Run_RequestFails_ReturnsHttpDetail()
    {
        var proc = new FakeProcessRunner();
        var http = new FakeHttpGateway();
        const string chatUrl = "http://localhost:11434/v1/chat/completions";
        http.AddPost(chatUrl, ok: true, status: 200,
            body: """{"choices":[{"message":{"content":"Ready to assist."}}]}"""); // warm-up
        http.AddPost(chatUrl, ok: false, status: 429,
            body: """{"error":{"message":"rate limited"}}""");
        var providers = new ProviderHub(proc, http);
        var runner = new OffloadRunner(providers, http);
        var model = new MenuItem { Kind = MenuItemKind.Model, Provider = "Ollama", Model = "qwen2.5-coder:7b" };

        var result = runner.Run(model, "analyze this", new LaunchConfig());

        result.Ok.Should().BeFalse();
        result.Error.Should().Contain("HTTP 429");
    }
}
