# XLayer

XLayer is a fully managed MP3 to WAV decoder. 

This repository is my port of [NLayer](https://github.com/naudio/NLayer) to modern .NET

The NLayer code was originally based 
on [JavaLayer](http://www.javazoom.net/javalayer/javalayer.html) (v1.0.1), 
which has been ported to C#.

Was previously hosted at [XLayer.codeplex.com](http://XLayer.codeplex.com/). 
Please see the history there for full details of contributors.

## Usage

To use XLayer for decoding MP3, first reference XLayer.

```cs
using XLayer;
```

Then create an `MpegFile`, pass a file name or a stream to the constructor, and use `ReadSamples` for decoding the content:

```cs
// samples per second times channel count
const int samplesCount = 44100;
var fileName = "myMp3File.mp3";
var mpegFile = new MpegFile(filename);
float[] samples = new float[samplesCount];
int readCount = mpegFile.ReadSamples(samples, 0, samplesCount);
```

More information could be found in code documents.

## Use with NAudio

I have not tested this port with NAudio
