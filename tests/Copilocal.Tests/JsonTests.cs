using Copilocal;
using FluentAssertions;

namespace Copilocal.Tests;

[TestClass]
public sealed class JsonTests
{
    [TestMethod]
    public void Escape_Backslashes_EscapesBackslashes()
    {
        // Arrange
        const string value = @"C:\models\phi";

        // Act
        var result = Json.Escape(value);

        // Assert
        result.Should().Be(@"C:\\models\\phi");
    }

    [TestMethod]
    public void Escape_DoubleQuotes_EscapesDoubleQuotes()
    {
        // Arrange
        const string value = "say \"hello\"";

        // Act
        var result = Json.Escape(value);

        // Assert
        result.Should().Be("say \\\"hello\\\"");
    }

    [TestMethod]
    public void Escape_PlainString_ReturnsOriginalString()
    {
        // Arrange
        const string value = "plain local model";

        // Act
        var result = Json.Escape(value);

        // Assert
        result.Should().Be(value);
    }

    [TestMethod]
    public void Escape_EmptyString_ReturnsEmptyString()
    {
        // Arrange
        const string value = "";

        // Act
        var result = Json.Escape(value);

        // Assert
        result.Should().BeEmpty();
    }
}
