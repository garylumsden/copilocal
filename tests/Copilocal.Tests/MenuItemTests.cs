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
}
