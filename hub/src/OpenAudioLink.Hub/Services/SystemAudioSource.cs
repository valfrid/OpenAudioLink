using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using NAudio.CoreAudioApi;
using NAudio.Wave;
using OpenAudioLink.Core.Audio;

namespace OpenAudioLink.Hub.Services;

/// <summary>
/// Captures whatever is playing on the Windows default output device
/// using WASAPI loopback (docs/WINDOWS-AUDIO-CAPTURE.md).
///
/// Loopback attaches to a normal render endpoint, so local playback is
/// unaffected — the PC keeps its own sound while the receivers get a
/// copy. No virtual audio device is involved.
///
/// Capture is push-based and runs on its own thread, while the RTP
/// sender pulls on a paced loop, so the two are bridged by a ring buffer.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class SystemAudioSource : IAudioSource
{
    private readonly WasapiLoopbackCapture _capture;
    private readonly AudioRingBuffer _buffer;
    private readonly int _sourceChannels;
    private readonly int _targetChannels;

    public SystemAudioSource(AudioStreamFormat format)
    {
        ArgumentNullException.ThrowIfNull(format);

        _capture = new WasapiLoopbackCapture();
        var endpoint = _capture.WaveFormat;

        // Resampling is not implemented yet, so a mismatch is reported
        // rather than silently producing wrong-pitch audio. Windows lets
        // the endpoint rate be set in Sound settings.
        if (endpoint.SampleRate != format.SampleRate)
        {
            _capture.Dispose();
            throw new NotSupportedException(
                $"The Windows output device is running at {endpoint.SampleRate} Hz but the stream needs " +
                $"{format.SampleRate} Hz. Set the device to {format.SampleRate} Hz in Windows Sound " +
                "settings (Device properties -> Advanced), or choose a different output device.");
        }

        if (endpoint.Encoding is not (WaveFormatEncoding.IeeeFloat or WaveFormatEncoding.Pcm))
        {
            _capture.Dispose();
            throw new NotSupportedException(
                $"Unsupported capture format {endpoint.Encoding} at {endpoint.BitsPerSample} bits.");
        }

        _sourceChannels = endpoint.Channels;
        _targetChannels = format.Channels;

        // Half a second of slack: enough to ride out scheduling hiccups
        // without adding meaningful latency, since the sender drains it
        // every packet.
        _buffer = new AudioRingBuffer(format.SampleRate * format.Channels / 2);

        Description = $"System audio ({endpoint.SampleRate} Hz, {endpoint.Channels} ch)";

        _capture.DataAvailable += OnDataAvailable;
        _capture.StartRecording();
    }

    public string Description { get; }

    public long DroppedSamples => _buffer.DroppedSamples;

    /// <inheritdoc />
    public long UnderrunSamples => _buffer.UnderrunSamples;

    private void OnDataAvailable(object? sender, WaveInEventArgs args)
    {
        if (args.BytesRecorded == 0)
        {
            return;
        }

        var format = _capture.WaveFormat;
        var raw = args.Buffer.AsSpan(0, args.BytesRecorded);

        float[]? rented = null;
        try
        {
            ReadOnlySpan<float> samples = format.Encoding == WaveFormatEncoding.IeeeFloat
                ? MemoryMarshal.Cast<byte, float>(raw)
                : ConvertPcm(raw, format.BitsPerSample, ref rented);

            if (_sourceChannels == _targetChannels)
            {
                _buffer.Write(samples);
            }
            else
            {
                _buffer.Write(Remix(samples, ref rented));
            }
        }
        catch (Exception)
        {
            // A malformed buffer must not kill the capture thread; the
            // stream continues and the gap shows up as an underrun.
        }
    }

    private static ReadOnlySpan<float> ConvertPcm(ReadOnlySpan<byte> raw, int bitsPerSample, ref float[]? rented)
    {
        if (bitsPerSample != 16)
        {
            throw new NotSupportedException($"Unsupported PCM depth {bitsPerSample}.");
        }

        var source = MemoryMarshal.Cast<byte, short>(raw);
        rented = new float[source.Length];
        for (int i = 0; i < source.Length; i++)
        {
            rented[i] = source[i] / 32768f;
        }
        return rented;
    }

    /// <summary>
    /// Maps the endpoint's channel count onto the stream's: mono is
    /// duplicated to both sides, and surround layouts keep the front
    /// pair, which is the correct downmix for the first two WAVE channels.
    /// </summary>
    private ReadOnlySpan<float> Remix(ReadOnlySpan<float> samples, ref float[]? rented)
    {
        int frames = samples.Length / _sourceChannels;
        var mixed = new float[frames * _targetChannels];

        for (int frame = 0; frame < frames; frame++)
        {
            int from = frame * _sourceChannels;
            int to = frame * _targetChannels;
            for (int channel = 0; channel < _targetChannels; channel++)
            {
                mixed[to + channel] = _sourceChannels == 1
                    ? samples[from]
                    : samples[from + Math.Min(channel, _sourceChannels - 1)];
            }
        }

        rented = mixed;
        return mixed;
    }

    public void ReadFrames(Span<float> destination) => _buffer.Read(destination);

    public void Dispose()
    {
        _capture.DataAvailable -= OnDataAvailable;
        try
        {
            _capture.StopRecording();
        }
        catch (Exception)
        {
            // Stopping an already-stopped or removed device is not an error here.
        }
        _capture.Dispose();
    }
}
