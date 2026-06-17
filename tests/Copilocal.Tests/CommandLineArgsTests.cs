using Copilocal;
using Copilocal.Cli;
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
    [DataRow("--version")]
    [DataRow("-V")]
    public void Parse_VersionFlag_SetsShowVersionAndIsNotForwarded(string flag)
    {
        var cli = CommandLineArgs.Parse(new[] { flag });

        cli.ShowVersion.Should().BeTrue();
        cli.CopilotArgs.Should().BeEmpty();
    }

    [TestMethod]
    [DataRow("--help")]
    [DataRow("-h")]
    [DataRow("-?")]
    public void Parse_HelpFlag_SetsShowHelpAndIsNotForwarded(string flag)
    {
        var cli = CommandLineArgs.Parse(new[] { flag });

        cli.ShowHelp.Should().BeTrue();
        cli.CopilotArgs.Should().BeEmpty();
    }

    [TestMethod]
    public void Parse_HelpAfterSeparator_IsForwardedNotConsumed()
    {
        var cli = CommandLineArgs.Parse(new[] { "--", "--help" });

        cli.ShowHelp.Should().BeFalse();
        cli.CopilotArgs.Should().Equal("--help");
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

    [TestMethod]
    public void Parse_LonePickFlag_IsConsumedNotForwarded()
    {
        var cli = CommandLineArgs.Parse(new[] { "--pick" });

        cli.Pick.Should().Be(-1);
        cli.CopilotArgs.Should().NotContain("--pick");
    }

    [TestMethod]
    public void Parse_LoneNameFlag_IsConsumedNotForwarded()
    {
        var cli = CommandLineArgs.Parse(new[] { "--name" });

        cli.SessionName.Should().BeNull();
        cli.CopilotArgs.Should().NotContain("--name");
    }

    [TestMethod]
    public void Parse_OwnFlagAfterSeparator_IsForwardedNotConsumed()
    {
        // Everything after "--" goes to copilot verbatim, even if it matches a copilocal flag.
        var cli = CommandLineArgs.Parse(new[] { "--", "--dry-run", "--pick", "3" });

        cli.DryRun.Should().BeFalse();
        cli.Pick.Should().Be(-1);
        cli.Interactive.Should().BeTrue();
        cli.CopilotArgs.Should().Equal("--dry-run", "--pick", "3");
    }

    [TestMethod]
    public void Parse_OwnFlagsBeforeSeparator_AreConsumed_RestForwarded()
    {
        var cli = CommandLineArgs.Parse(new[] { "--dry-run", "--", "--offline" });

        cli.DryRun.Should().BeTrue();      // before "--" => copilocal's
        cli.Offline.Should().BeFalse();    // after "--"  => forwarded, not copilocal's
        cli.CopilotArgs.Should().Equal("--offline");
    }
}
