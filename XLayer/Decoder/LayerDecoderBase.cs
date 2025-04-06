/*
 * XLayer - A C# MPEG1/2/2.5 audio decoder
 * 
 * Portions of this file are courtesy Fluendo, S.A.  They are dual licensed as Ms-PL
 * and under the following license:
 *
 *   Copyright <2005-2012> Fluendo S.A.
 *   
 *   Unless otherwise indicated, Source Code is licensed under MIT license.
 *   See further explanation attached in License Statement (distributed in the file
 *   LICENSE).
 *   
 *   Permission is hereby granted, free of charge, to any person obtaining a copy of
 *   this software and associated documentation files (the "Software"), to deal in
 *   the Software without restriction, including without limitation the rights to
 *   use, copy, modify, merge, publish, distribute, sublicense, and/or sell copies
 *   of the Software, and to permit persons to whom the Software is furnished to do
 *   so, subject to the following conditions:
 *   
 *   The above copyright notice and this permission notice shall be included in all
 *   copies or substantial portions of the Software.
 *   
 *   THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR
 *   IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY,
 *   FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE
 *   AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER
 *   LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM,
 *   OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE
 *   SOFTWARE.
 *
 */

using System;
using System.Buffers;
using System.Collections.Generic;

namespace XLayer.Decoder
{
    internal abstract class LayerDecoderBase
    {
        private readonly List<float[]> _synBuf = new(2);
        private readonly List<int> _bufOffset = new(2);
        private float[] _eq;

        internal LayerDecoderBase() => StereoMode = StereoMode.Both;

        ~LayerDecoderBase()
        {
            foreach (var buf in _synBuf)
            {
                if (buf != null) ArrayPool<float>.Shared.Return(buf, clearArray: true);
            }
        }

        abstract internal int DecodeFrame(IMpegFrame frame, Span<float> ch0, Span<float> ch1);

        internal void SetEQ(float[] eq)
        {
            if (eq == null || eq.Length == 32) _eq = eq;
        }

        internal StereoMode StereoMode { get; set; }

        virtual internal void ResetForSeek()
        {
            _synBuf.Clear();
            _bufOffset.Clear();
        }

        protected void InversePolyPhase(int channel, Span<float> data)
        {
            GetBufAndOffset(channel, out Span<float> synBuf, out int k);

            if (_eq != null)
            {
                for (int i = 0; i < 32; i++)
                {
                    data[i] *= _eq.AsSpan()[i];
                }
            }

            Span<float> ippuv = stackalloc float[512];

            DCT32(data, synBuf, k);

            BuildUVec(ippuv, synBuf, k);

            DewindowOutput(ippuv, data);
        }

        private void GetBufAndOffset(int channel, out Span<float> synBuf, out int k)
        {
            while (_synBuf.Count <= channel)
            {
                // _synBuf.Add(new float[1024]);
                _synBuf.Add(ArrayPool<float>.Shared.Rent(1024));
            }

            while (_bufOffset.Count <= channel)
            {
                _bufOffset.Add(0);
            }

            synBuf = _synBuf[channel].AsSpan();
            k = _bufOffset[channel];

            k = (k - 32) & 511;
            _bufOffset[channel] = k;
        }

        private void DCT32(ReadOnlySpan<float> _in, Span<float> _out, int k)
        {
            Span<float> ei32 = stackalloc float[16];
            Span<float> oi32 = stackalloc float[16];
            Span<float> eo32 = stackalloc float[16];
            Span<float> oo32 = stackalloc float[16];

            for (int i = 0; i < 16; i++)
            {
                ei32[i] = _in[i] + _in[31 - i];
                oi32[i] = (_in[i] - _in[31 - i]) * LookupTables.SYNTH_COS64_TABLE[2 * i];
            }

            DCT16(ei32, eo32);
            DCT16(oi32, oo32);

            for (int i = 0; i < 15; i++)
            {
                _out[2 * i + k] = eo32[i];
                _out[2 * i + 1 + k] = oo32[i] + oo32[i + 1];
            }
            _out[30 + k] = eo32[15];
            _out[31 + k] = oo32[15];
        }

        private void DCT16(ReadOnlySpan<float> _in, Span<float> _out)
        {
            Span<float> ei16 = stackalloc float[8];
            Span<float> oi16 = stackalloc float[8];
            Span<float> eo16 = stackalloc float[8];
            Span<float> oo16 = stackalloc float[8];

            for (int i = 0; i < 8; i++)
            {
                float a = _in[i];
                float b = _in[15 - i];
                ei16[i] = a + b;
                oi16[i] = (a - b) * LookupTables.SYNTH_COS64_TABLE[1 + 4 * i];
            }

            DCT8(ei16, eo16);
            DCT8(oi16, oo16);

            for (int i = 0; i < 8; i++)
            {
                _out[2 * i] = eo16[i];
                _out[2 * i + 1] = oo16[i] + (i < 7 ? oo16[i + 1] : 0);
            }
        }

