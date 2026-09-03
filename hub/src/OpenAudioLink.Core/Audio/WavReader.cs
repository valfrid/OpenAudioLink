using System.Buffers.Binary;
using System.Text;

namespace OpenAudioLink.Core.Audio;

/// <summary>One channel of a WAV file, as samples in [-1, 1).</summary>
public sealed record WavAudio(int SampleRate, int Channels, double[] Samples, double PeakLevel, long ClippedSamples)
{
    public TimeSpan Duration => TimeSpan.FromSeconds((double)Samples.Length / SampleRate);

    /// <summary>Peak in dBFS, or negative infinity for a silent file.</summary>
    public double PeakDbFs => PeakLevel <= 0 ? double.NegativeInfinity : 20.0 * Math.Log10(PeakLevel);
}

/// <summary>
/// Reads one channel out of a PCM WAV file.
/// </summary>
/// <remarks>
/// <para>
/// The counterpart of <see cref="WavWriter"/>, and deliberately narrow: it
/// exists so the analyser can open a recording this Hub made, not so that
/// OpenAudioLink can open arbitrary audio files. 16- and 24-bit linear PCM
/// is what the recorder writes and what a phone's voice memo exports to
/// after a conversion, which covers every file that has been useful so far.
/// </para>
/// <para>
/// It reports the peak level and how many samples reached full scale along
/// with the audio, because the first question about any measurement
/// recording is whether it clipped — a clipped sweep measures the clipping,
/// and the resulting curve looks like a real one.
/// </para>
/// </remarks>
public static class WavReader
{
    /// <summary>
    /// Reads <paramref name="channel"/> (0-based) from a WAV file.
    /// </summary>
    /// <exception cref="InvalidDataException">
    /// Not a PCM WAV file this can read. Said plainly rather than returning
    /// silence, which is what a measurement of the wrong file looks like.
    /// </exception>
    public static WavAudio Read(Stream stream, int channel = 0)
    {
        ArgumentNullException.ThrowIfNull(stream);

        using var reader = new BinaryReader(stream, Encoding.ASCII, leaveOpen: true);

        if (Tag(reader) != "RIFF")
        {
            throw new InvalidDataException("Not a RIFF file.");
        }
        reader.ReadUInt32();                       // riff size, not trusted
        if (Tag(reader) != "WAVE")
        {
            throw new InvalidDataException("Not a WAVE file.");
        }

        int format = 0, channels = 0, sampleRate = 0, bits = 0;
        byte[]? data = null;

        // Chunk walk rather than fixed offsets: files from other tools carry
        // LIST, fact and JUNK chunks before the data, and reading at byte 44
        // gets a header where the audio should be.
        while (stream.Position + 8 <= stream.Length)
        {
            string id = Tag(reader);
            uint size = reader.ReadUInt32();
            long next = stream.Position + size + (size % 2);   // chunks are word-aligned

            if (id == "fmt ")
            {
                var fmt = reader.ReadBytes((int)Math.Min(size, 40));
                if (fmt.Length < 16)
                {
                    throw new InvalidDataException("The format chunk is too short to describe anything.");
                }
                format = BinaryPrimitives.ReadUInt16LittleEndian(fmt);
                channels = BinaryPrimitives.ReadUInt16LittleEndian(fmt.AsSpan(2));
                sampleRate = (int)BinaryPrimitives.ReadUInt32LittleEndian(fmt.AsSpan(4));
                bits = BinaryPrimitives.ReadUInt16LittleEndian(fmt.AsSpan(14));

                // WAVE_FORMAT_EXTENSIBLE keeps the real format in the
                // sub-format GUID, whose first two bytes are the tag.
                if (format == 0xFFFE && fmt.Length >= 26)
                {
                    format = BinaryPrimitives.ReadUInt16LittleEndian(fmt.AsSpan(24));
                }
            }
            else if (id == "data")
            {
                data = reader.ReadBytes((int)size);
            }

            if (stream.Position != next)
            {
                stream.Position = Math.Min(next, stream.Length);
            }
        }

        if (format != 1)
        {
            throw new InvalidDataException(
                $"Only linear PCM is supported; this file is format {format}.");
        }
        if (channels <= 0 || sampleRate <= 0)
        {
            throw new InvalidDataException("The file has no usable format chunk.");
        }
        if (bits != 16 && bits != 24 && bits != 32)
        {
            throw new InvalidDataException($"{bits}-bit PCM is not supported.");
        }
        if (data is null)
        {
            throw new InvalidDataException("The file has no data chunk.");
        }
        if (channel < 0 || channel >= channels)
        {
            throw new ArgumentOutOfRangeException(nameof(channel),
                $"The file has {channels} channel(s); there is no channel {channel}.");
        }

        int bytes = bits / 8;
        int frameBytes = bytes * channels;
        int frames = data.Length / frameBytes;
        var samples = new double[frames];

        double full = bits switch { 16 => 32768.0, 24 => 8388608.0, _ => 2147483648.0 };
        double peak = 0;
        long clipped = 0;

        for (int i = 0; i < frames; i++)
        {
            int at = i * frameBytes + channel * bytes;
            int value = bits switch
            {
                16 => BinaryPrimitives.ReadInt16LittleEndian(data.AsSpan(at)),
                24 => (data[at] | (data[at + 1] << 8) | ((sbyte)data[at + 2] << 16)),
                _ => BinaryPrimitives.ReadInt32LittleEndian(data.AsSpan(at)),
            };

            samples[i] = value / full;
            double magnitude = Math.Abs(samples[i]);
            if (magnitude > peak)
            {
                peak = magnitude;
            }
            if (magnitude >= 1.0 - 1.0 / full)
            {
                clipped++;
            }
        }

        return new WavAudio(sampleRate, channels, samples, peak, clipped);
    }

    /// <summary>Reads a file by name, which is what the analyser is given.</summary>
    public static WavAudio Read(string path, int channel = 0)
    {
        using var stream = File.OpenRead(path);
        return Read(stream, channel);
    }

    private static string Tag(BinaryReader reader) => Encoding.ASCII.GetString(reader.ReadBytes(4));
}
