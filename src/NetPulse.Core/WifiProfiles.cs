using System.Text;
using System.Xml.Linq;
using static NetPulse.Core.Lang;

namespace NetPulse.Core;

/// <summary>A saved Wi-Fi profile as described by its WLAN profile XML.</summary>
/// <param name="Key">Plain-text key, or null if the profile has none or Windows returned it encrypted.</param>
/// <param name="KeyProtected">True when the profile has a key but Windows only returned it encrypted.</param>
public sealed record WifiProfileInfo(
    string Name,
    string Ssid,
    string Authentication,
    string Encryption,
    bool Hidden,
    bool AutoConnect,
    bool KeyProtected,
    string? Key)
{
    public bool IsOpen => Authentication.Equals("open", StringComparison.OrdinalIgnoreCase)
        && Encryption.Equals("none", StringComparison.OrdinalIgnoreCase);

    public bool IsEnterprise => !IsOpen && !Authentication.EndsWith("PSK", StringComparison.OrdinalIgnoreCase)
        && !Authentication.Contains("SAE", StringComparison.OrdinalIgnoreCase)
        && !Authentication.Equals("shared", StringComparison.OrdinalIgnoreCase)
        && !Encryption.Equals("WEP", StringComparison.OrdinalIgnoreCase);

    /// <summary>Friendly security name for display.</summary>
    public string SecurityLabel => Authentication.ToUpperInvariant() switch
    {
        "OPEN" when Encryption.Equals("WEP", StringComparison.OrdinalIgnoreCase) => "WEP",
        "OPEN" => T("开放", "Open"),
        "SHARED" => "WEP",
        "WPAPSK" => T("WPA-个人", "WPA-Personal"),
        "WPA2PSK" => T("WPA2-个人", "WPA2-Personal"),
        "WPA3SAE" => T("WPA3-个人", "WPA3-Personal"),
        "WPA3" or "WPA3ENT" or "WPA3ENT192" => T("WPA3-企业", "WPA3-Enterprise"),
        "WPA2" => T("WPA2-企业", "WPA2-Enterprise"),
        "WPA" => T("WPA-企业", "WPA-Enterprise"),
        "OWE" => T("增强开放 (OWE)", "Enhanced Open (OWE)"),
        _ => Authentication,
    };
}

public static class WifiProfileParser
{
    private static readonly XNamespace Ns = "http://www.microsoft.com/networking/WLAN/profile/v1";

    public static WifiProfileInfo Parse(string xml)
    {
        var root = XDocument.Parse(xml).Root ?? throw new FormatException("Empty profile XML.");
        var name = (string?)root.Element(Ns + "name") ?? string.Empty;
        var ssidConfig = root.Element(Ns + "SSIDConfig");
        var ssidElement = ssidConfig?.Element(Ns + "SSID");
        var ssid = (string?)ssidElement?.Element(Ns + "name") ?? DecodeHex((string?)ssidElement?.Element(Ns + "hex")) ?? name;
        var hidden = string.Equals((string?)ssidConfig?.Element(Ns + "nonBroadcast"), "true", StringComparison.OrdinalIgnoreCase);
        var autoConnect = !string.Equals((string?)root.Element(Ns + "connectionMode"), "manual", StringComparison.OrdinalIgnoreCase);

        var security = root.Element(Ns + "MSM")?.Element(Ns + "security");
        var authEncryption = security?.Element(Ns + "authEncryption");
        var authentication = (string?)authEncryption?.Element(Ns + "authentication") ?? "open";
        var encryption = (string?)authEncryption?.Element(Ns + "encryption") ?? "none";

        var sharedKey = security?.Element(Ns + "sharedKey");
        var keyProtected = string.Equals((string?)sharedKey?.Element(Ns + "protected"), "true", StringComparison.OrdinalIgnoreCase);
        var keyMaterial = (string?)sharedKey?.Element(Ns + "keyMaterial");

        return new WifiProfileInfo(
            name,
            ssid,
            authentication,
            encryption,
            hidden,
            autoConnect,
            KeyProtected: keyMaterial is not null && keyProtected,
            Key: keyMaterial is not null && !keyProtected ? keyMaterial : null);
    }

    private static string? DecodeHex(string? hex)
    {
        if (string.IsNullOrEmpty(hex) || hex.Length % 2 != 0)
        {
            return null;
        }

        try
        {
            return Encoding.UTF8.GetString(Convert.FromHexString(hex));
        }
        catch (FormatException)
        {
            return null;
        }
    }
}

public static class WifiQrPayload
{
    /// <summary>
    /// Builds the de-facto standard "WIFI:" URI understood by Android and iOS camera apps:
    /// <c>WIFI:T:WPA;S:&lt;ssid&gt;;P:&lt;password&gt;;H:true;;</c>. Special characters are backslash-escaped.
    /// </summary>
    public static string Build(WifiProfileInfo profile, string? password)
    {
        var type = profile.IsOpen ? "nopass"
            : profile.Encryption.Equals("WEP", StringComparison.OrdinalIgnoreCase) || profile.Authentication.Equals("shared", StringComparison.OrdinalIgnoreCase) ? "WEP"
            : profile.Authentication.Contains("SAE", StringComparison.OrdinalIgnoreCase) ? "SAE"
            : "WPA";

        var builder = new StringBuilder("WIFI:T:").Append(type).Append(";S:").Append(Escape(profile.Ssid)).Append(';');
        if (type != "nopass" && !string.IsNullOrEmpty(password))
        {
            builder.Append("P:").Append(Escape(password)).Append(';');
        }

        if (profile.Hidden)
        {
            builder.Append("H:true;");
        }

        return builder.Append(';').ToString();
    }

    private static string Escape(string value)
    {
        var builder = new StringBuilder(value.Length);
        foreach (var c in value)
        {
            if (c is '\\' or ';' or ',' or ':' or '"')
            {
                builder.Append('\\');
            }

            builder.Append(c);
        }

        return builder.ToString();
    }
}
