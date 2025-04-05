using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Diagnosers;
using BenchmarkDotNet.Running;

if (args.Length > 0 && args[0] == "-b")
{
    BenchmarkRunner.Run<Benchmarks>();
    return;
}

NLayer.MpegFile NLayerMpeg = new("the_toons_recording_contract.mp3");
XLayer.MpegFile XLayerMpeg = new("the_toons_recording_contract.mp3");
ArgumentOutOfRangeException.ThrowIfNotEqual(NLayerMpeg!.SampleRate, XLayerMpeg!.SampleRate);
ArgumentOutOfRangeException.ThrowIfNotEqual(NLayerMpeg!.Duration.Seconds, XLayerMpeg!.Duration.Seconds);

int samplesrate = NLayerMpeg!.SampleRate;
float[] samplesN = new float[samplesrate];
float[] samplesX = new float[samplesrate];
int samplesCount = NLayerMpeg.Duration.Seconds * samplesrate;
for (int i = 0; i < samplesCount; i++)
{
    NLayerMpeg!.ReadSamples(samplesN, 0, samplesrate);
    XLayerMpeg!.ReadSamples(samplesX, 0, samplesrate);
    if (samplesN.SequenceEqual(samplesX))
    {
        continue;
    }

    for (int j = 0; j < samplesrate; j++)
    {
        ArgumentOutOfRangeException.ThrowIfNotEqual(samplesN[j], samplesX[j], "Sample mismatch at sample " + j);
    }

    Console.WriteLine("Processing is still Equal!");
}

[MemoryDiagnoser]
[ExceptionDiagnoser]
[ThreadingDiagnoser]
[DisassemblyDiagnoser]
// [SimpleJob(launchCount: 0, warmupCount: 1, iterationCount: 1, invocationCount: 5)]
[MarkdownExporter, HtmlExporter, CsvExporter, RPlotExporter]
public class Benchmarks
{
    const string test1 = @"the_toons_recording_contract.mp3";
    readonly Stream fileStream = File.OpenRead(test1);
    readonly MemoryStream fileStreamMem;

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
            readCount += XLayerMpeg!.ReadSamples(samples, 0, samplesrate);
        }
    }
}