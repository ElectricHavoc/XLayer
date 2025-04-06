using System;
using System.Linq;

namespace XLayer.Decoder;

internal class RiffHeaderFrame : FrameBase
{
    internal static RiffHeaderFrame TrySync(uint syncMark) => syncMark == 0x52494646U ? new RiffHeaderFrame() : null;

    private RiffHeaderFrame() { }

    protected override int Validate()
    {
        byte[] buf = new byte[4];

        // we expect this to be the "WAVE" chunk
        if (Read(8, buf) != 4) return -1;
        if (!buf.AsSpan().SequenceEqual("WAVE"u8)) return -1;

        // now the "fmt " chunk
        if (Read(12, buf) != 4) return -1;
        if (!buf.AsSpan().SequenceEqual("fmt "u8)) return -1;

        // we've found the fmt chunk, so look for the data chunk
        int offset = 16;
        while (true)
        {
            // read the length and seek forward
            if (Read(offset, buf) != 4) return -1;
            offset += 4 + BitConverter.ToInt32(buf, 0);

            // get the chunk ID
            if (Read(offset, buf) != 4) return -1;
            offset += 4;

            // if it's not the data chunk, try again
            if (buf.AsSpan().SequenceEqual("data"u8)) break;
        }
        // ... and now we know exactly where the frame ends
        return offset + 4;
    }
}
