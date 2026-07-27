namespace OpenAudioLink.Core.Devices;

/// <summary>
/// Primary logical role a device announces. See protocol/IDENTITY.md.
/// </summary>
public static class DeviceRole
{
    public const string Receiver = "receiver";
    public const string AnalogSource = "analog-source";
    public const string Hub = "hub";

    public static bool IsKnown(string role) =>
        role is Receiver or AnalogSource or Hub;
}
