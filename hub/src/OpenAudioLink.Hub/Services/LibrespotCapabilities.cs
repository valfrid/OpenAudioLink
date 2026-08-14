using System.Diagnostics;

namespace OpenAudioLink.Hub.Services;

/// <summary>
/// What this particular librespot build can be asked to do.
/// </summary>
/// <remarks>
/// Asked rather than assumed, because librespot's options move between
/// versions and the operator supplies the binary themselves — this project
/// does not know which one is on the machine. Guessing a flag name is not a
/// cheap mistake either: librespot exits on an unknown argument, so a wrong
/// guess does not degrade, it takes Spotify away entirely.
///
/// It exists for decision 14's unfinished half. Volume is meant to be
/// digital gain at the Consumer, and librespot applies the phone's volume to
/// the samples before they ever reach the Hub — a second attenuator in
/// series that made a speaker at 38 % silent rather than quiet, because the
/// analogue level fell under the cabinet's standby threshold. Putting that
/// right means telling librespot to leave the samples alone and applying the
/// phone's volume at the node, and both halves depend on flags that may or
/// may not be there.
///
/// So the Hub reads <c>--help</c> once and writes down what it found. The
/// answer reaches a log line whether or not anything uses it yet, which is
/// the point: the design of the fix should be settled by what is on the
/// machine rather than by what anybody remembers.
/// </remarks>
public sealed record LibrespotCapabilities(
    bool VolumeControl,
    bool EventHook,
    bool InitialVolume,
    IReadOnlyList<string> VolumeAndEventOptions)
{
    /// <summary>Nothing known, which is what a failed probe means.</summary>
    public static readonly LibrespotCapabilities Unknown = new(false, false, false, []);

    /// <summary>
    /// Whether the phone's volume could be moved to the node, leaving one
    /// gain stage where decision 14 says it belongs.
    /// </summary>
    /// <remarks>
    /// Both halves or neither. Telling librespot to stop attenuating without
    /// being able to read the phone's volume would leave the phone's slider
    /// doing nothing at all — worse than the two-attenuator problem it is
    /// meant to fix, and worse in a way somebody would notice immediately.
    /// </remarks>
    public bool CanUnifyVolume => VolumeControl && EventHook;

    public static LibrespotCapabilities Probe(string executable, ILogger logger)
    {
        try
        {
            using var process = Process.Start(new ProcessStartInfo
            {
                FileName = executable,
                Arguments = "--help",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            });

            if (process is null)
            {
                return Unknown;
            }

            var text = process.StandardOutput.ReadToEnd() + process.StandardError.ReadToEnd();

            // A help screen that takes this long is not a help screen.
            if (!process.WaitForExit(5000))
            {
                process.Kill(entireProcessTree: true);
                logger.LogWarning("librespot --help did not finish; its options are unknown");
                return Unknown;
            }

            // Reported verbatim rather than interpreted. The next decision
            // here is about the exact spelling of an option, so a summary
            // would throw away the only thing worth having.
            var interesting = text
                .Split('\n')
                .Select(line => line.TrimEnd())
                .Where(line => line.Contains("volume", StringComparison.OrdinalIgnoreCase)
                    || line.Contains("event", StringComparison.OrdinalIgnoreCase))
                .Where(line => line.TrimStart().StartsWith('-'))
                .ToList();

            var capabilities = new LibrespotCapabilities(
                VolumeControl: text.Contains("--volume-ctrl", StringComparison.Ordinal),
                EventHook: text.Contains("--onevent", StringComparison.Ordinal),
                InitialVolume: text.Contains("--initial-volume", StringComparison.Ordinal),
                interesting);

            logger.LogInformation(
                "librespot volume and event options: {Options}",
                interesting.Count == 0 ? "none found" : string.Join(" | ", interesting));
            logger.LogInformation(
                "librespot can {Can} the phone's volume moved to the speaker "
                + "(volume-ctrl {VolumeCtrl}, event hook {Events})",
                capabilities.CanUnifyVolume ? "have" : "not have",
                capabilities.VolumeControl, capabilities.EventHook);

            return capabilities;
        }
        catch (Exception ex)
        {
            // Not fatal. Everything works as it does today; only the
            // information is missing.
            logger.LogWarning(ex, "Could not ask librespot what options it supports");
            return Unknown;
        }
    }
}
