using System;
using System.Buffers;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace XLayer
{
    public class MpegFrameDecoder
    {
        private const int floatSize = sizeof(float);
        private Decoder.LayerIDecoder _layerIDecoder;
        private Decoder.LayerIIDecoder _layerIIDecoder;
        private Decoder.LayerIIIDecoder _layerIIIDecoder;

        private float[] _eqFactors;

        // channel buffers for getting data out of the decoders...
        // we do it this way so the stereo interleaving code is in one place: DecodeFrameImpl(...)
        // if we ever add support for multi-channel, we'll have to add a pass after the initial
        // stereo decode (since multi-channel basically uses the stereo channels as a reference)
        private readonly float[] _ch0;
        private readonly float[] _ch1;

        public MpegFrameDecoder()
        {
            _ch0 = ArrayPool<float>.Shared.Rent(1152);
            _ch1 = ArrayPool<float>.Shared.Rent(1152);
        }

        ~MpegFrameDecoder()
        {
            ArrayPool<float>.Shared.Return(_ch0);
            ArrayPool<float>.Shared.Return(_ch1);
        }

        public void SetEQ(ReadOnlySpan<float> eq)
        {
            if (!eq.IsEmpty)
            {
                Span<float> factors = stackalloc float[32];
                int length = Math.Min(eq.Length, 32);
                for (int i = 0; i < length; i++)
                {
                    // convert from dB -> scaling
                    factors[i] = (float)Math.Pow(2, eq[i] / 6);
                }
                _eqFactors = factors.ToArray();
            }
            else
            {
                _eqFactors = null;
            }
        }

        public StereoMode StereoMode { get; set; }

        internal int DecodeFrame(IMpegFrame frame, Span<float> dest, int destOffset)
        {
            frame.Reset();

            Decoder.LayerDecoderBase curDecoder = null;
            switch (frame.Layer)
            {
                case MpegLayer.LayerI:
                    _layerIDecoder ??= new Decoder.LayerIDecoder();
                    curDecoder = _layerIDecoder;
                    break;
                case MpegLayer.LayerII:
                    _layerIIDecoder ??= new Decoder.LayerIIDecoder();
                    curDecoder = _layerIIDecoder;
                    break;
                case MpegLayer.LayerIII:
                    _layerIIIDecoder ??= new Decoder.LayerIIIDecoder();
                    curDecoder = _layerIIIDecoder;
                    break;
            }

            if (curDecoder != null)
            {
                curDecoder.SetEQ(_eqFactors);
                curDecoder.StereoMode = StereoMode;

                int cnt = curDecoder.DecodeFrame(frame, _ch0, _ch1);

                if (frame.ChannelMode == MpegChannelMode.Mono)
                {
                    _ch0.AsSpan()[..cnt].CopyTo(dest[destOffset..]);
                }
                else
                {
                    for (int i = 0; i < cnt; i++)
                    {
                        dest[destOffset++] = _ch0[i];
                        dest[destOffset++] = _ch1[i];
                    }

                    cnt *= 2;
                }

                return cnt;
            }

            return 0;
        }

        /// <summary>
        /// Reset the decoder.
        /// </summary>
        public void Reset()
        {
            // the synthesis filters need to be cleared
            _layerIDecoder?.ResetForSeek();
            _layerIIDecoder?.ResetForSeek();
            _layerIIIDecoder?.ResetForSeek();
        }
    }
}
