using System.Diagnostics;
using OpenScanner.Server.DSP;

// Channelizer throughput benchmark.
//
// Feeds N channelizers the same wideband u8 IQ stream in the 64 KiB chunks the
// parallel capture loop reads from rtl_sdr, and reports how many seconds of IQ
// are processed per wall-clock second ("x realtime"). Anything below 1x drops
// samples on the device.
//
// Usage: dotnet run -c Release --project server/OpenScanner.Bench -- [channels] [seconds] [sampleRate]

int channels = args.Length > 0 ? int.Parse(args[0]) : 8;
double seconds = args.Length > 1 ? double.Parse(args[1]) : 5;
int sampleRate = args.Length > 2 ? int.Parse(args[2]) : 2_400_000;
const int ChunkBytes = 16384 * 4; // matches RtlDevice.RunParallelCapture

// Synthetic IQ: a few FM carriers plus noise, so the demod paths do real work.
var rng = new Random(1234);
int totalBytes = (int)(sampleRate * seconds) * 2;
totalBytes -= totalBytes % ChunkBytes;
var iq = new byte[totalBytes];
double p1 = 0, p2 = 0, mod = 0;
for (int i = 0; i < totalBytes / 2; i++)
{
    mod += 2 * Math.PI * 1000 / sampleRate;
    p1 += 2 * Math.PI * (150_000 + 3000 * Math.Sin(mod)) / sampleRate;
    p2 += 2 * Math.PI * (-420_000 + 2500 * Math.Sin(mod * 1.7)) / sampleRate;
    double re = 40 * Math.Cos(p1) + 30 * Math.Cos(p2) + rng.NextDouble() * 20 - 10;
    double im = 40 * Math.Sin(p1) + 30 * Math.Sin(p2) + rng.NextDouble() * 20 - 10;
    iq[i * 2] = (byte)Math.Clamp(127.5 + re, 0, 255);
    iq[i * 2 + 1] = (byte)Math.Clamp(127.5 + im, 0, 255);
}

Channelizer[] Make() => Enumerable.Range(0, channels)
    .Select(c => new Channelizer(sampleRate, 48000,
        -900_000 + c * (1_800_000.0 / Math.Max(1, channels - 1)), amMode: c % 4 == 3))
    .ToArray();

double Run(string label, Channelizer[] chans, bool parallel)
{
    var outs = chans.Select(c => new byte[c.MaxOutputBytes(ChunkBytes)]).ToArray();
    var chunk = new byte[ChunkBytes];

    // Warm up (JIT tiering) on one second of data.
    for (int off = 0; off < Math.Min(totalBytes, sampleRate * 2); off += ChunkBytes)
        for (int c = 0; c < chans.Length; c++)
            chans[c].ProcessIQ(iq.AsSpan(off, ChunkBytes).ToArray(), ChunkBytes, outs[c]);

    var sw = Stopwatch.StartNew();
    for (int off = 0; off < totalBytes; off += ChunkBytes)
    {
        Buffer.BlockCopy(iq, off, chunk, 0, ChunkBytes);
        if (parallel)
            Parallel.For(0, chans.Length, c => chans[c].ProcessIQ(chunk, ChunkBytes, outs[c]));
        else
            for (int c = 0; c < chans.Length; c++)
                chans[c].ProcessIQ(chunk, ChunkBytes, outs[c]);
    }
    sw.Stop();

    double realtime = seconds / sw.Elapsed.TotalSeconds;
    double perChanMs = sw.Elapsed.TotalMilliseconds / seconds / chans.Length;
    Console.WriteLine(
        $"{label,-12} {chans.Length,2} ch  {sw.Elapsed.TotalMilliseconds,8:F0} ms for {seconds}s IQ  " +
        $"{realtime,6:F2}x realtime  ({perChanMs:F1} ms CPU-wall per channel-second)");
    return realtime;
}

Console.WriteLine($"Channelizer bench: {sampleRate / 1e6:F1} MSPS, {seconds}s, {Environment.ProcessorCount} cores");
Run("sequential", Make(), parallel: false);
Run("parallel", Make(), parallel: true);
