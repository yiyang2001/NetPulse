using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;
using NetPulse.Core;

namespace NetPulse.Platform;

internal sealed record WlanInterfaceInfo(Guid Id, string Description, string State);

internal sealed record WlanConnectionInfo(
    string ProfileName,
    string Ssid,
    string Bssid,
    string PhyType,
    uint SignalQuality,
    int? RssiDbm,
    uint? Channel,
    uint ReceiveRateKbps,
    uint TransmitRateKbps,
    string Authentication,
    string Cipher);

/// <summary>Native Wifi (wlanapi.dll) access: interfaces, the current connection, and saved profiles.</summary>
internal sealed unsafe class WlanClient : IDisposable
{
    public const uint ErrorAccessDenied = 5;

    private const uint ERROR_SUCCESS = 0;
    private const uint WLAN_PROFILE_GET_PLAINTEXT_KEY = 4;
    private const int OpcodeCurrentConnection = 7;
    private const int OpcodeChannelNumber = 8;
    private const int OpcodeRssi = 0x10000102;

    private readonly nint _handle;

    public WlanClient()
    {
        var status = WlanOpenHandle(2, 0, out _, out _handle);
        if (status != ERROR_SUCCESS)
        {
            throw new Win32Exception((int)status, "WlanOpenHandle failed. Is the WLAN AutoConfig service running?");
        }
    }

    public void Dispose() => _ = WlanCloseHandle(_handle, 0);

    public IReadOnlyList<WlanInterfaceInfo> GetInterfaces()
    {
        Check(WlanEnumInterfaces(_handle, 0, out var list), "WlanEnumInterfaces");
        try
        {
            var result = new List<WlanInterfaceInfo>();
            var items = (WlanInterfaceInfoNative*)((byte*)list + 8);
            for (var i = 0; i < *(uint*)list; i++)
            {
                result.Add(new WlanInterfaceInfo(
                    items[i].InterfaceGuid,
                    new string(items[i].Description),
                    StateName(items[i].State)));
            }

            return result;
        }
        finally
        {
            WlanFreeMemory(list);
        }
    }

    /// <summary>Returns null when the interface is not connected. Throws <see cref="Win32Exception"/> with
    /// <see cref="ErrorAccessDenied"/> when Windows withholds the data (location permission on Windows 11 24H2+).</summary>
    public WlanConnectionInfo? GetCurrentConnection(Guid interfaceId)
    {
        var status = WlanQueryInterface(_handle, &interfaceId, OpcodeCurrentConnection, 0, out _, out var data, out _);
        if (status == 0x0000139F /* ERROR_INVALID_STATE: not connected */)
        {
            return null;
        }

        Check(status, "WlanQueryInterface(current_connection)");
        try
        {
            var connection = (WlanConnectionAttributes*)data;
            if (connection->State != 1 /* wlan_interface_state_connected */)
            {
                return null;
            }

            var association = connection->Association;
            var ssidLength = (int)Math.Min(32, association.SsidLength);
            var ssid = Encoding.UTF8.GetString(new ReadOnlySpan<byte>(association.Ssid, ssidLength));
            var bssid = string.Join(':', new ReadOnlySpan<byte>(association.Bssid, 6).ToArray().Select(b => b.ToString("X2")));

            return new WlanConnectionInfo(
                new string(connection->ProfileName),
                ssid,
                bssid,
                PhyName(association.PhyType),
                association.SignalQuality,
                QueryInt(interfaceId, OpcodeRssi),
                (uint?)QueryInt(interfaceId, OpcodeChannelNumber),
                association.RxRate,
                association.TxRate,
                AuthName(connection->Security.AuthAlgorithm),
                CipherName(connection->Security.CipherAlgorithm));
        }
        finally
        {
            WlanFreeMemory(data);
        }
    }

    public IReadOnlyList<string> GetProfileNames(Guid interfaceId)
    {
        Check(WlanGetProfileList(_handle, &interfaceId, 0, out var list), "WlanGetProfileList");
        try
        {
            var result = new List<string>();
            var items = (WlanProfileInfoNative*)((byte*)list + 8);
            for (var i = 0; i < *(uint*)list; i++)
            {
                result.Add(new string(items[i].ProfileName));
            }

            return result;
        }
        finally
        {
            WlanFreeMemory(list);
        }
    }

    /// <summary>
    /// Reads one saved profile. With <paramref name="includeKey"/> the key is requested in plain text;
    /// Windows only honours that when the caller is allowed to, otherwise it comes back protected.
    /// </summary>
    public WifiProfileInfo GetProfile(Guid interfaceId, string profileName, bool includeKey)
    {
        var flags = includeKey ? WLAN_PROFILE_GET_PLAINTEXT_KEY : 0u;
        Check(WlanGetProfile(_handle, &interfaceId, profileName, 0, out var xml, ref flags, out _), "WlanGetProfile");
        try
        {
            return WifiProfileParser.Parse(new string((char*)xml));
        }
        finally
        {
            WlanFreeMemory(xml);
        }
    }

