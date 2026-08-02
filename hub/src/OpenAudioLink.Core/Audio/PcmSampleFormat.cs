using System.Buffers.Binary;

namespace OpenAudioLink.Core.Audio;

/// <summary>
/// Sample layouts a pipe-fed player can hand us. The names match
/// librespot's <c>--format</c> values so that what is configured and what
/// is decoded cannot drift apart.
/// </summary>
public enum PcmSampleFormat
{
    /// <summary>16-bit signed integer.</summary>
    S16,

    /// <summary>24-bit signed integer packed into three bytes.</summary>
    S24_3,

    /// <summary>32-bit signed integer.</summary>
    S32,

    /// <summary>32-bit IEEE float, already in the -1..1 range.</summary>
    F32,
}

/// <summary>
/// Turns a raw PCM byte stream into the float samples the rest of the
/// audio path works in.
///
/// Byte order is little-endian because a pipe backend writes the machine's
/// own layout and every machine this Hub runs on is little-endian. Stating
/// it explicitly rather than casting the memory keeps the decision visible
/// if that ever stops being true.
/// </summary>
public static class PcmDecoder
{
    public static int BytesPerSample(PcmSampleFormat format) => format switch
    {
        PcmSampleFormat.S16 => 2,
        PcmSampleFormat.S24_3 => 3,
        PcmSampleFormat.S32 => 4,
        PcmSampleFormat.F32 => 4,
        _ => throw new ArgumentOutOfRangeException(nameof(format)),
    };

    /// <summary>
    /// Decodes as many whole samples as <paramref name="source"/> holds and
    /// <paramref name="destination"/> has room for.
    ///
    /// A stream read from a pipe stops wherever the pipe felt like
    /// stopping, so a trailing partial sample is normal rather than an
    /// error; the caller carries those bytes into the next read.
    /// </summary>
    /// <returns>How many samples were written.</returns>
    public static int Decode(ReadOnlySpan<byte> source, Span<float> destination, PcmSampleFormat format)
    {
        int width = BytesPerSample(format);
        int count = Math.Min(source.Length / width, destination.Length);

        switch (format)
        {
            case PcmSampleFormat.S16:
                for (int i = 0; i < count; i++)
                {
                    destination[i] = BinaryPrimitives.ReadInt16LittleEndian(source[(i * 2)..]) / 32768f;
                }
                break;

            case PcmSampleFormat.S24_3:
                for (int i = 0; i < count; i++)
                {
                    int at = i * 3;
                    // The top byte carries the sign, so it is read signed and
                    // the lower two unsigned; shifting an unsigned byte into
                    // bit 23 and hoping would give a positive number for every
                    // negative sample.
                    int value = source[at] | (source[at + 1] << 8) | ((sbyte)source[at + 2] << 16);
                    destination[i] = value / 8388608f;
                }
                break;

            case PcmSampleFormat.S32:
                for (int i = 0; i < count; i++)
                {
                    destination[i] = BinaryPrimitives.ReadInt32LittleEndian(source[(i * 4)..]) / 2147483648f;
                }
                break;

            case PcmSampleFormat.F32:
                for (int i = 0; i < count; i++)
                {
                    destination[i] = BinaryPrimitives.ReadSingleLittleEndian(source[(i * 4)..]);
                }
                break;

            default:
                throw new ArgumentOutOfRangeException(nameof(format));
        }

        return count;
    }

    public static bool TryParse(string? name, out PcmSampleFormat format)
    {
        format = PcmSampleFormat.S16;
        if (string.IsNullOrWhiteSpace(name))
        {
            return false;
        }

        // "S24_3" is how librespot spells it; accept "S24-3" and "S243" too
        // rather than making someone guess which separator we wanted.
        var normalised = name.Trim().Replace("-", "_").ToUpperInvariant();
        switch (normalised)
        {
            case "S16":
                format = PcmSampleFormat.S16;
                return true;
            case "S24_3":
            case "S243":
                format = PcmSampleFormat.S24_3;
                return true;
            case "S32":
                format = PcmSampleFormat.S32;
                return true;
            case "F32":
                format = PcmSampleFormat.F32;
                return true;
            default:
                return false;
        }
    }

    /// <summary>The spelling to pass to librespot's <c>--format</c>.</summary>
    public static string ToLibrespotName(PcmSampleFormat format) => format switch
    {
        PcmSampleFormat.S16 => "S16",
        PcmSampleFormat.S24_3 => "S24_3",
        PcmSampleFormat.S32 => "S32",
        PcmSampleFormat.F32 => "F32",
        _ => throw new ArgumentOutOfRangeException(nameof(format)),
    };
}
