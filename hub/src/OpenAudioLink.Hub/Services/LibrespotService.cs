using System.Net;
using OpenAudioLink.Core.Audio;
using OpenAudioLink.Core.Devices;
using OpenAudioLink.Core.Net;
using OpenAudioLink.Core.Protocol;
using OpenAudioLink.Hub.Configuration;

namespace OpenAudioLink.Hub.Services;

/// <summary>What one cast point's Spotify Connect receiver is doing.</summary>
public sealed record LibrespotStatus(
    string CastPointId,
    string Name,
    bool Running,
    bool Playing,
    bool Streaming,
    long SamplesDelivered,
    string? Error);

/// <summary>
/// Runs one librespot per cast point and lets it drive the stream
/// (docs/CAST-POINTS.md, docs/LIBRESPOT.md).
///
/// The point of the feature is that choosing "Kitchen" on a phone picks
/// what plays and where it plays in one act. So the Hub keeps a receiver
/// running under each cast point's name, watches for audio to start coming
/// out of one, and sends it to that cast point's speakers. In normal use
/// nobody opens the Hub's interface at all.
///
/// A Hub with no librespot installed does nothing here and says so once.
/// </summary>
public sealed class LibrespotService : BackgroundService
{
    /// <summary>
    /// Fast enough that the moment between pressing play and hearing sound
    /// is short, and that little audio piles up unread before the stream
    /// carries it. Everything it does is comparing a handful of strings.
    /// </summary>
    private static readonly TimeSpan Tick = TimeSpan.FromMilliseconds(200);

    /// <summary>
    /// The reference format, not whatever the streamer last carried. A test
    /// tone sent as L16 must not decide how the house hears music.
    /// </summary>
    private static readonly AudioStreamFormat StreamFormat = new();

    private readonly CastPointStore _castPoints;
    private readonly DeviceRegistry _registry;
    private readonly RtpStreamer _streamer;
    private readonly DeviceCommandClient _commands;
    private readonly HubConfig _config;
    private readonly LibrespotOptions _options;
    private readonly HubPaths _paths;
    private readonly HubNetworkSetting _networkSetting;
    private readonly ILogger<LibrespotService> _logger;
    private readonly ILoggerFactory _loggers;

    private readonly Dictionary<string, LibrespotInstance> _instances = [];

    /// <summary>
    /// When each instance's audio started flowing. Two receivers can be
    /// playing at once — two accounts, two phones — and the Hub sends one
    /// stream, so the one that started most recently wins. Pressing play is
    /// a statement about what should be heard now.
    /// </summary>
    private readonly Dictionary<string, long> _playingSince = [];

    private string? _streaming;

    /// <summary>
    /// The token of the playback this service started, so ending it cannot
    /// delete a record that now belongs to another source.
    /// </summary>
    private long _streamingToken;
    private string? _lastComplaint;
    private string _executablePath = "librespot";

    public LibrespotService(
        CastPointStore castPoints, DeviceRegistry registry, RtpStreamer streamer,
        DeviceCommandClient commands, HubConfig config, LibrespotOptions options, HubPaths paths,
        HubNetworkSetting network, ILogger<LibrespotService> logger, ILoggerFactory loggers)
    {
        _castPoints = castPoints;
        _registry = registry;
        _streamer = streamer;
        _commands = commands;
        _config = config;
        _options = options;
        _paths = paths;
        _networkSetting = network;
        _logger = logger;
        _loggers = loggers;
    }

    /// <summary>
    /// The source name the streamer is started with. It identifies both the
    /// feature and the cast point, so this service can tell a stream it
    /// started from one a test tone replaced it with.
    /// </summary>
    public static string SourceKind(string castPointId) => SourcePrefix + castPointId;

    /// <summary>
    /// What every source name this service starts begins with, so a stream
    /// on the sender can be recognised as this service's without knowing
    /// which cast point it belongs to.
    /// </summary>
    private const string SourcePrefix = "librespot:";

    /// <summary>The cast point this service is currently streaming, if any.</summary>
    public string? StreamingCastPointId => _streaming;

