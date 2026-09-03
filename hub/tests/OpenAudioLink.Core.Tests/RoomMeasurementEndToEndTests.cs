using OpenAudioLink.Core.Audio;
using Xunit;

namespace OpenAudioLink.Core.Tests;

/// <summary>
/// The whole path at its real size and with the real defaults: the sweep
/// that ships, a room, a 24-bit WAV written by the recorder, read back and
/// analysed with nothing overridden.
/// </summary>
/// <remarks>
/// The other tests use a short signal so they run quickly, which means none
/// of them exercises the numbers that actually ship — a 480 000-frame cycle
/// padded to 2^19, a quarter-second alignment margin, a half-second window.
/// Every one of those is a place where a default that is wrong for the real
/// signal would pass every other test in the suite.
/// </remarks>
public class RoomMeasurementEndToEndTests
{
    /// <summary>A peaking filter, applied in place.</summary>
    private static void Filter(double[] audio, double frequency, double q, double gainDb, int rate)
    {
        double a = Math.Pow(10.0, gainDb / 40.0);
        double w = 2.0 * Math.PI * frequency / rate;
        double alpha = Math.Sin(w) / (2.0 * q);
        double cos = Math.Cos(w);

        double b0 = (1 + alpha * a), b1 = -2 * cos, b2 = (1 - alpha * a);
        double a0 = (1 + alpha / a), a1 = -2 * cos, a2 = (1 - alpha / a);
        b0 /= a0; b1 /= a0; b2 /= a0; a1 /= a0; a2 /= a0;

        double x1 = 0, x2 = 0, y1 = 0, y2 = 0;
        for (int n = 0; n < audio.Length; n++)
        {
            double x = audio[n];
            double y = b0 * x + b1 * x1 + b2 * x2 - a1 * y1 - a2 * y2;
            x2 = x1; x1 = x;
            y2 = y1; y1 = y;
            audio[n] = y;
        }
    }

    [Fact]
    public void A_room_mode_survives_the_whole_path_from_wav_to_curve()
    {
        var signal = new SweepSignal();          // exactly what is sent
        int rate = signal.SampleRate;

        // Five cycles, arriving 313 ms late — about what the playout buffer
        // and the network cost — starting 2.7 s into the recording, because
        // nobody presses record on the cycle boundary.
        const int delay = 15_000;
        const int offset = 130_000;
        int frames = 5 * signal.CycleFrames + delay + offset;

        var audio = new double[frames];
        for (int n = offset + delay; n < frames; n++)
        {
            audio[n] = signal.SampleAt(n - offset - delay);
        }

        Filter(audio, frequency: 62, q: 3, gainDb: 9, rate: rate);      // a bass mode
        Filter(audio, frequency: 3500, q: 1.2, gainDb: -5, rate: rate); // a dull tweeter

        // A microphone set so the loudest moment lands at -3 dBFS, which is
        // what the gain dialog is for. The bass mode alone adds 9 dB, so a
        // sweep sent at -6 comes back over full scale without this — which
        // is a real way to ruin a measurement and not a fault in the test.
        double loudest = audio.Max(Math.Abs);
        for (int n = 0; n < frames; n++)
        {
            audio[n] *= 0.707 / loudest;
        }

        // Down the wire as L24 big-endian, and back through the recorder's
        // own writer, so the byte swap is in the path being tested.
        var payload = new byte[frames * 2 * 3];
        for (int n = 0; n < frames; n++)
        {
            int value = (int)Math.Round(Math.Clamp(audio[n], -1.0, 0.9999999) * 8388607.0);
            for (int channel = 0; channel < 2; channel++)
            {
                int at = (n * 2 + channel) * 3;
                payload[at] = (byte)(value >> 16);
                payload[at + 1] = (byte)(value >> 8);
                payload[at + 2] = (byte)value;
            }
        }

        var file = new MemoryStream();
        using (var writer = new WavWriter(file, rate, 2, ownsStream: false))
        {
            writer.WriteL24(payload);
        }
        file.Position = 0;

        var read = WavReader.Read(file);
        Assert.Equal(rate, read.SampleRate);
        Assert.Equal(frames, read.Samples.Length);

        var response = SweepAnalyser.Analyse(read.Samples, signal);

        Assert.Equal(9.0, At(response, 62), 1.5);
        Assert.Equal(-5.0, At(response, 3500), 1.5);
        Assert.Equal(0.0, At(response, 500), 1.0);
        Assert.Equal(0.0, At(response, 12000), 1.5);

        Assert.InRange(response.CyclesAveraged, 3, 5);
        Assert.Equal(0, response.ClippedSamples);
        Assert.Empty(response.Warnings);

        // The direct sound belongs on the alignment margin, and the margin
        // is a quarter of a second because that is what the default says.
        Assert.Equal(0.25, response.AlignMarginSeconds, 3);
        Assert.Equal(0.25, response.ImpulsePeakSeconds, 2);

        Assert.Equal(240, response.FrequenciesHz.Count);
        Assert.Equal(20.0, response.FrequenciesHz[0], 6);
        Assert.Equal(20000.0, response.FrequenciesHz[^1], 6);
    }

