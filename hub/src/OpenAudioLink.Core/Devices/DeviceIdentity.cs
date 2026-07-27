namespace OpenAudioLink.Core.Devices;

/// <summary>
/// Helpers for the device identity string model. See protocol/IDENTITY.md.
/// </summary>
public static class DeviceIdentity
{
    public const string ProvisionedPrefix = "oal-";
    public const string FactoryPrefix = "mac-";

    /// <summary>Creates a new provisioned identity, e.g. "oal-9f8e7d6c-…".</summary>
    public static string NewProvisioned() =>
        ProvisionedPrefix + Guid.NewGuid().ToString("D").ToLowerInvariant();

    /// <summary>Factory identity from a MAC address, e.g. "mac-a0b1c2d3e4f5".</summary>
    public static string FromMac(ReadOnlySpan<byte> mac)
    {
        if (mac.Length != 6)
        {
            throw new ArgumentException("MAC address must be 6 bytes.", nameof(mac));
        }

        return FactoryPrefix + Convert.ToHexString(mac).ToLowerInvariant();
    }

    public static bool IsValid(string? id)
    {
        if (string.IsNullOrEmpty(id))
        {
            return false;
        }

        if (id.StartsWith(ProvisionedPrefix, StringComparison.Ordinal))
        {
            return Guid.TryParseExact(id[ProvisionedPrefix.Length..], "D", out _);
        }

        if (id.StartsWith(FactoryPrefix, StringComparison.Ordinal))
        {
            var hex = id.AsSpan(FactoryPrefix.Length);
            if (hex.Length != 12)
            {
                return false;
            }

            foreach (var c in hex)
            {
                if (c is not ((>= '0' and <= '9') or (>= 'a' and <= 'f')))
                {
                    return false;
                }
            }

            return true;
        }

        return false;
    }
}
