using OpenAudioLink.Core.Audio;
using OpenAudioLink.Core.Devices;

namespace OpenAudioLink.Hub.Services;

/// <summary>
/// Walks each node toward the latency profile it is meant to be running.
/// </summary>
/// <remarks>
/// <para>
/// A profile is two settings that cannot be applied together. The ring is
/// an allocation and takes effect at the next boot; the delay takes effect
/// immediately, and the node <b>refuses</b> a delay its current ring
/// cannot hold rather than clamping it — <c>delay_ceiling()</c> in
/// <c>oal_control.c</c>. Moving a node from Standard to Long therefore
/// asks for a 450 ms delay that its 400 ms ring rejects with a 400, and no
/// ordering of two calls inside one request can avoid that.
/// </para>
/// <para>
/// So the profile is written down and converged on instead: set the ring,
/// and when the node comes back with it, set the delay. That also covers
/// the cases a one-shot push cannot — a node that was offline when the
/// profile was chosen, one that was reflashed back to defaults, one that
/// lost its NVS. Each simply arrives, is found to disagree, and is
/// corrected.
/// </para>
/// <para>
/// <b>It only ever moves a node toward a profile somebody chose.</b> A
/// node with no stored profile is left alone entirely, and setting the
/// ring or delay by hand clears the profile, so the operator wins any
/// disagreement rather than being overruled twenty seconds later.
/// </para>
/// </remarks>
public sealed class LatencyProfileReconciler : BackgroundService
{
    /// <summary>
    /// Slow on purpose. Nothing here is urgent — the case it exists for is
    /// a node that has just rebooted, which takes far longer than this —
    /// and every pass that finds work to do writes to a device with seven
    /// sockets. The HTTP 502s of run 37 were caused by polling a node too
    /// eagerly, and a convenience feature is not worth repeating that for.
    /// </summary>
    public static readonly TimeSpan Interval = TimeSpan.FromSeconds(20);

    private readonly DeviceRegistry _registry;
    private readonly NodeAudioStore _store;
    private readonly DeviceCommandClient _commands;
    private readonly ILogger<LatencyProfileReconciler> _logger;

    /// <summary>
    /// Nodes already reported as unreachable, so a node that is simply off
    /// does not write a line every twenty seconds all night.
    /// </summary>
    private readonly HashSet<string> _quiet = [];

    public LatencyProfileReconciler(
        DeviceRegistry registry, NodeAudioStore store,
        DeviceCommandClient commands, ILogger<LatencyProfileReconciler> logger)
    {
        _registry = registry;
        _store = store;
        _commands = commands;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(Interval);
        try
        {
            do
            {
                try
                {
                    await ReconcileAsync(stoppingToken);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    // Convergence is a convenience. It must never be able to
                    // take the Hub, or the music, down with it.
                    _logger.LogDebug(ex, "Latency profile pass failed");
                }
            }
            while (await timer.WaitForNextTickAsync(stoppingToken));
        }
        catch (OperationCanceledException)
        {
        }
    }

    private async Task ReconcileAsync(CancellationToken cancellationToken)
    {
        var wanted = _store.Snapshot();
        if (wanted.Count == 0)
        {
            return;
        }

        foreach (var device in _registry.Snapshot())
        {
            if (!wanted.TryGetValue(device.Id, out var intent)
                || intent.Profile is null
                || LatencyProfile.ById(intent.Profile) is not { } profile)
            {
                continue;
            }

            var status = device.Status;
            if (status is null || !device.Online)
            {
                continue;
            }

            /*
             * The ring first, and only the ring: until the node is running
             * it, the delay the profile wants may be one this node still
             * rejects. Nothing else happens on this pass -- the reboot is
             * the operator's to make, and the next pass will find the new
             * ring waiting.
             */
            if (status.RingMs != profile.RingMs)
            {
                if (await Send(device, "ring",
                        ct => _commands.SetRingAsync(device, profile.RingMs, ct),
                        cancellationToken))
                {
                    _logger.LogInformation(
                        "{Device}: ring set to {RingMs} ms for the {Profile} profile; "
                        + "it takes effect when the node next reboots",
                        device.Name, profile.RingMs, profile.Name);
                }
                continue;
            }

            var delay = profile.DelayMs + intent.AlignMs;
            if (status.DelayMs != delay)
            {
                if (await Send(device, "delay",
                        ct => _commands.SetDelayAsync(device, delay, ct),
                        cancellationToken))
                {
                    _logger.LogInformation(
                        "{Device}: now on the {Profile} profile — {TargetMs} ms target "
                        + "resting at about {RestingMs} ms{Align}",
                        device.Name, profile.Name, profile.TargetMs, profile.SteerToMs,
                        intent.AlignMs > 0 ? $", plus {intent.AlignMs} ms of alignment" : "");
                }
            }
            else
            {
                _quiet.Remove(device.Id);
            }
        }
    }

    /// <summary>
    /// Sends one setting, reporting a failure once rather than on every
    /// pass. Returns whether it landed.
    /// </summary>
    private async Task<bool> Send(
        DeviceRecord device, string what,
        Func<CancellationToken, Task<bool>> send, CancellationToken cancellationToken)
    {
        if (await send(cancellationToken))
        {
            _quiet.Remove(device.Id);
            return true;
        }

        if (_quiet.Add(device.Id))
        {
            _logger.LogWarning(
                "{Device}: could not set the {What} for its latency profile; "
                + "will keep trying", device.Name, what);
        }
        return false;
    }
}