    /// <summary>
    /// A room that rings, which is the case the first real measurement
    /// broke on.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Every other test here uses a room whose response is over in a few
    /// milliseconds, and all of them passed while the analyser was getting
    /// a real living room half a second wrong. The coarse alignment looks
    /// for the quiet part of the cycle, and the room is still ringing when
    /// the gap starts — so the quietest window is the one shifted past the
    /// reverberation, and the arrival is reported late by about the decay
    /// time. Late enough and the direct sound falls *before* the analysis
    /// window, which is what the warning caught.
    /// </para>
    /// <para>
    /// The room is built as a real impulse response — a direct arrival
    /// with an exponentially decaying noise tail, coloured by a known
    /// peaking filter — and applied by convolution, so the recording is
    /// the periodic steady state the analyser assumes rather than an
    /// approximation of it.
    /// </para>
    /// </remarks>
    [Fact]
    public void A_room_that_rings_for_half_a_second_still_aligns()
    {
        var signal = new SweepSignal();
        int rate = signal.SampleRate;
        int cycle = signal.CycleFrames;

        // Direct sound 4 ms in, then 0.5 s of decaying noise: a T60 of
        // about 0.45 s, which is an ordinary furnished room.
        const int direct = 192;
        const int tail = 24_000;
        var room = new double[direct + tail];
        var random = new Random(9);
        room[direct] = 1.0;
        for (int n = 1; n < tail; n++)
        {
            room[direct + n] = 0.5 * Math.Exp(-n / 3127.0) * (random.NextDouble() * 2 - 1);
        }
        // Below the 200 Hz-2 kHz band the curve is levelled against, so
        // the levelling cannot absorb part of the very peak being looked
        // for. A bass mode is the thing this feature exists to find.
        Filter(room, frequency: 100, q: 3, gainDb: 8, rate: rate);

        // One cycle of the steady-state response, by convolution. The
        // sweep's own silent gap is longer than the room, so the linear
        // convolution and the periodic one are the same sequence — which
        // is the assumption the analyser rests on, made explicit here.
        int size = Fft.NextPowerOfTwo(cycle);
        var re = new double[size];
        var im = new double[size];
        for (int n = 0; n < cycle; n++)
        {
            re[n] = signal.SampleAt(n);
        }
        Fft.Forward(re, im);

        var roomRe = new double[size];
        var roomIm = new double[size];
        room.CopyTo(roomRe, 0);
        Fft.Forward(roomRe, roomIm);

        for (int k = 0; k < size; k++)
        {
            double productRe = re[k] * roomRe[k] - im[k] * roomIm[k];
            im[k] = re[k] * roomIm[k] + im[k] * roomRe[k];
            re[k] = productRe;
        }
        Fft.Inverse(re, im);

        // Six cycles of it, starting 3.1 s in, because nobody presses
        // record on a cycle boundary.
        const int offset = 150_000;
        int frames = 6 * cycle;
        var audio = new double[frames];
        for (int n = 0; n < frames; n++)
        {
            audio[n] = re[(n + offset) % cycle];
        }

        double loudest = audio.Max(Math.Abs);
        for (int n = 0; n < frames; n++)
        {
            audio[n] *= 0.707 / loudest;
        }

        var response = SweepAnalyser.Analyse(audio, signal);

        Assert.Empty(response.Warnings);
        Assert.Equal(0.25, response.ImpulsePeakSeconds, 2);
        Assert.Equal(8.0, At(response, 100), 2.0);
        Assert.Equal(0.0, At(response, 500), 2.0);
        Assert.Equal(0.0, At(response, 5000), 2.0);
    }

    private static double At(RoomResponse response, double frequency)
    {
        int nearest = 0;
        double best = double.MaxValue;
        for (int p = 0; p < response.FrequenciesHz.Count; p++)
        {
            double distance = Math.Abs(Math.Log(response.FrequenciesHz[p] / frequency));
            if (distance < best)
            {
                best = distance;
                nearest = p;
            }
        }
        return response.MagnitudeDb[nearest];
    }
}