    private int? QueryInt(Guid interfaceId, int opcode)
    {
        if (WlanQueryInterface(_handle, &interfaceId, opcode, 0, out var size, out var data, out _) != ERROR_SUCCESS)
        {
            return null;
        }

        try
        {
            return size >= 4 ? *(int*)data : null;
        }
        finally
        {
            WlanFreeMemory(data);
        }
    }

    private static void Check(uint status, string operation)
    {
        if (status != ERROR_SUCCESS)
        {
            throw new Win32Exception((int)status, $"{operation} failed ({status}).");
        }
    }

    private static string StateName(uint state) => state switch
    {
        0 => "未就绪",
        1 => "已连接",
        2 => "临时网络已形成",
        3 => "正在断开",
        4 => "未连接",
        5 => "正在关联",
        6 => "正在发现",
        7 => "正在认证",
        _ => $"状态 {state}",
    };

    private static string PhyName(uint phy) => phy switch
    {
        4 => "802.11a",
        5 => "802.11b",
        6 => "802.11g",
        7 => "802.11n (Wi-Fi 4)",
        8 => "802.11ac (Wi-Fi 5)",
        9 => "802.11ad",
        10 => "802.11ax (Wi-Fi 6/6E)",
        11 => "802.11be (Wi-Fi 7)",
        _ => $"PHY {phy}",
    };

    private static string AuthName(uint algorithm) => algorithm switch
    {
        1 => "开放",
        2 => "共享密钥 (WEP)",
        3 => "WPA-企业",
        4 => "WPA-个人",
        6 => "WPA2-企业",
        7 => "WPA2-个人",
        8 => "WPA3-企业 (192 位)",
        9 => "WPA3-个人",
        10 => "增强开放 (OWE)",
        11 => "WPA3-企业",
        _ => $"认证 {algorithm}",
    };

    private static string CipherName(uint cipher) => cipher switch
    {
        0 => "无",
        1 => "WEP-40",
        2 => "TKIP",
        4 => "CCMP (AES)",
        5 => "WEP-104",
        6 => "BIP",
        8 => "GCMP-128",
        9 => "GCMP-256",
        0x101 => "WEP",
        _ => $"0x{cipher:X}",
    };

    [DllImport("wlanapi.dll")]
    private static extern uint WlanOpenHandle(uint clientVersion, nint reserved, out uint negotiatedVersion, out nint clientHandle);

    [DllImport("wlanapi.dll")]
    private static extern uint WlanCloseHandle(nint clientHandle, nint reserved);

    [DllImport("wlanapi.dll")]
    private static extern void WlanFreeMemory(void* memory);

    [DllImport("wlanapi.dll")]
    private static extern uint WlanEnumInterfaces(nint clientHandle, nint reserved, out void* interfaceList);

    [DllImport("wlanapi.dll")]
    private static extern uint WlanQueryInterface(
        nint clientHandle, Guid* interfaceGuid, int opcode, nint reserved, out uint dataSize, out void* data, out int valueType);

    [DllImport("wlanapi.dll")]
    private static extern uint WlanGetProfileList(nint clientHandle, Guid* interfaceGuid, nint reserved, out void* profileList);

    [DllImport("wlanapi.dll", CharSet = CharSet.Unicode)]
    private static extern uint WlanGetProfile(
        nint clientHandle, Guid* interfaceGuid, string profileName, nint reserved, out void* profileXml, ref uint flags, out uint grantedAccess);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct WlanInterfaceInfoNative
    {
        public Guid InterfaceGuid;
        public fixed char Description[256];
        public uint State;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct WlanProfileInfoNative
    {
        public fixed char ProfileName[256];
        public uint Flags;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct WlanAssociationAttributes
    {
        public uint SsidLength;
        public fixed byte Ssid[32];
        public uint BssType;
        public fixed byte Bssid[6];
        public uint PhyType;
        public uint PhyIndex;
        public uint SignalQuality;
        public uint RxRate;
        public uint TxRate;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct WlanSecurityAttributes
    {
        public int SecurityEnabled;
        public int OneXEnabled;
        public uint AuthAlgorithm;
        public uint CipherAlgorithm;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct WlanConnectionAttributes
    {
        public uint State;
        public uint ConnectionMode;
        public fixed char ProfileName[256];
        public WlanAssociationAttributes Association;
        public WlanSecurityAttributes Security;
    }
}
