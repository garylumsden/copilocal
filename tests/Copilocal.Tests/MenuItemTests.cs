using Copilocal;
using Copilocal.Providers;
using FluentAssertions;

namespace Copilocal.Tests;

[TestClass]
public sealed class MenuItemTests
{
    [TestMethod]
    public void Display_Header_ReturnsStyledProvider()
    {
        // Arrange
        var item = new MenuItem
        {
            Kind = MenuItemKind.Header,
            Provider = "Ollama",
        };

        // Act
        var result = item.Display;

        // Assert
        result.Should().Be("[teal bold]Ollama[/]");
    }

    [TestMethod]
    public void Display_Control_ReturnsModelLabel()
    {
        // Arrange
        var item = new MenuItem
        {
            Kind = MenuItemKind.Control,
            Model = "Configure launch options",
            ControlAction = ControlAction.Configure,
        };

        // Act
        var result = item.Display;

        // Assert
        result.Should().Be("Configure launch options");
    }

    [TestMethod]
    public void Display_ModelHelp_ReturnsModelHelpText()
    {
        // Arrange
        var item = new MenuItem
        {
            Kind = MenuItemKind.ModelHelp,
            Model = "No local models found",
        };

        // Act
        var result = item.Display;

        // Assert
        result.Should().Be("No local models found");
    }

    [TestMethod]
    public void Display_ModelWithTools_ReturnsProviderAndModel()
    {
        // Arrange
        var item = new MenuItem
        {
            Kind = MenuItemKind.Model,
            Provider = "Ollama",
            Model = "llama3.2",
            Tools = true,
        };

        // Act
        var result = item.Display;

        // Assert
        result.Should().Be("[dim]Ollama   [/] llama3.2");
    }

    [TestMethod]
    public void Display_ModelWithoutTools_AppendsNoToolCallingWarning()
    {
        // Arrange
        var item = new MenuItem
        {
            Kind = MenuItemKind.Model,
            Provider = "Foundry",
            Model = "phi-4",
            Tools = false,
        };

        // Act
        var result = item.Display;

        // Assert
        result.Should().Be("[dim]Foundry  [/] phi-4  [yellow](no tool-calling)[/]");
    }

    [TestMethod]
    public void Display_ModelNameWithMarkupBrackets_EscapesBrackets()
    {
        // Arrange
        var item = new MenuItem
        {
            Kind = MenuItemKind.Model,
            Provider = "LM Studio",
            Model = "qwen[tools]/model]",
            Tools = true,
        };

        // Act
        var result = item.Display;

        // Assert
        result.Should().Be("[dim]LM Studio[/] qwen[[tools]]/model]]");
    }

    [TestMethod]
    public void Display_LiteLlmModel_PrefixesWithPlatformName()
    {
        var item = new MenuItem
        {
            Kind = MenuItemKind.Model,
            Provider = "LiteLLM",
            Model = "ollama/qwen2.5-coder:7b",
            Tools = true,
        };

        item.Display.Should().Be("[dim]LiteLLM  [/] [[Ollama]] qwen2.5-coder:7b");
    }

    [TestMethod]
    public void Display_LiteLlmModel_FoundryPrefix_UsesFoundryLocalLabel()
    {
        var item = new MenuItem
        {
            Kind = MenuItemKind.Model,
            Provider = "LiteLLM",
            Model = "foundry/qwen2.5-coder-7b-instruct-openvino-npu",
            Tools = true,
        };

        item.Display.Should().Be("[dim]LiteLLM  [/] [[Foundry Local]] qwen2.5-coder-7b-instruct-openvino-npu");
    }

    [TestMethod]
    public void Display_LiteLlmModel_LmStudioPrefix_UsesLmStudioLabel()
    {
        var item = new MenuItem
        {
            Kind = MenuItemKind.Model,
            Provider = "LiteLLM",
            Model = "lmstudio/qwen2.5-coder-7b-instruct",
            Tools = true,
        };

        item.Display.Should().Be("[dim]LiteLLM  [/] [[LM Studio]] qwen2.5-coder-7b-instruct");
    }
}