        private void DCT8(ReadOnlySpan<float> _in, Span<float> _out)
        {
            Span<float> ei8 = stackalloc float[4];
            Span<float> tmp8 = stackalloc float[6];
            Span<float> oi8 = stackalloc float[4];
            Span<float> oo8 = stackalloc float[4];

            // Even indices
            ei8[0] = _in[0] + _in[7];
            ei8[1] = _in[3] + _in[4];
            ei8[2] = _in[1] + _in[6];
            ei8[3] = _in[2] + _in[5];

            tmp8[0] = ei8[0] + ei8[1];
            tmp8[1] = ei8[2] + ei8[3];
            tmp8[2] = (ei8[0] - ei8[1]) * LookupTables.SYNTH_COS64_TABLE[7];
            tmp8[3] = (ei8[2] - ei8[3]) * LookupTables.SYNTH_COS64_TABLE[23];
            tmp8[4] = (tmp8[2] - tmp8[3]) * LookupTables.INV_SQRT_2;

            _out[0] = tmp8[0] + tmp8[1];
            _out[2] = tmp8[2] + tmp8[3] + tmp8[4];
            _out[4] = (tmp8[0] - tmp8[1]) * LookupTables.INV_SQRT_2;
            _out[6] = tmp8[4];

            // Odd indices
            oi8[0] = (_in[0] - _in[7]) * LookupTables.SYNTH_COS64_TABLE[3];
            oi8[1] = (_in[1] - _in[6]) * LookupTables.SYNTH_COS64_TABLE[11];
            oi8[2] = (_in[2] - _in[5]) * LookupTables.SYNTH_COS64_TABLE[19];
            oi8[3] = (_in[3] - _in[4]) * LookupTables.SYNTH_COS64_TABLE[27];

            tmp8[0] = oi8[0] + oi8[3];
            tmp8[1] = oi8[1] + oi8[2];
            tmp8[2] = (oi8[0] - oi8[3]) * LookupTables.SYNTH_COS64_TABLE[7];
            tmp8[3] = (oi8[1] - oi8[2]) * LookupTables.SYNTH_COS64_TABLE[23];
            tmp8[4] = tmp8[2] + tmp8[3];
            tmp8[5] = (tmp8[2] - tmp8[3]) * LookupTables.INV_SQRT_2;

            oo8[0] = tmp8[0] + tmp8[1];
            oo8[1] = tmp8[4] + tmp8[5];
            oo8[2] = (tmp8[0] - tmp8[1]) * LookupTables.INV_SQRT_2;
            oo8[3] = tmp8[5];

            _out[1] = oo8[0] + oo8[1];
            _out[3] = oo8[1] + oo8[2];
            _out[5] = oo8[2] + oo8[3];
            _out[7] = oo8[3];
        }

        private static void BuildUVec(Span<float> u_vec, ReadOnlySpan<float> cur_synbuf, int k)
        {
            int uvp = 0;

            for (int j = 0; j < 8; j++)
            {
                for (int i = 0; i < 16; i++)
                {
                    // Copy first 32 elements
                    u_vec[uvp + i] = cur_synbuf[k + i + 16];
                    u_vec[uvp + i + 17] = -cur_synbuf[k + 31 - i];
                }

                // k wraps at the synthesis buffer boundary
                k = (k + 32) & 511;

                for (int i = 0; i < 16; i++)
                {
                    // Copy next 32 elements
                    u_vec[uvp + i + 32] = -cur_synbuf[k + 16 - i];
                    u_vec[uvp + i + 48] = -cur_synbuf[k + i];
                }
                u_vec[uvp + 16] = 0;

                // k wraps at the synthesis buffer boundary
                k = (k + 32) & 511;
                uvp += 64;
            }
        }

        private static void DewindowOutput(Span<float> u_vec, Span<float> samples)
        {
            // Multiply each element with the corresponding dewindow table value.
            for (int i = 0; i < 512; i++)
            {
                u_vec[i] *= LookupTables.DEWINDOW_TABLE[i];
            }

            // Unroll the inner loop for accumulating 16 samples to minimize loop overhead.
            for (int i = 0; i < 32; i++)
            {
                samples[i] =
                      u_vec[i]
                    + u_vec[i + 32]
                    + u_vec[i + 64]
                    + u_vec[i + 96]
                    + u_vec[i + 128]
                    + u_vec[i + 160]
                    + u_vec[i + 192]
                    + u_vec[i + 224]
                    + u_vec[i + 256]
                    + u_vec[i + 288]
                    + u_vec[i + 320]
                    + u_vec[i + 352]
                    + u_vec[i + 384]
                    + u_vec[i + 416]
                    + u_vec[i + 448]
                    + u_vec[i + 480];
            }
        }
    }
}
