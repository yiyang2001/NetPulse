using System.ComponentModel;
using System.Net.NetworkInformation;
using NetPulse.Core;

namespace NetPulse.Platform;

/// <summary>
/// Names the network a Wi-Fi adapter is connected to, for per-network statistics. Other adapters are
/// identified by their own name. The SSIDs are looked up at most every few seconds and cached.
/// </summary>
internal sealed class WifiNetworkNames
{
    private static readonly TimeSpan RefreshInterval = TimeSpan.FromSeconds(5);

    private Dictionary<string, string> _ssidByAdapter = new(StringComparer.OrdinalIgnoreCase);
    private DateTime _refreshedAt = DateTime.MinValue;
    private bool _failureLogged;

    public NetworkIdentity Identify(AdapterSnapshot adapter)
    {
        if (adapter.Type != NetworkInterfaceType.Wireless80211)
        {
            return NetworkIdentity.ForAdapter(adapter);
        }

        Refresh();
        return _ssidByAdapter.TryGetValue(adapter.Id, out var ssid) && ssid.Length > 0
            ? NetworkIdentity.ForWifi(ssid)
            : NetworkIdentity.ForAdapter(adapter);
    }

    private void Refresh()
    {
        if (DateTime.UtcNow - _refreshedAt < RefreshInterval)
        {
            return;
        }

        _refreshedAt = DateTime.UtcNow; // also after a failure, so a stopped WLAN service is not retried every second
        try
        {
            using var wlan = new WlanClient();
            var names = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var nic in wlan.GetInterfaces())
            {
                if (wlan.GetCurrentConnection(nic.Id) is { Ssid.Length: > 0 } connection)
                {
                    names[nic.Id.ToString("B")] = connection.Ssid;
                }
            }

            _ssidByAdapter = names;
        }
        catch (Win32Exception ex)
        {
            // WLAN AutoConfig not running, or Windows withholds the name (location permission): fall back to the adapter name.
            if (!_failureLogged)
            {
                _failureLogged = true;
                ErrorLog.Write(ex, "WifiNetworkNames");
            }

            _ssidByAdapter = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        }
    }
}
