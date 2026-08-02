using OpenAudioLink.Core.Audio;
using Xunit;

namespace OpenAudioLink.Core.Tests;

/// <summary>
/// The resampler sits between every Spotify track and every speaker, so a
/// fault here is not subtle: wrong pitch, a channel swap or a burst of
/// noise on each track change. These check the arithmetic that is easy to
/// get wrong and impossible to spot in a serial log.
/// </summary>
public class RationalResamplerTests
{
    private const int Spotify = 44100;
    private const int Stream = 48000;

    private static float[] Resample(RationalResampler resampler, ReadOnlySpan<float> input, int chunkFrames)
    {
        var produced = new List<float>();
        var output = new float[resampler.MaxOutputFrames(chunkFrames) * resampler.Channels];

        int frames = input.Length / resampler.Channels;
        for (int at = 0; at < frames; at += chunkFrames)
        {
            int take = Math.Min(chunkFrames, frames - at);
            int written = resampler.Process(
                input.Slice(at * resampler.Channels, take * resampler.Channels), output);
            produced.AddRange(output[..written]);
        }
        return [.. produced];
    }

    [Fact]
    public void One_second_of_44_1_becomes_one_second_of_48()
    {
        var resampler = new RationalResampler(Spotify, Stream, 2);
        var input = new float[Spotify * 2];

        var output = Resample(resampler, input, 441);

        Assert.Equal(Stream * 2, output.Length);
    }

    /// <summary>
    /// The chunk size a pipe hands us is arbitrary and changes every read,
    /// so the same input split differently must give the same output. If
    /// the phase accounting were per-call rather than carried, this is
    /// where it would show.
    /// </summary>
    [Theory]
    [InlineData(1)]
    [InlineData(7)]
    [InlineData(240)]
    [InlineData(4096)]
    public void Chunking_does_not_change_the_result(int chunkFrames)
    {
        var input = new float[Spotify * 2];
        for (int frame = 0; frame < Spotify; frame++)
        {
            input[frame * 2] = MathF.Sin(2f * MathF.PI * 440f * frame / Spotify);
            input[frame * 2 + 1] = input[frame * 2];
        }

        var reference = Resample(new RationalResampler(Spotify, Stream, 2), input, Spotify);
        var chunked = Resample(new RationalResampler(Spotify, Stream, 2), input, chunkFrames);

        Assert.Equal(reference.Length, chunked.Length);
        for (int i = 0; i < reference.Length; i++)
        {
            Assert.Equal(reference[i], chunked[i]);
        }
    }

    /// <summary>
    /// A sine in must be the same sine out, delayed by the filter's group
    /// delay. This is the test that would catch a wrong ratio: 44.1 played
    /// as 48 is a semitone and a half sharp, which every listener notices
    /// and no counter reports.
    /// </summary>
    [Fact]
    public void A_sine_survives_at_the_same_frequency()
    {
        const double frequency = 997.0;
        var resampler = new RationalResampler(Spotify, Stream, 1);

        var input = new float[Spotify];
        for (int n = 0; n < input.Length; n++)
        {
            input[n] = (float)Math.Sin(2.0 * Math.PI * frequency * n / Spotify);
        }

        var output = Resample(resampler, input, 512);

        double worst = 0.0;
        // Skip the ends: the filter starts with an empty history and the
        // last output frames have not seen their whole window yet.
        for (int n = 2000; n < output.Length - 2000; n++)
        {
            double atInput = (double)n * Spotify / Stream - RationalResampler.TapsPerPhase / 2.0;
            double ideal = Math.Sin(2.0 * Math.PI * frequency * atInput / Spotify);
            worst = Math.Max(worst, Math.Abs(output[n] - ideal));
        }

        Assert.True(worst < 5e-4, $"worst sample error was {worst}");
    }

