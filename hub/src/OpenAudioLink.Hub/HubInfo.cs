using System.Reflection;

namespace OpenAudioLink.Hub;

public static class HubInfo
{
    public static string Version { get; } =
        Assembly.GetExecutingAssembly().GetName().Version?.ToString(3) ?? "0.0.0";
}
