using NetPulse.Core;
using Xunit;

namespace NetPulse.Core.Tests;

public sealed class FirewallBlockTests
{
    private const string Windows = @"C:\Windows";
    private const string Own = @"C:\Users\me\AppData\Local\Programs\NetPulse\NetPulse.exe";

    [Theory]
    [InlineData(@"C:\Program Files\App\app.exe", BlockCheck.Ok)]
    [InlineData(@"D:\Games\game.EXE", BlockCheck.Ok)]
    [InlineData(@"C:\Windows\System32\curl.exe", BlockCheck.WindowsComponent)]
    [InlineData(@"c:\windows\SystemApps\x\SearchHost.exe", BlockCheck.WindowsComponent)]
    [InlineData(@"C:\WindowsApps\app.exe", BlockCheck.Ok)] // a sibling folder is not inside C:\Windows
    [InlineData(@"C:\Windows\System32\svchost.exe", BlockCheck.Protected)]
    [InlineData(@"C:\Windows\System32\LSASS.EXE", BlockCheck.Protected)]
    [InlineData(@"C:\Windows\explorer.exe", BlockCheck.Protected)]
    [InlineData(Own, BlockCheck.Protected)]
    [InlineData(@"C:\Users\me\AppData\Local\Programs\NetPulse\..\NetPulse\NetPulse.exe", BlockCheck.Protected)]
    [InlineData(@"app.exe", BlockCheck.NotAnExecutable)]
    [InlineData(@"C:\tools\script.bat", BlockCheck.NotAnExecutable)]
    [InlineData("", BlockCheck.NotAnExecutable)]
    [InlineData(null, BlockCheck.NotAnExecutable)]
    public void ChecksWhatMayBeBlocked(string? path, BlockCheck expected) =>
        Assert.Equal(expected, FirewallBlocks.Check(path, Windows, Own));

    [Fact]
    public void RuleNamesAreReadableAndUniquePerPathAndDirection()
    {
        var outbound = FirewallBlocks.RuleName(@"C:\A\app.exe", FirewallDirection.Outbound);
        var inbound = FirewallBlocks.RuleName(@"C:\A\app.exe", FirewallDirection.Inbound);
        var other = FirewallBlocks.RuleName(@"C:\B\app.exe", FirewallDirection.Outbound);
        Assert.StartsWith("NetPulse: block app.exe (outbound) #", outbound);
        Assert.StartsWith("NetPulse: block app.exe (inbound) #", inbound);
        Assert.NotEqual(outbound, other);
        // Paths are case-insensitive: the same program gets the same hash whatever its spelling.
        Assert.Equal(outbound[^9..], FirewallBlocks.RuleName(@"c:\a\APP.exe", FirewallDirection.Outbound)[^9..]);
    }

    [Fact]
    public void SummarizesOnlyOwnBlockRules()
    {
        FirewallRuleInfo[] rules =
        [
            new("NetPulse: block a.exe (outbound) #1", "NetPulse", @"C:\A\a.exe", FirewallDirection.Outbound, true, true),
            new("NetPulse: block a.exe (inbound) #1", "NetPulse", @"c:\a\A.EXE", FirewallDirection.Inbound, true, true),
            new("NetPulse: block b.exe (outbound) #2", "NetPulse", @"C:\B\b.exe", FirewallDirection.Outbound, true, false),
            new("Someone else's rule", "Other", @"C:\C\c.exe", FirewallDirection.Outbound, true, true),
            new("NetPulse allow?", "NetPulse", @"C:\D\d.exe", FirewallDirection.Outbound, false, true),
            new("NetPulse no app", "NetPulse", null, FirewallDirection.Outbound, true, true),
        ];

        var programs = FirewallBlocks.Summarize(rules);
        Assert.Equal(2, programs.Count);
        Assert.Equal(new BlockedProgram(@"C:\A\a.exe", Outbound: true, Inbound: true, Enabled: true), programs[0]);
        Assert.Equal("b.exe", programs[1].FileName);
        Assert.False(programs[1].Inbound);
        Assert.False(programs[1].Enabled);
    }

    [Fact]
    public void FindsRulesToRemoveWithoutTouchingOthers()
    {
        FirewallRuleInfo[] rules =
        [
            new("old name", "NetPulse", @"C:\A\a.exe", FirewallDirection.Outbound, true, true),
            new("NetPulse: block a.exe (inbound) #1", "NetPulse", @"C:\a\a.exe", FirewallDirection.Inbound, true, true),
            new("Vendor rule", "Vendor", @"C:\A\a.exe", FirewallDirection.Outbound, true, true),
            new("NetPulse: block b.exe (outbound) #2", "NetPulse", @"C:\B\b.exe", FirewallDirection.Outbound, true, true),
        ];

        Assert.Equal(["old name", "NetPulse: block a.exe (inbound) #1"], FirewallBlocks.RulesFor(rules, @"C:\A\a.exe"));
        Assert.Empty(FirewallBlocks.RulesFor(rules, @"C:\Z\z.exe"));
    }
}
