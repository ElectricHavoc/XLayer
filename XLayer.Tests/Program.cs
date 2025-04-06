using System.Diagnostics;
using BenchmarkDotNet.Running;
using XLayer.Tests;

if (args.Length > 0 && args[0] == "-b")
{
    BenchmarkRunner.Run<Benchmarks>();
    return;
}

if (args.Length > 0 && args[0] == "-m")
{
    Benchmarks benchmarks = new();
    benchmarks.XLayer();
    return;
}

if (args.Length > 0 && args[0] == "-p")
{
    File.Delete("__ffmpegtest__.mp3");
    ProcessStartInfo psi = new()
    {
        // You should have ffmpg in your PATH or specify the full path to ffmpeg.exe
        FileName = "ffmpeg",
        Arguments = "-hide_banner -f f32le -ar 44100 -ac 2 -i pipe:0 -f mp3 __ffmpegtest__.mp3",
        RedirectStandardInput = true,
        RedirectStandardOutput = true,
        RedirectStandardError = true,
        UseShellExecute = false,
        CreateNoWindow = true,
    };

    using Process process = new()
    {
        StartInfo = psi,
        EnableRaisingEvents = true
    };

    process.Start();
    
    using Stream input = process.StandardInput.BaseStream;
    process.BeginOutputReadLine();
    process.BeginErrorReadLine();
    XLayer.MpegFile decoder = new("the_toons_recording_contract.mp3");

    Span<float> samples = stackalloc float[decoder.SampleRate];
    Console.WriteLine($"Writing to ffmpeg {decoder.SampleRate} samples per second, {decoder.Channels} channels, {decoder.Length} total samples");
    Console.WriteLine($"Writing to ffmpeg {decoder.SampleRate * decoder.Channels} bytes per second, {decoder.Length * decoder.Channels * sizeof(float)} total bytes");
    while (decoder.Position < decoder.Length)
    {
        int i = decoder.ReadSamples(samples);
        byte[] bytes = new byte[samples.Length * sizeof(float)];
        System.Runtime.InteropServices.MemoryMarshal.AsBytes(samples).CopyTo(bytes);
        input.Write(bytes, 0, bytes.Length);
    }
    input.Close();
    process.Close();
    Console.WriteLine("Finished writing to ffmpeg, waiting for process to exit...");
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
    XLayerMpeg!.ReadSamples(samplesX);
    if (samplesN.SequenceEqual(samplesX))
    {
        continue;
    }

    for (int j = 0; j < samplesrate; j++)
    {
        ArgumentOutOfRangeException.ThrowIfNotEqual(samplesN[j], samplesX[j], "Sample mismatch at sample " + j);
    }
}

Console.WriteLine("Processing is still Equal!");
