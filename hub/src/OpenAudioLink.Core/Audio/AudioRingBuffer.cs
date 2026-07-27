namespace OpenAudioLink.Core.Audio;

/// <summary>
/// Fixed-capacity float ring buffer bridging a push-style capture
/// callback to the pull-style sender.
///
/// The two ends run on different threads at slightly different rates, so
/// both directions of mismatch are handled explicitly rather than
/// blocking: a writer that gets ahead drops the *oldest* samples (live
/// audio should stay current rather than fall behind), and a reader that
/// gets ahead is given silence. Both are counted so a stream can report
/// that it is struggling instead of just sounding bad.
/// </summary>
public sealed class AudioRingBuffer
{
    private readonly float[] _buffer;
    private readonly object _sync = new();
    private int _readIndex;
    private int _writeIndex;
    private int _available;

    public AudioRingBuffer(int capacity)
    {
        if (capacity <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(capacity), "Capacity must be positive.");
        }

        _buffer = new float[capacity];
    }

    public int Capacity => _buffer.Length;

    public int Available
    {
        get { lock (_sync) { return _available; } }
    }

    /// <summary>Samples discarded because the reader could not keep up.</summary>
    public long DroppedSamples { get; private set; }

    /// <summary>Samples of silence substituted because the writer fell behind.</summary>
    public long UnderrunSamples { get; private set; }

    public void Write(ReadOnlySpan<float> source)
    {
        lock (_sync)
        {
            // More than the whole buffer: keep only the newest samples.
            if (source.Length >= _buffer.Length)
            {
                DroppedSamples += _available + source.Length - _buffer.Length;
                source[^_buffer.Length..].CopyTo(_buffer);
                _readIndex = 0;
                _writeIndex = 0;
                _available = _buffer.Length;
                return;
            }

            int overflow = _available + source.Length - _buffer.Length;
            if (overflow > 0)
            {
                _readIndex = (_readIndex + overflow) % _buffer.Length;
                _available -= overflow;
                DroppedSamples += overflow;
            }

            int firstPart = Math.Min(source.Length, _buffer.Length - _writeIndex);
            source[..firstPart].CopyTo(_buffer.AsSpan(_writeIndex));
            if (firstPart < source.Length)
            {
                source[firstPart..].CopyTo(_buffer);
            }

            _writeIndex = (_writeIndex + source.Length) % _buffer.Length;
            _available += source.Length;
        }
    }

    /// <summary>
    /// Fills <paramref name="destination"/>, padding with silence when
    /// fewer samples are available.
    /// </summary>
    /// <returns>How many real samples were copied before any padding.</returns>
    public int Read(Span<float> destination)
    {
        lock (_sync)
        {
            int copied = Math.Min(_available, destination.Length);

            int firstPart = Math.Min(copied, _buffer.Length - _readIndex);
            _buffer.AsSpan(_readIndex, firstPart).CopyTo(destination);
            if (firstPart < copied)
            {
                _buffer.AsSpan(0, copied - firstPart).CopyTo(destination[firstPart..]);
            }

            _readIndex = copied == 0 ? _readIndex : (_readIndex + copied) % _buffer.Length;
            _available -= copied;

            if (copied < destination.Length)
            {
                destination[copied..].Clear();
                UnderrunSamples += destination.Length - copied;
            }

            return copied;
        }
    }

    public void Clear()
    {
        lock (_sync)
        {
            _readIndex = 0;
            _writeIndex = 0;
            _available = 0;
        }
    }
}
