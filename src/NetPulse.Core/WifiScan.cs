namespace NetPulse.Core;

/// <summary>One access point (BSS) seen in a scan.</summary>
public sealed record WifiAccessPoint(
    string Ssid,
    string Bssid,
    int RssiDbm,
    uint SignalQuality,
    int FrequencyMHz,
    string Phy,
    bool IsConnected)
{
    public string Band => WifiChannels.BandOf(FrequencyMHz);

    public int Channel => WifiChannels.ChannelOf(FrequencyMHz);
}

/// <summary>How crowded one channel is: how many networks use it and a weighted overlap score.</summary>
public sealed record ChannelLoad(int Channel, int Networks, double Load);

public static class WifiChannels
{
    /// <summary>Candidate 20 MHz channels for a recommendation; the beacon list gives no channel width.</summary>
    private static readonly int[] FiveGhzChannels =
        [36, 40, 44, 48, 52, 56, 60, 64, 100, 104, 108, 112, 116, 120, 124, 128, 132, 136, 140, 144, 149, 153, 157, 161, 165];

    public static string BandOf(int frequencyMHz) => frequencyMHz switch
    {
        >= 5925 => "6 GHz",
        >= 4900 => "5 GHz",
        >= 2400 => "2.4 GHz",
        _ => string.Empty,
    };

    /// <summary>802.11 channel number of a center frequency, or 0 if it is not a Wi-Fi frequency.</summary>
    public static int ChannelOf(int frequencyMHz) => frequencyMHz switch
    {
        2484 => 14,
        >= 2412 and <= 2472 => (frequencyMHz - 2407) / 5,
        >= 5150 and < 5925 => (frequencyMHz - 5000) / 5,
        >= 5925 and <= 7125 => frequencyMHz == 5935 ? 2 : (frequencyMHz - 5950) / 5,
        _ => 0,
    };

    /// <summary>Channels worth considering in <paramref name="band"/>; 2.4 GHz only 1, 6 and 11, which do not overlap.</summary>
    public static IReadOnlyList<int> Candidates(string band) => band switch
    {
        "2.4 GHz" => [1, 6, 11],
        "5 GHz" => FiveGhzChannels,
        _ => [],
    };

    /// <summary>
    /// Load per channel of <paramref name="band"/> (2.4 GHz: channels 1–13; 5 and 6 GHz: the channels in use plus,
    /// for 5 GHz, the standard ones). A network adds its signal strength to its own channel and, in 2.4 GHz where
    /// 20 MHz channels overlap, a fading share to the four neighbours on each side.
    /// </summary>
    public static IReadOnlyList<ChannelLoad> Congestion(IEnumerable<WifiAccessPoint> accessPoints, string band)
    {
        var inBand = accessPoints.Where(a => a.Band == band && a.Channel > 0).ToList();
        var channels = band == "2.4 GHz"
            ? Enumerable.Range(1, 13)
            : Candidates(band).Concat(inBand.Select(a => a.Channel)).Distinct().Order();

        var result = new List<ChannelLoad>();
        foreach (var channel in channels)
        {
            var load = 0.0;
            var networks = 0;
            foreach (var ap in inBand)
            {
                var overlap = Overlap(band, channel, ap.Channel);
                if (overlap <= 0)
                {
                    continue;
                }

                load += overlap * Strength(ap.RssiDbm);
                if (ap.Channel == channel)
                {
                    networks++;
                }
            }

            result.Add(new ChannelLoad(channel, networks, load));
        }

        return result;
    }

    /// <summary>The candidate channel with the least load; null when the band has no recommendation (6 GHz) or no data.</summary>
    public static int? Recommend(IEnumerable<WifiAccessPoint> accessPoints, string band)
    {
        var candidates = Candidates(band);
        if (candidates.Count == 0)
        {
            return null;
        }

        return Congestion(accessPoints, band)
            .Where(c => candidates.Contains(c.Channel))
            .OrderBy(c => c.Load)
            .ThenBy(c => c.Networks)
            .Select(c => (int?)c.Channel)
            .FirstOrDefault();
    }

    /// <summary>1 for a very strong signal (−30 dBm), 0 at −100 dBm.</summary>
    public static double Strength(int rssiDbm) => Math.Clamp((rssiDbm + 100) / 70.0, 0, 1);

    private static double Overlap(string band, int channel, int other)
    {
        if (band != "2.4 GHz")
        {
            return channel == other ? 1 : 0;
        }

        var distance = Math.Abs(channel - other);
        return distance >= 5 ? 0 : 1 - (distance / 5.0);
    }
}