    public IReadOnlyList<LibrespotStatus> Snapshot()
    {
        lock (_instances)
        {
            return [.. _instances.Values.Select(instance => new LibrespotStatus(
                instance.CastPointId,
                instance.Name,
                instance.Running,
                instance.Playing,
                instance.CastPointId == _streaming,
                instance.SamplesDelivered,
                instance.Error))];
        }
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!_options.Enabled)
        {
            _logger.LogInformation("Spotify Connect is switched off in configuration");
            return;
        }

        if (!_options.HasUsableFormat)
        {
            _logger.LogError(
                "Librespot:Format is '{Format}', which this Hub cannot decode; expected S16, S24_3, S32 or F32",
                _options.Format);
            return;
        }

        var executable = LibrespotLocator.Find(_options.ExecutablePath);
        if (executable is null)
        {
            // Not an error. Most of this project works without it, and the
            // operator supplies the binary themselves (docs/CAST-POINTS.md).
            _logger.LogInformation(
                "No librespot found, so cast points are not offered to Spotify. " +
                "Put the binary beside the Hub or set Librespot:ExecutablePath.");
            return;
        }

        _executablePath = executable;
        _logger.LogInformation("Spotify Connect receivers will run from {Path}", executable);

        // Once, at startup, and only to write down what this build offers.
        // Nothing acts on it yet: it is the measurement that decides how
        // decision 14's second attenuator gets removed.
        LibrespotCapabilities.Probe(executable, _logger);

