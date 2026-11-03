using System.Net;
using System.Runtime.InteropServices;
using NetPulse.Core;

namespace NetPulse.Platform;

/// <summary>Reads the IPv4 neighbour (ARP) table of this PC: the devices it has recently exchanged frames with.</summary>
internal static class ArpTable
{
    private const uint ErrorInsufficientBuffer = 122;
    private const uint TypeInvalid = 2;

    // MIB_IPNETROW: index, physical address length, 8 address bytes, IPv4 address, type; 24 bytes.
    private const int RowSize = 24;

    public static IReadOnlyList<ArpEntry> Read()
    {
        uint size = 0;
        var status = GetIpNetTable(0, ref size, false);
        if (status != ErrorInsufficientBuffer || size == 0)
        {
            return [];
        }

        var buffer = Marshal.AllocHGlobal((int)size);
        try
        {
            if (GetIpNetTable(buffer, ref size, false) != 0)
            {
                return [];
            }

            var entries = new List<ArpEntry>();
            var count = Marshal.ReadInt32(buffer);
            for (var i = 0; i < count; i++)
            {
                var row = buffer + 4 + (i * RowSize);
                var macLength = Marshal.ReadInt32(row, 4);
                if ((uint)Marshal.ReadInt32(row, 20) == TypeInvalid || macLength is < 1 or > 8)
                {
                    continue;
                }

                var mac = new byte[macLength];
                Marshal.Copy(row + 8, mac, 0, macLength);
                var address = new byte[4];
                Marshal.Copy(row + 16, address, 0, 4);
                entries.Add(new ArpEntry(new IPAddress(address), string.Join('-', mac.Select(b => b.ToString("X2")))));
            }

            return entries;
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    [DllImport("iphlpapi.dll")]
    private static extern uint GetIpNetTable(nint table, ref uint size, [MarshalAs(UnmanagedType.Bool)] bool sort);
}
