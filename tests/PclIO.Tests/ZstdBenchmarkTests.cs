using System.Diagnostics;
using PclIO.Internal;
using Xunit;
using Xunit.Abstractions;

namespace PclIO.Tests;

/// <summary>Benchmark for the managed Zstandard decoder: decodes a large concatenated payload made of reference
/// compressor frames, asserts the output and reports the throughput. The output (not the time) is asserted, so the
/// test stays green on any machine; run it in Release and read the MB/s from the test output.</summary>
public sealed class ZstdBenchmarkTests
{
    private readonly ITestOutputHelper _output;

    public ZstdBenchmarkTests(ITestOutputHelper output) => _output = output;

    // Large frames that together exercise raw blocks, RLE blocks, Huffman literals (single and four streams),
    // treeless blocks, repeat offsets and far matches.
    private static readonly string[] PayloadFrames =
    [
        "MultiBlock", "MultiBlockLevel19", "TreelessLiterals", "HuffmanLargeLiterals",
        "RepeatOffsets", "FarMatch", "TextLevel19", "MixedContent",
    ];

    private static byte[] FrameOf(string name) =>
        Convert.FromBase64String(ZstdVectors.All.Single(v => v.Name == name).Base64);

    [Fact]
    public void Decode_Throughput_Of_A_Large_Concatenated_Payload()
    {
#if DEBUG
        const int repeats = 1;
        const int timedRuns = 1;
#else
        const int repeats = 144;
        const int timedRuns = 5;
#endif
        byte[][] frames = [.. PayloadFrames.Select(FrameOf)];
        byte[] input = [.. frames.SelectMany(f => Enumerable.Repeat(f, repeats)).SelectMany(f => f)];

        // The payload concatenates whole frames, so the content is the frames' contents one after another.
        var contents = new List<byte>();
        foreach (string name in PayloadFrames)
            for (int i = 0; i < repeats; i++)
                contents.AddRange(ZstdDecoderTests.ContentOf(name));
        byte[] expected = [.. contents];

        // Correctness first: the benchmark is only meaningful when the output is right.
        Assert.Equal(expected, ZstdDecoder.Decompress(input, expected.Length));

        // The reference decoder (a C# port of zstd used only by this benchmark) must agree on the same input.
        byte[] reference;
        using (var decompressor = new ZstdSharp.Decompressor())
            reference = decompressor.Unwrap(input).ToArray();
        Assert.Equal(expected, reference);

        var elapsed = new List<TimeSpan>(timedRuns);
        var referenceElapsed = new List<TimeSpan>(timedRuns);
        for (int i = 0; i < timedRuns; i++)
        {
            // Alternate the order so that neither decoder always runs on the colder CPU.
            if (i % 2 == 0)
            {
                elapsed.Add(TimeOurs(input, expected.Length));
                referenceElapsed.Add(TimeReference(input, expected.Length));
            }
            else
            {
                referenceElapsed.Add(TimeReference(input, expected.Length));
                elapsed.Add(TimeOurs(input, expected.Length));
            }
        }

        TimeSpan best = elapsed.Min();
        double seconds = best.TotalSeconds;
        TimeSpan referenceBest = referenceElapsed.Min();
        double referenceSeconds = referenceBest.TotalSeconds;
        _output.WriteLine($"Input: {input.Length / (1024.0 * 1024):F1} MiB in {elapsed.Count} runs, content {expected.Length / (1024.0 * 1024):F1} MiB.");
        _output.WriteLine($"Best decode, ours: {best.TotalMilliseconds:F0} ms, {input.Length / (1024.0 * 1024) / seconds:F1} MiB/s in, {expected.Length / (1024.0 * 1024) / seconds:F1} MiB/s out.");
        _output.WriteLine($"Best decode, reference: {referenceBest.TotalMilliseconds:F0} ms, {input.Length / (1024.0 * 1024) / referenceSeconds:F1} MiB/s in, {expected.Length / (1024.0 * 1024) / referenceSeconds:F1} MiB/s out.");
    }

    private static TimeSpan TimeOurs(byte[] input, int expectedLength)
    {
        GC.Collect(GC.MaxGeneration, GCCollectionMode.Forced, blocking: true);
        var watch = Stopwatch.StartNew();
        byte[] actual = ZstdDecoder.Decompress(input, expectedLength);
        watch.Stop();
        Assert.Equal(expectedLength, actual.Length);
        return watch.Elapsed;
    }

    private static TimeSpan TimeReference(byte[] input, int expectedLength)
    {
        GC.Collect(GC.MaxGeneration, GCCollectionMode.Forced, blocking: true);
        var watch = Stopwatch.StartNew();
        byte[] reference;
        using (var decompressor = new ZstdSharp.Decompressor())
            reference = decompressor.Unwrap(input).ToArray();
        watch.Stop();
        Assert.Equal(expectedLength, reference.Length);
        return watch.Elapsed;
    }
}