        using var timer = new PeriodicTimer(Tick);
        try
        {
            do
            {
                try
                {
                    Reconcile(executable);
                    await DriveStreamAsync(stoppingToken);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    // A tick that throws must not take the Hub with it. A
                    // hosted service that faults stops the host by default,
                    // and losing device discovery because a directory could
                    // not be created would be a poor trade.
                    _logger.LogError(ex, "Spotify Connect tick failed");
                }
            }
            while (await timer.WaitForNextTickAsync(stoppingToken));
        }
        catch (OperationCanceledException)
        {
        }
        finally
        {
            lock (_instances)
            {
                foreach (var instance in _instances.Values)
                {
                    instance.Dispose();
                }
                _instances.Clear();
            }
        }
    }

    /// <summary>
    /// Makes the set of running receivers match the set of cast points. A
    /// cast point created in the Hub appears on the phone within a tick; one
    /// deleted disappears; one renamed is renamed, which means a new process
    /// because the name is what the network advertises.
    /// </summary>
    private void Reconcile(string executable)
    {
        var points = _castPoints.Snapshot();
        var zeroconf = ChooseZeroconfInterface();

        lock (_instances)
        {
            foreach (var gone in _instances.Keys.Except(points.Select(p => p.Id)).ToList())
            {
                _logger.LogInformation("Cast point {Id} is gone; stopping its receiver", gone);
                _instances[gone].Dispose();
                _instances.Remove(gone);
                _playingSince.Remove(gone);
            }

            foreach (var point in points)
            {
                if (_instances.TryGetValue(point.Id, out var existing))
                {
                    if (existing.Name == point.Name && existing.ZeroconfInterface == zeroconf)
                    {
                        continue;
                    }

                    if (existing.Name != point.Name)
                    {
                        _logger.LogInformation("{Old} was renamed to {New}", existing.Name, point.Name);
                    }
                    else
                    {
                        // Usually the first speaker being discovered, which
                        // is what tells the Hub which of its addresses is
                        // the one on the speakers' network.
                        _logger.LogInformation(
                            "{Name} will re-announce on {Address}", point.Name, zeroconf ?? "all interfaces");
                    }

                    existing.Dispose();
                    _instances.Remove(point.Id);
                    _playingSince.Remove(point.Id);
                }

                // Each cast point is a separate Spotify device with its own
                // cache, but the credential in it authenticates the account
                // rather than the device — verified on hardware by giving a
                // second cast point a copy of the first's and watching it
                // appear in the picker and play, with no sign-in of its own.
                var root = string.IsNullOrWhiteSpace(_options.CacheDirectory)
                    ? Path.Combine(_paths.DataDirectory, "librespot")
                    : _options.CacheDirectory;
                var cache = Path.Combine(root, point.Id);
                Directory.CreateDirectory(cache);
                if (!SeedCredentials(root, cache, point.Name))
                {
                    WarnIfNotSignedIn(point.Name, cache);
                }

                _instances[point.Id] = new LibrespotInstance(
                    point.Id, point.Name, executable, cache, zeroconf, _options, StreamFormat,
                    _loggers.CreateLogger($"Librespot.{point.Id}"));
            }
        }
    }

    /// <summary>
    /// Which of this host's addresses the receivers should announce on.
    ///
    /// Left to itself, librespot binds every interface and lets the
    /// operating system decide where multicast goes — by route metric. That
    /// is wrong on any machine with a VPN, and measurably so: on the machine
    /// this was found on, Tailscale held metric 5 against Wi-Fi's 50, so
    /// every announcement went out over the overlay to nobody while the
    /// phone sat on the LAN seeing nothing.
    ///
    /// The Hub can answer better than the routing table can, because it
    /// knows something the routing table does not: where the speakers are.
    /// An address sharing a subnet with a speaker is reachable from that
    /// subnet by definition, and the phone is on it too. This is
    /// <see cref="LocalAddressSelector"/>'s reasoning, applied to
    /// announcements instead of firmware downloads.
    /// </summary>
    /// <returns>An address, or null to leave the choice to librespot.</returns>
    private string? ChooseZeroconfInterface()
    {
        if (!string.IsNullOrWhiteSpace(_options.ZeroconfInterface))
        {
            return _options.ZeroconfInterface.Trim();
        }

        /*
         * One question, asked once, of the thing that knows the answer.
         *
         * This used to walk the registry itself and match each record's
         * subnet against this host's interfaces — which is the right idea
         * and was wrong in one detail: the registry contains the Hub, and
         * matching this machine against its own Tailscale address found
         * the Tailscale interface. Spotify was then announced on the
         * overlay, where the phone could see it and nothing could play.
         *
         * OalNetworkResolver asks it properly: the devices decide, the Hub
         * is not one of them, and the operator can overrule the lot.
         */
        var nodes = _registry.Snapshot()
            .Where(d => d.Id != _config.Id && d.Online)
            .Select(d => IPAddress.TryParse(d.Address, out var ip) ? ip : null)
            .OfType<IPAddress>()
            .ToList();

        // Hub:Network first: it is the whole system's answer, where
        // Librespot:ZeroconfInterface only ever spoke for this one feature.
        // The older key still works for anyone who set it.
        var configured = string.IsNullOrWhiteSpace(_networkSetting.Configured)
            ? _options.ZeroconfInterface
            : _networkSetting.Configured;

        var resolved = OalNetworkResolver.Resolve(
            configured,
            LocalAddressSelector.EnumerateLocalAddresses(),
            nodes);

        if (resolved.Network is not null)
        {
            _logger.LogInformation("Announcing Spotify on {Network}: {Why}",
                resolved.Network, resolved.Explanation);
            return resolved.Network.Address.ToString();
        }

        // Said out loud, because the alternative is librespot binding every
        // interface and Windows choosing by route metric — the trap that
        // put the announcement on a VPN in the first place.
        _logger.LogWarning(
            "Cannot tell which network OpenAudioLink runs on, so Spotify is announced "
            + "wherever librespot decides. {Why}", resolved.Explanation);
        return null;
    }

    /// <summary>
    /// Says, once per cast point, that it has never been signed in.
    ///
    /// Current Spotify clients do not offer *unclaimed* receivers in their
    /// device picker (docs/LIBRESPOT.md). A cast point that has never
    /// signed in therefore runs perfectly, announces itself correctly, and
    /// is invisible — which took this project two evenings to establish
    /// and should take the next person one log line.
    /// </summary>
    /// <summary>
    /// Gives a new cast point a copy of a signed-in one's credentials.
    /// </summary>
    /// <remarks>
    /// Sign in once, and every room works. Without this each new cast point
    /// needs its own browser round trip, which is a poor way to add the
    /// fourth speaker to a house and an actively bad one at a party.
    ///
    /// Safe because the file authenticates the *account*, not the device:
    /// each librespot still generates its own device id and appears as its
    /// own entry in the picker. Only ever copied between cast points of this
    /// Hub, which are the operator's own rooms on the operator's own
    /// account.
    ///
    /// Copying rather than sharing one file deliberately. librespot writes
    /// this path, and several processes writing one file is a way to lose
    /// every room's login at once rather than one of them.
    /// </remarks>
    /// <returns>True when the cast point has credentials, seeded or already there.</returns>
    private bool SeedCredentials(string root, string cache, string name)
    {
        const string fileName = "credentials.json";

        var destination = Path.Combine(cache, fileName);
        if (File.Exists(destination))
        {
            return true;
        }

        try
        {
            var source = Directory.EnumerateDirectories(root)
                .Select(directory => Path.Combine(directory, fileName))
                .FirstOrDefault(File.Exists);
            if (source is null)
            {
                return false;
            }

            File.Copy(source, destination);
            _logger.LogInformation(
                "{Name} signed in from an existing cast point's credentials; no browser needed",
                name);
            return true;
        }
        catch (IOException ex)
        {
            // Not fatal: the operator can still sign this one in by hand,
            // and WarnIfNotSignedIn is about to tell them how.
            _logger.LogWarning(ex, "Could not seed credentials for {Name}", name);
            return false;
        }
        catch (UnauthorizedAccessException ex)
        {
            _logger.LogWarning(ex, "Could not seed credentials for {Name}", name);
            return false;
        }
    }

    private void WarnIfNotSignedIn(string name, string cache)
    {
        if (File.Exists(Path.Combine(cache, "credentials.json")))
        {
            return;
        }

        // Built as one value rather than as placeholders: the point of the
        // message is a line the operator can paste, and a structured logger
        // is entitled to render placeholders however it likes.
        var command =
            $"\"{_executablePath}\" --name \"{name}\" --backend pipe " +
            $"--format {PcmDecoder.ToLibrespotName(_options.SampleFormat)} " +
            $"--device-type {_options.DeviceType} --cache \"{cache}\" --enable-oauth";

        _logger.LogWarning(
            "{Name} has never signed in to Spotify, so it will not appear in any device " +
            "picker. Sign it in once, at this machine, with a browser available:\n  {Command}\n" +
            "Then stop it with Ctrl+C. Leaving it running is the confusing case: it keeps the " +
            "name, so Spotify plays into the terminal instead of the Hub, and it complains that " +
            "Windows stdio in console mode does not support writing non-UTF-8 byte sequences. " +
            "That message means the audio had nowhere to go, not that the sign-in failed.",
            name, command);
    }

    private async Task DriveStreamAsync(CancellationToken cancellationToken)
    {
        List<LibrespotInstance> instances;
        lock (_instances)
        {
            instances = [.. _instances.Values];
        }

        var now = Environment.TickCount64;
        foreach (var instance in instances)
        {
            if (instance.Playing)
            {
                if (!_playingSince.ContainsKey(instance.CastPointId))
                {
                    _playingSince[instance.CastPointId] = now;
                }
            }
            else
            {
                _playingSince.Remove(instance.CastPointId);
            }
        }

        // Something else took the streamer — a test tone, or system audio
        // started from the Hub's page. It wins; this service is not going to
        // fight a person for the sender.
        if (_streaming is not null)
        {
            var status = _streamer.Status;
            if (!status.Running || status.Source != SourceKind(_streaming))
            {
                _logger.LogInformation("Stream for {CastPoint} was taken over; standing down", _streaming);
                // Only if the record is still ours. Being taken over means
                // something else has already claimed this room, and clearing
                // by cast point id would delete *its* record — leaving a
                // stream running that the Hub no longer knows about.
                _castPoints.MarkStoppedIfCurrent(_streamingToken);
                /*
                 * And stay stood down.
                 *
                 * Clearing _streaming was the whole of "standing down", and
                 * it lasted microseconds: librespot is still playing, so the
                 * next few lines picked the same cast point straight back up
                 * and restarted the sender on the same tick.
                 *
                 * That is what made switching to Vinyl sound broken. The
                 * node began sending to the speaker, this service began
                 * sending Spotify to the same speaker, and the consumer got
                 * two RTP streams with different SSRCs interleaved. Stopping
                 * Spotify on the phone cleared it, which is the tell — the
                 * Hub was never going to let go on its own.
                 *
                 * Standing aside is decided below, from who owns the room
                 * right now, rather than remembered here. Remembering it was
                 * the first attempt and it deadlocked: the claim was only
                 * released when librespot stopped playing, librespot went on
                 * playing to itself indefinitely, and the room stayed barred
                 * for good — audible, unstoppable, and impossible to hand to
                 * another speaker from the phone.
                 */
                _streaming = null;
            }
        }

        var wanted = _playingSince
            .OrderByDescending(entry => entry.Value)
            .Select(entry => entry.Key)
            .FirstOrDefault();

        if (wanted is null)
        {
            if (_streaming is not null)
            {
                await EndStreamAsync(_streaming, instances);
            }
            return;
        }

        /*
         * Stand aside while another source owns the room.
         *
         * Derived, not remembered. The question "may this service take the
         * sender" has an answer in the cast point store at every instant —
         * whether somebody else's playback is recorded — so asking it each
         * tick cannot get stuck the way a remembered claim did.
         *
         * Returning without touching the streamer is the point: the stream
         * running belongs to whoever owns the room, and stopping it here
         * would silence a record player on Spotify's behalf.
         *
         * The consequence is that Spotify resumes when the other source
         * stops, since the phone still believes it is playing. That is a
         * little surprising and enormously better than a room nothing can
         * play to.
         */
        /*
         * Stand aside while the sender is carrying somebody else's stream.
         *
         * The stand-down above has always said "it wins; this service is not
         * going to fight a person for the sender" — and then did fight,
         * because clearing _streaming was all it did and the next few lines
         * took the sender straight back. A test tone lasted one tick.
         *
         * Any librespot source counts as this service's, whichever cast
         * point it names: moving playback from one room to another on the
         * phone has to be allowed to replace its own stream.
         *
         * Derived from the streamer, so it cannot wedge — when the other
         * stream stops, this one is free to take over on the next tick.
         */
        var sender = _streamer.Status;
        if (sender.Running && sender.Source?.StartsWith(SourcePrefix, StringComparison.Ordinal) != true)
        {
            return;
        }

        /*
         * And stand aside while another source owns the room, which is the
         * same question asked of the cast point store rather than of the
         * sender. Both are needed: a node producer streams without touching
         * the Hub's sender at all, and the Hub's own raw stream endpoints
         * take the sender without recording a cast point.
         */
        var owner = _castPoints.Playing;
        if (owner is not null && owner.Token != _streamingToken)
        {
            return;
        }

        if (wanted != _streaming)
        {
            await BeginStreamAsync(wanted, instances, cancellationToken);
            return;
        }

        FollowDestinationChanges(wanted);
    }

    /// <summary>
    /// Named apart from the hosted service's own StartAsync/StopAsync,
    /// which mean starting and stopping this service rather than a stream.
    /// </summary>
    private async Task BeginStreamAsync(
        string castPointId, List<LibrespotInstance> instances, CancellationToken cancellationToken)
    {
        if (!_castPoints.TryGet(castPointId, out var point))
        {
            return;
        }

        var instance = instances.FirstOrDefault(i => i.CastPointId == castPointId);
        if (instance is null)
        {
            return;
        }

        var addresses = ResolveDestinations(point.Destinations, point.Name);
        if (addresses.Count == 0)
        {
            // Repeated every tick otherwise, and the reason does not change
            // five times a second.
            Complain($"{point.Name} has no speaker that can be reached, so there is nowhere to play it");
            return;
        }

        // A consumer can only play one stream, so a cast point that overlaps
        // whatever is playing replaces it — including a stream a node
        // producer is sending (docs/CAST-POINTS.md).
        var conflict = _castPoints.ConflictWith(castPointId);
        if (conflict is not null && conflict.ProducerId != _config.Id
            && _registry.TryGet(conflict.ProducerId, out var busy))
        {
            await _commands.StopStreamAsync(busy, cancellationToken);
            _castPoints.MarkStopped(conflict.CastPointId);
        }

        if (_streaming is not null && _streaming != castPointId)
        {
            _castPoints.MarkStoppedIfCurrent(_streamingToken);
        }

        try
        {
            await _streamer.StartAsync(
                SourceKind(castPointId), instance.CreateSource(), addresses,
                ProtocolSuite.RtpPort, StreamFormat);
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException)
        {
            _logger.LogError(ex, "Could not start streaming {CastPoint}", point.Name);
            return;
        }

        _streaming = castPointId;
        _lastComplaint = null;
        // Keep the token: it is what lets this service end its own playback
        // later without touching one that replaced it.
        _streamingToken = _castPoints.MarkPlaying(castPointId, _config.Id, "spotify").Token;
        _logger.LogInformation("{CastPoint} is playing from Spotify to {Count} speaker(s)",
            point.Name, addresses.Count);
    }

    private async Task EndStreamAsync(string castPointId, List<LibrespotInstance> instances)
    {
        await _streamer.StopAsync();
        _castPoints.MarkStoppedIfCurrent(_streamingToken);

        // The buffered tail belongs to the track that just ended; carrying
        // it into the next one would open every song with the last second of
        // the previous.
        instances.FirstOrDefault(i => i.CastPointId == castPointId)?.Discard();

        _streaming = null;
        _lastComplaint = null;
        _logger.LogInformation("Spotify stopped; {CastPoint} is quiet", castPointId);
    }

    /// <summary>
    /// Keeps a playing cast point's destinations in step with its
    /// membership, so a speaker added to the kitchen while music is playing
    /// starts playing, and one that rebooted comes back on its own.
    /// </summary>
    private void FollowDestinationChanges(string castPointId)
    {
        if (!_castPoints.TryGet(castPointId, out var point))
        {
            return;
        }

        var addresses = ResolveDestinations(point.Destinations, point.Name);
        if (addresses.Count == 0)
        {
            // Every speaker in the room went away. Leaving the stream
            // pointed where it was is right: they are coming back, and the
            // alternative is stopping the music because a node rebooted.
            return;
        }

        var current = _streamer.Status.Destinations;
        var wanted = addresses.Select(a => a.ToString()).ToList();
        if (current.Count == wanted.Count && !current.Except(wanted).Any())
        {
            return;
        }

        _streamer.SetDestinations(addresses);
    }

    private List<IPAddress> ResolveDestinations(IReadOnlyList<string> deviceIds, string castPointName)
    {
        var addresses = new List<IPAddress>();
        foreach (var deviceId in deviceIds)
        {
            if (!_registry.TryGet(deviceId, out var device))
            {
                Complain($"{castPointName} refers to a device the Hub has never seen ({deviceId})");
                continue;
            }
            if (!device.Online)
            {
                // Skipped rather than refused: a room with three speakers
                // and one unplugged should play through the other two.
                continue;
            }
            if (IPAddress.TryParse(device.Address, out var address))
            {
                addresses.Add(address);
            }
        }
        return addresses;
    }

    /// <summary>
    /// Logs a reason once rather than every tick. The same complaint five
    /// times a second buries whatever else the log was going to say.
    /// </summary>
    private void Complain(string message)
    {
        if (_lastComplaint == message)
        {
            return;
        }
        _lastComplaint = message;
        _logger.LogWarning("{Message}", message);
    }
}
