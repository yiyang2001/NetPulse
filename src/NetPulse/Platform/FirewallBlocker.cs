using System.Collections;
using System.Reflection;
using System.Runtime.InteropServices;
using NetPulse.Core;

namespace NetPulse.Platform;

/// <summary>
/// Windows Firewall through its COM API (HNetCfg.FwPolicy2), late-bound so no interop assembly is needed.
/// Reading works for everyone; adding or removing rules needs administrator rights.
/// </summary>
internal static class FirewallBlocker
{
    private const int ActionBlock = 0;
    private const int AllProfiles = 0x7FFFFFFF;
    private const int E_ACCESSDENIED = unchecked((int)0x80070005);

    /// <summary>False when the firewall is off for a network type in use: rules then have no effect.</summary>
    public static bool IsFirewallOnForCurrentNetwork()
    {
        var policy = CreatePolicy();
        try
        {
            var profiles = (int)Get(policy, "CurrentProfileTypes");
            foreach (var profile in new[] { 1, 2, 4 })
            {
                if ((profiles & profile) != 0 && !(bool)Get(policy, "FirewallEnabled", profile))
                {
                    return false;
                }
            }

            return true;
        }
        finally
        {
            Marshal.FinalReleaseComObject(policy);
        }
    }

    public static IReadOnlyList<BlockedProgram> List() => FirewallBlocks.Summarize(ReadOwnRules());

    /// <summary>Adds an outbound and an inbound block rule; existing NetPulse rules for the program are replaced.</summary>
    public static void Block(string path)
    {
        Unblock(path);
        Run(policy =>
        {
            var rules = Get(policy, "Rules");
            foreach (var direction in new[] { FirewallDirection.Outbound, FirewallDirection.Inbound })
            {
                var rule = Activator.CreateInstance(Type.GetTypeFromProgID("HNetCfg.FWRule", throwOnError: true)!)!;
                Set(rule, "Name", FirewallBlocks.RuleName(path, direction));
                Set(rule, "Description", "Created by NetPulse (Programs > Blocked programs). " + path);
                Set(rule, "ApplicationName", path);
                Set(rule, "Direction", (int)direction);
                Set(rule, "Action", ActionBlock);
                Set(rule, "Profiles", AllProfiles);
                Set(rule, "Grouping", FirewallBlocks.Group);
                Set(rule, "Enabled", true);
                Call(rules, "Add", rule);
                Marshal.FinalReleaseComObject(rule);
            }
        });
    }

    /// <summary>Removes every NetPulse rule for the program (rules of other software are never touched).</summary>
    public static void Unblock(string path)
    {
        var names = FirewallBlocks.RulesFor(ReadOwnRules(), path);
        if (names.Count == 0)
        {
            return;
        }

        Run(policy =>
        {
            var rules = Get(policy, "Rules");
            foreach (var name in names)
            {
                Call(rules, "Remove", name);
            }
        });
    }

    private static List<FirewallRuleInfo> ReadOwnRules()
    {
        var result = new List<FirewallRuleInfo>();
        Run(policy =>
        {
            foreach (var rule in (IEnumerable)Get(policy, "Rules"))
            {
                try
                {
                    // Only our group is read in full: there are hundreds of rules and each property is a COM call.
                    if (Get(rule, "Grouping") as string == FirewallBlocks.Group)
                    {
                        result.Add(new FirewallRuleInfo(
                            (string)Get(rule, "Name"),
                            FirewallBlocks.Group,
                            Get(rule, "ApplicationName") as string,
                            (FirewallDirection)(int)Get(rule, "Direction"),
                            (int)Get(rule, "Action") == ActionBlock,
                            (bool)Get(rule, "Enabled")));
                    }
                }
                finally
                {
                    Marshal.FinalReleaseComObject(rule);
                }
            }
        });
        return result;
    }

    private static void Run(Action<object> action)
    {
        var policy = CreatePolicy();
        try
        {
            action(policy);
        }
        catch (TargetInvocationException ex) when (ex.InnerException is COMException { HResult: E_ACCESSDENIED } or UnauthorizedAccessException)
        {
            throw new UnauthorizedAccessException(ex.InnerException.Message, ex.InnerException);
        }
        catch (TargetInvocationException ex) when (ex.InnerException is COMException inner)
        {
            System.Runtime.ExceptionServices.ExceptionDispatchInfo.Throw(inner);
        }
        finally
        {
            Marshal.FinalReleaseComObject(policy);
        }
    }

    private static object CreatePolicy() =>
        Activator.CreateInstance(Type.GetTypeFromProgID("HNetCfg.FwPolicy2", throwOnError: true)!)!;

    private static object Get(object target, string name, params object[] arguments) =>
        target.GetType().InvokeMember(name, BindingFlags.GetProperty, null, target, arguments)!;

    private static void Set(object target, string name, object value) =>
        target.GetType().InvokeMember(name, BindingFlags.SetProperty, null, target, [value]);

    private static void Call(object target, string name, params object[] arguments) =>
        target.GetType().InvokeMember(name, BindingFlags.InvokeMethod, null, target, arguments);
}
