using System.Xml.Linq;

namespace NetPulse.Core;

/// <summary>Task Scheduler definition that starts a program elevated when one user signs in.</summary>
public static class LogonTask
{
    public static readonly XNamespace Namespace = "http://schemas.microsoft.com/windows/2004/02/mit/task";

    /// <summary>XML for <c>schtasks /Create /XML</c>; write it as UTF-16, which the declaration states.</summary>
    public static string Build(string userSid, string command, string? arguments, string description)
    {
        var ns = Namespace;
        var task = new XElement(
            ns + "Task",
            new XAttribute("version", "1.2"),
            new XElement(ns + "RegistrationInfo", new XElement(ns + "Description", description)),
            new XElement(
                ns + "Triggers",
                new XElement(
                    ns + "LogonTrigger",
                    new XElement(ns + "Enabled", "true"),
                    new XElement(ns + "UserId", userSid))),
            new XElement(
                ns + "Principals",
                new XElement(
                    ns + "Principal",
                    new XAttribute("id", "Author"),
                    new XElement(ns + "UserId", userSid),
                    new XElement(ns + "LogonType", "InteractiveToken"),
                    new XElement(ns + "RunLevel", "HighestAvailable"))),
            new XElement(
                ns + "Settings",
                new XElement(ns + "MultipleInstancesPolicy", "IgnoreNew"),
                new XElement(ns + "DisallowStartIfOnBatteries", "false"),
                new XElement(ns + "StopIfGoingOnBatteries", "false"),
                new XElement(ns + "AllowHardTerminate", "true"),
                new XElement(ns + "StartWhenAvailable", "false"),
                new XElement(ns + "AllowStartOnDemand", "true"),
                new XElement(ns + "Enabled", "true"),
                new XElement(ns + "ExecutionTimeLimit", "PT0S"), // no limit; the default stops it after 3 days
                new XElement(ns + "Priority", "4")), // normal; the default 7 runs it at below-normal priority
            new XElement(
                ns + "Actions",
                new XAttribute("Context", "Author"),
                new XElement(
                    ns + "Exec",
                    new XElement(ns + "Command", command),
                    string.IsNullOrWhiteSpace(arguments) ? null : new XElement(ns + "Arguments", arguments))));
        return new XDeclaration("1.0", "UTF-16", null) + Environment.NewLine + task;
    }
}