    /// <summary>
    /// Unity gain at DC. A filter scaled wrongly is quieter or louder by a
    /// fixed amount, which sounds like a volume problem and gets chased in
    /// the wrong place.
    /// </summary>
    [Fact]
    public void Steady_input_comes_out_at_the_same_level()
    {
        var resampler = new RationalResampler(Spotify, Stream, 1);
        var input = new float[Spotify];
        Array.Fill(input, 0.5f);

        var output = Resample(resampler, input, 1024);

        for (int n = 1000; n < output.Length - 1000; n++)
        {
            Assert.Equal(0.5, output[n], 3);
        }
    }

    /// <summary>
    /// Channels are interleaved, so an off-by-one in the history index
    /// swaps or blends left and right. Feeding one channel silence makes
    /// that immediately visible.
    /// </summary>
    [Fact]
    public void Channels_stay_apart()
    {
        var resampler = new RationalResampler(Spotify, Stream, 2);
        var input = new float[Spotify * 2];
        for (int frame = 0; frame < Spotify; frame++)
        {
            input[frame * 2] = MathF.Sin(2f * MathF.PI * 1000f * frame / Spotify);
            input[frame * 2 + 1] = 0f;
        }

        var output = Resample(resampler, input, 480);

        double loudest = 0.0;
        double quietest = 0.0;
        for (int frame = 1000; frame < output.Length / 2 - 1000; frame++)
        {
            loudest = Math.Max(loudest, Math.Abs(output[frame * 2]));
            quietest = Math.Max(quietest, Math.Abs(output[frame * 2 + 1]));
        }

        Assert.True(loudest > 0.9, $"left collapsed to {loudest}");
        Assert.True(quietest < 1e-5, $"right picked up {quietest} from left");
    }

    [Theory]
    [InlineData(1)]
    [InlineData(13)]
    [InlineData(441)]
    [InlineData(1000)]
    public void Never_writes_more_than_it_promised(int chunkFrames)
    {
        var resampler = new RationalResampler(Spotify, Stream, 2);
        int capacity = resampler.MaxOutputFrames(chunkFrames) * 2;
        var input = new float[chunkFrames * 2];
        var output = new float[capacity];

        // Many calls in a row: the output count alternates as the phase
        // walks, so the largest one only appears after a while.
        for (int round = 0; round < 500; round++)
        {
            Assert.True(resampler.Process(input, output) <= capacity);
        }
    }

    [Fact]
    public void Equal_rates_are_reported_as_needing_no_work()
    {
        var resampler = new RationalResampler(Stream, Stream, 2);

        // Callers skip a passthrough rather than run it: the filter would
        // still be a filter, and delaying audio by 32 frames to achieve
        // nothing is not free.
        Assert.True(resampler.IsPassthrough);
        Assert.False(new RationalResampler(Spotify, Stream, 2).IsPassthrough);
    }

    [Fact]
    public void Reset_clears_the_tail_of_the_previous_track()
    {
        var resampler = new RationalResampler(Spotify, Stream, 1);
        var loud = new float[Spotify / 10];
        Array.Fill(loud, 1f);
        Resample(resampler, loud, 512);

        resampler.Reset();

        var silence = new float[RationalResampler.TapsPerPhase];
        var output = new float[resampler.MaxOutputFrames(silence.Length)];
        int written = resampler.Process(silence, output);

        for (int n = 0; n < written; n++)
        {
            Assert.Equal(0f, output[n]);
        }
    }

    [Fact]
    public void Rejects_impossible_arguments()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new RationalResampler(0, Stream, 2));
        Assert.Throws<ArgumentOutOfRangeException>(() => new RationalResampler(Spotify, -1, 2));
        Assert.Throws<ArgumentOutOfRangeException>(() => new RationalResampler(Spotify, Stream, 0));

        var resampler = new RationalResampler(Spotify, Stream, 2);
        Assert.Throws<ArgumentException>(() => resampler.Process(new float[200], new float[4]));
    }
}
