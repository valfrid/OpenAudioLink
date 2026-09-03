namespace OpenAudioLink.Core.Audio;

/// <summary>
/// What a recorder should do with one arriving packet, given the ones
/// before it.
/// </summary>
/// <param name="SilenceFrames">
/// Frames of silence to write first, standing in for packets that never
/// arrived.
/// </param>
/// <param name="Write">Whether this packet's payload belongs in the file.</param>
public readonly record struct RtpTimelineStep(int SilenceFrames, bool Write);

/// <summary>
/// Keeps a recording's time axis true when packets go missing, arrive
/// twice, or arrive late.
/// </summary>
/// <remarks>
/// <para>
/// <b>This is the difference between a recording and a measurement.</b>
/// Concatenating whatever arrives produces a file that sounds almost
/// right and whose time axis is wrong by however much was lost — and every
/// number taken from it is then wrong by the same amount, silently. The
/// clap test that measured 281.4 ms would have read 276 ms after a single
/// dropped packet, with nothing anywhere to say so.
/// </para>
/// <para>
/// So gaps are filled rather than closed. The file's length is real time,
/// a sample at ten seconds happened at ten seconds, and the count of
/// substituted frames is reported so a reader knows how much of what they
/// are looking at is invention.
/// </para>
/// <para>
/// Sequence numbers rather than timestamps, because a sender that pauses
/// and resumes keeps counting sequence numbers while its timestamp jumps —
/// and the profile's fixed 240-frame packets make the two equivalent when
/// nothing has gone wrong.
/// </para>
/// </remarks>
public sealed class RtpTimeline
{
    private readonly int _framesPerPacket;

    private ushort _expected;
    private bool _started;
    private uint _ssrc;

    public RtpTimeline(int framesPerPacket = 240)
    {
        if (framesPerPacket <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(framesPerPacket));
        }
        _framesPerPacket = framesPerPacket;
    }

    /// <summary>Packets whose payload was written.</summary>
    public long Written { get; private set; }

    /// <summary>Frames of silence substituted for packets that never came.</summary>
    public long SilenceFrames { get; private set; }

    /// <summary>Packets that arrived after the recorder had moved past them.</summary>
    public long Late { get; private set; }

    /// <summary>Packets that arrived twice.</summary>
    public long Duplicates { get; private set; }

    /// <summary>Times a different sender took over mid-recording.</summary>
    public long SsrcChanges { get; private set; }

    /// <summary>How much of the file is invented, as a fraction of its length.</summary>
    public double SilenceFraction
    {
        get
        {
            long total = Written * _framesPerPacket + SilenceFrames;
            return total == 0 ? 0 : (double)SilenceFrames / total;
        }
    }

    /// <summary>
    /// Decides what to write for one packet.
    /// </summary>
    public RtpTimelineStep Accept(ushort sequence, uint ssrc)
    {
        if (!_started)
        {
            _started = true;
            _ssrc = ssrc;
            _expected = unchecked((ushort)(sequence + 1));
            Written++;
            return new RtpTimelineStep(0, true);
        }

        /*
         * A new source is a new stream, and its sequence numbers have
         * nothing to do with the old one's. Re-baselining rather than
         * filling the difference: the alternative is inventing however many
         * hours of silence lie between two unrelated counters.
         */
        if (ssrc != _ssrc)
        {
            SsrcChanges++;
            _ssrc = ssrc;
            _expected = unchecked((ushort)(sequence + 1));
            Written++;
            return new RtpTimelineStep(0, true);
        }

        // Unsigned 16-bit difference, so the counter wrapping costs nothing.
        int ahead = unchecked((ushort)(sequence - _expected));

        if (ahead == 0)
        {
            _expected = unchecked((ushort)(sequence + 1));
            Written++;
            return new RtpTimelineStep(0, true);
        }

        /*
         * More than half the sequence space ahead is behind: the packet is
         * late or duplicated rather than the stream having jumped 32 000
         * packets forward. Dropped rather than written, because its place
         * in the file has already been filled with silence and moving the
         * write head backwards would corrupt what is already there.
         */
        if (ahead > 0x7FFF)
        {
            if (ahead == 0xFFFF)
            {
                Duplicates++;
            }
            else
            {
                Late++;
            }
            return new RtpTimelineStep(0, false);
        }

        _expected = unchecked((ushort)(sequence + 1));
        Written++;
        SilenceFrames += (long)ahead * _framesPerPacket;
        return new RtpTimelineStep(ahead * _framesPerPacket, true);
    }
}
