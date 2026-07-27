using OpenAudioLink.Core.Audio;
using Xunit;

namespace OpenAudioLink.Core.Tests;

public class AudioRingBufferTests
{
    [Fact]
    public void Round_trips_samples_in_order()
    {
        var buffer = new AudioRingBuffer(16);
        buffer.Write([1f, 2f, 3f, 4f]);

        var read = new float[4];
        Assert.Equal(4, buffer.Read(read));
        Assert.Equal(new[] { 1f, 2f, 3f, 4f }, read);
        Assert.Equal(0, buffer.Available);
    }

    [Fact]
    public void Wraps_around_the_end_of_the_buffer()
    {
        var buffer = new AudioRingBuffer(8);
        buffer.Write([1f, 2f, 3f, 4f, 5f, 6f]);
        buffer.Read(new float[5]);          // read index now near the end
        buffer.Write([7f, 8f, 9f, 10f]);    // write wraps past the end

        var read = new float[5];
        Assert.Equal(5, buffer.Read(read));
        Assert.Equal(new[] { 6f, 7f, 8f, 9f, 10f }, read);
    }

    [Fact]
    public void Reader_falling_behind_gets_silence_and_counts_underrun()
    {
        var buffer = new AudioRingBuffer(16);
        buffer.Write([1f, 2f]);

        var read = new float[5];
        Assert.Equal(2, buffer.Read(read));
        Assert.Equal(new[] { 1f, 2f, 0f, 0f, 0f }, read);
        Assert.Equal(3, buffer.UnderrunSamples);
    }

    [Fact]
    public void Empty_buffer_reads_as_silence()
    {
        var buffer = new AudioRingBuffer(8);

        var read = new float[4];
        Assert.Equal(0, buffer.Read(read));
        Assert.All(read, s => Assert.Equal(0f, s));
        Assert.Equal(4, buffer.UnderrunSamples);
    }

    /// <summary>
    /// Live audio must stay current, so an overrun discards the oldest
    /// samples rather than rejecting the newest.
    /// </summary>
    [Fact]
    public void Writer_getting_ahead_drops_the_oldest_samples()
    {
        var buffer = new AudioRingBuffer(4);
        buffer.Write([1f, 2f, 3f]);
        buffer.Write([4f, 5f]);

        var read = new float[4];
        Assert.Equal(4, buffer.Read(read));
        Assert.Equal(new[] { 2f, 3f, 4f, 5f }, read);
        Assert.Equal(1, buffer.DroppedSamples);
    }

    [Fact]
    public void Write_larger_than_capacity_keeps_only_the_newest()
    {
        var buffer = new AudioRingBuffer(3);
        buffer.Write([1f, 2f, 3f, 4f, 5f]);

        var read = new float[3];
        Assert.Equal(3, buffer.Read(read));
        Assert.Equal(new[] { 3f, 4f, 5f }, read);
        Assert.Equal(2, buffer.DroppedSamples);
    }

    [Fact]
    public void Clear_discards_pending_samples()
    {
        var buffer = new AudioRingBuffer(8);
        buffer.Write([1f, 2f, 3f]);
        buffer.Clear();

        Assert.Equal(0, buffer.Available);
        Assert.Equal(0, buffer.Read(new float[2]));
    }

    [Fact]
    public void Concurrent_write_and_read_lose_no_samples()
    {
        var buffer = new AudioRingBuffer(4096);
        const int total = 100_000;

        var writer = Task.Run(() =>
        {
            var chunk = new float[64];
            for (int i = 0; i < total / chunk.Length; i++)
            {
                Array.Fill(chunk, 1f);
                buffer.Write(chunk);
                Thread.SpinWait(20);
            }
        });

        long real = 0;
        var reader = Task.Run(() =>
        {
            var chunk = new float[48];
            var deadline = DateTime.UtcNow.AddSeconds(10);
            while (!writer.IsCompleted || buffer.Available > 0)
            {
                real += buffer.Read(chunk);
                if (DateTime.UtcNow > deadline)
                {
                    break;
                }
            }
        });

        Assert.True(Task.WaitAll([writer, reader], TimeSpan.FromSeconds(15)));

        // Every sample is either delivered or explicitly accounted for as
        // dropped; none may vanish silently.
        Assert.Equal(total, real + buffer.DroppedSamples + buffer.Available);
    }

    [Fact]
    public void Capacity_must_be_positive()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new AudioRingBuffer(0));
    }
}
