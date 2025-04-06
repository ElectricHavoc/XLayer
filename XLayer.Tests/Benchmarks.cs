using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Diagnosers;

namespace XLayer.Tests;

[MemoryDiagnoser]
[MarkdownExporter]
public class Benchmarks : IDisposable
{
    private const string test1 = @"the_toons_recording_contract.mp3";
    private readonly Stream fileStream = File.OpenRead(test1);
    private readonly MemoryStream fileStreamMem;

    public Benchmarks()
    {
        fileStreamMem = new MemoryStream(File.ReadAllBytes(test1));
        fileStream.Dispose();
    }

    [IterationSetup]
    public void ResetPointer()
    {
        fileStreamMem.Position = 0;
    }

    [Benchmark(Baseline = true)]
    public void NLayer()
    {
        NLayer.MpegFile NLayerMpeg = new(fileStreamMem);
        int samplesrate = NLayerMpeg!.SampleRate;
        float[] samples = new float[samplesrate];
        int samplesCount = NLayerMpeg.Duration.Seconds * samplesrate;
        int readCount = 0;
        for (int i = 0; i < samplesCount; i++)
        {
            readCount += NLayerMpeg!.ReadSamples(samples, 0, samplesrate);
        }
    }

    [Benchmark]
    public void XLayer()
    {
        XLayer.MpegFile XLayerMpeg = new(fileStreamMem);
        int samplesrate = XLayerMpeg!.SampleRate;
        float[] samples = new float[samplesrate];
        int samplesCount = XLayerMpeg.Duration.Seconds * samplesrate;
        int readCount = 0;
        for (int i = 0; i < samplesCount; i++)
        {
            readCount += XLayerMpeg!.ReadSamples(samples);
        }
    }

    public void Dispose()
    {
        GC.SuppressFinalize(this);
        fileStreamMem.Dispose();
    }
}