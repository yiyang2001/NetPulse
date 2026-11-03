using System.Security.Cryptography;
using System.Text;

namespace NetPulse.Core;

public enum FirewallDirection
{
    Inbound = 1,
    Outbound = 2,
}

/// <summary>One Windows Firewall rule, as far as NetPulse cares.</summary>
public sealed record FirewallRuleInfo(string Name, string? Grouping, string? ApplicationPath, FirewallDirection Direction, bool IsBlock, bool Enabled);

/// <summary>A program NetPulse blocks, with the directions its rules cover.</summary>
public sealed record BlockedProgram(string Path, bool Outbound, bool Inbound, bool Enabled)
{
    public string FileName => System.IO.Path.GetFileName(Path);
}

public enum BlockCheck
{
    Ok,

    /// <summary>Allowed, but a part of Windows: blocking it may break updates, search or the like.</summary>
    WindowsComponent,

    NotAnExecutable,

    /// <summary>Never blocked: system processes whose loss breaks networking or Windows itself, and NetPulse.</summary>
    Protected,
}

/// <summary>
/// "Block this program from the network" via Windows Firewall rules: one outbound and one inbound block
/// rule per program, all in the group "NetPulse" so NetPulse only ever lists or deletes its own rules.
/// </summary>
public static class FirewallBlocks
{
    public const string Group = "NetPulse";

    private static readonly HashSet<string> ProtectedNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "svchost.exe", "lsass.exe", "services.exe", "wininit.exe", "winlogon.exe", "csrss.exe", "smss.exe",
        "System", "Registry", "dwm.exe", "explorer.exe", "spoolsv.exe", "MsMpEng.exe",
    };

    /// <summary>
    /// Unique per program and direction (rules are removed by name, and two programs may share a file
    /// name), readable in the Windows Firewall console: "NetPulse: block chrome.exe (outbound) #1a2b3c4d".
    /// </summary>
    public static string RuleName(string path, FirewallDirection direction) =>
        $"NetPulse: block {Path.GetFileName(path)} ({(direction == FirewallDirection.Outbound ? "outbound" : "inbound")}) #{PathHash(path)}";

    public static BlockCheck Check(string? path, string windowsDirectory, string ownExe)
    {
        if (string.IsNullOrWhiteSpace(path) || !Path.IsPathFullyQualified(path) ||
            !path.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) || path.IndexOfAny(Path.GetInvalidPathChars()) >= 0)
        {
            return BlockCheck.NotAnExecutable;
        }

        if (ProtectedNames.Contains(Path.GetFileName(path)) || SamePath(path, ownExe))
        {
            return BlockCheck.Protected;
        }

        var windows = Path.TrimEndingDirectorySeparator(Path.GetFullPath(windowsDirectory)) + Path.DirectorySeparatorChar;
        return Path.GetFullPath(path).StartsWith(windows, StringComparison.OrdinalIgnoreCase) ? BlockCheck.WindowsComponent : BlockCheck.Ok;
    }

    /// <summary>NetPulse's own block rules, one entry per program; other rules are ignored.</summary>
    public static IReadOnlyList<BlockedProgram> Summarize(IEnumerable<FirewallRuleInfo> rules) =>
        rules
            .Where(r => r.IsBlock && string.Equals(r.Grouping, Group, StringComparison.Ordinal) && !string.IsNullOrEmpty(r.ApplicationPath))
            .GroupBy(r => r.ApplicationPath!, StringComparer.OrdinalIgnoreCase)
            .Select(g => new BlockedProgram(
                g.First().ApplicationPath!,
                g.Any(r => r.Direction == FirewallDirection.Outbound),
                g.Any(r => r.Direction == FirewallDirection.Inbound),
                g.All(r => r.Enabled)))
            .OrderBy(p => p.FileName, StringComparer.CurrentCultureIgnoreCase)
            .ToList();

    /// <summary>Names of NetPulse's rules for <paramref name="path"/>, whatever they were called when created.</summary>
    public static IReadOnlyList<string> RulesFor(IEnumerable<FirewallRuleInfo> rules, string path) =>
        rules
            .Where(r => string.Equals(r.Grouping, Group, StringComparison.Ordinal) && r.ApplicationPath is { } app && SamePath(app, path))
            .Select(r => r.Name)
            .Distinct(StringComparer.Ordinal)
            .ToList();

    public static bool SamePath(string a, string b)
    {
        try
        {
            return string.Equals(Path.GetFullPath(a), Path.GetFullPath(b), StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return false;
        }
    }

    private static string PathHash(string path) =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(Path.GetFullPath(path).ToUpperInvariant())))[..8];
}
