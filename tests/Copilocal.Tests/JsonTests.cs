using Copilocal;
using Copilocal.Infrastructure;
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

    [TestMethod]
    public void Escape_ControlCharacters_EscapesAsJsonEscapes()
    {
        // Arrange: a value with a newline, carriage return and tab would corrupt a JSON
        // document if passed through verbatim.
        const string value = "line1\nline2\r\tend";

        // Act
        var result = Json.Escape(value);

        // Assert
        result.Should().Be("line1\\nline2\\r\\tend");
    }

    [TestMethod]
    public void Escape_OtherControlChar_EscapesAsUnicodeSequence()
    {
        // Arrange: NUL has no short escape, so it must become a \u00XX sequence.
        const string value = "a\0b";

        // Act
        var result = Json.Escape(value);

        // Assert
        result.Should().Be("a\\u0000b");
    }
}
