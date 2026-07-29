namespace OpenAudioLink.Core.Devices;

/// <summary>
/// Logical roles as defined in docs/ARCHITECTURE.md section 2. Roles are
/// capabilities, not device types: a device holds a *set* of them, which
/// is why announcements carry a list. An analog source that also plays is
/// both a producer and a consumer, and the Hub is controller and producer
/// at once.
///
/// Provisioner is deliberately absent from the wire: nothing on the
/// network acts on knowing which device does USB flashing and recovery,
/// so announcing it would be a name with no reader.
/// </summary>
public static class DeviceRole
{
    /// <summary>Discovery, selection, route ownership, configuration.</summary>
    public const string Controller = "controller";

    /// <summary>Generates an RTP audio stream.</summary>
    public const string Producer = "producer";

    /// <summary>Receives and plays audio: jitter buffer, clock, I²S out.</summary>
    public const string Consumer = "consumer";

    public static bool IsKnown(string role) =>
        role is Controller or Producer or Consumer;

    /// <summary>What the Hub itself announces (ARCHITECTURE.md section 5).</summary>
    public static readonly IReadOnlyList<string> HubRoles = [Controller, Producer];
}
