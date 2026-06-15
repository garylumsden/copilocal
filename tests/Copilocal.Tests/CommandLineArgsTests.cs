using Copilocal;
using FluentAssertions;

namespace Copilocal.Tests;

[TestClass]
public sealed class CommandLineArgsTests
{
    [TestMethod]
    public void Parse_NoArgs_DefaultsToInteractiveManagedSession()
    {
        var cli = CommandLineArgs.Parse(Array.Empty<string>());

        cli.DryRun.Should().BeFalse();
        cli.Offline.Should().BeFalse();
        cli.SessionName.Should().BeNull();
        cli.Pick.Should().Be(-1);
        cli.Interactive.Should().BeTrue();
        cli.UserManagedSession.Should().BeFalse();
        cli.WantsManagedSession.Should().BeTrue();
        cli.CopilotArgs.Should().BeEmpty();
    }

    [TestMethod]
    public void Parse_ExtractsOwnFlagsAndForwardsRemainder()
    {
        var cli = CommandLineArgs.Parse(new[] { "--dry-run", "--offline", "--name", "my feature", "--pick", "2", "--", "--banner" });

        cli.DryRun.Should().BeTrue();
        cli.Offline.Should().BeTrue();
        cli.SessionName.Should().Be("my feature");
        cli.Pick.Should().Be(2);
        cli.Interactive.Should().BeFalse();
        cli.CopilotArgs.Should().Equal("--banner");
    }

    [TestMethod]
    public void Parse_DryRun_DoesNotWantManagedSession()
    {
        var cli = CommandLineArgs.Parse(new[] { "--dry-run" });

        cli.Interactive.Should().BeTrue();
        cli.WantsManagedSession.Should().BeFalse();
    }

    [TestMethod]
    [DataRow("--resume")]
    [DataRow("--continue")]
    [DataRow("-r")]
    [DataRow("--session-id=abc")]
    public void Parse_UserDrivenSessionFlags_DisablesManagedSession(string forwarded)
    {
        var cli = CommandLineArgs.Parse(new[] { "--", forwarded });

        cli.UserManagedSession.Should().BeTrue();
        cli.WantsManagedSession.Should().BeFalse();
    }

    [TestMethod]
    public void Parse_LeadingDoubleDash_IsStrippedFromForwardedArgs()
    {
        var cli = CommandLineArgs.Parse(new[] { "--", "--plan", "extra" });

        cli.CopilotArgs.Should().Equal("--plan", "extra");
    }

    [TestMethod]
    public void Parse_NonNumericPick_TreatedAsInteractive()
    {
        var cli = CommandLineArgs.Parse(new[] { "--pick", "notanumber" });

        cli.Pick.Should().Be(0);
        cli.Interactive.Should().BeTrue();
    }
}
