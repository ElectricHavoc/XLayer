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
using System.Runtime.CompilerServices;

namespace XLayer.Decoder
{
    internal sealed partial class LayerIIIDecoder
    {
        // This class is based on the Fluendo hybrid logic.
        private class HybridMDCT
        {
            private const float PI = (float)Math.PI;

            private static readonly float[][] _swin;

            static HybridMDCT()
            {
                _swin = new float[4][];
                for (int i = 0; i < 4; i++)
                {
                    // _swin[i] = new float[36];
                    _swin[i] = ArrayPool<float>.Shared.Rent(36);
                }

                try
                {
                    Span<float> swin0 = _swin[0];
                    Span<float> swin1 = _swin[1];
                    Span<float> swin2 = _swin[2];
                    Span<float> swin3 = _swin[3];

                    const float piOver36 = PI / 36;
                    const float piOver12 = PI / 12;

                    // Type 0
                    for (int i = 0; i < 36; i++)
                    {
                        swin0[i] = (float)Math.Sin(piOver36 * (i + 0.5));
                    }

                    // Type 1
                    for (int i = 0; i < 18; i++)
                    {
                        swin1[i] = (float)Math.Sin(piOver36 * (i + 0.5));
                    }
                    swin1.Slice(18, 6).Fill(1.0f);
                    for (int i = 24; i < 30; i++)
                    {
                        swin1[i] = (float)Math.Sin(piOver12 * (i + 0.5 - 18));
                    }
                    swin1.Slice(30, 6).Clear();

                    // Type 3
                    swin3[..6].Clear();
                    for (int i = 6; i < 12; i++)
                    {
                        swin3[i] = (float)Math.Sin(piOver12 * (i + 0.5 - 6));
                    }
                    swin3.Slice(12, 6).Fill(1.0f);
                    for (int i = 18; i < 36; i++)
                    {
                        swin3[i] = (float)Math.Sin(piOver36 * (i + 0.5));
                    }

                    // Type 2
                    for (int i = 0; i < 12; i++)
                    {
                        swin2[i] = (float)Math.Sin(piOver12 * (i + 0.5));
                    }
                    swin2.Slice(12, 24).Clear();
                }
                finally
                {
                    // Return the arrays to the pool
                    for (int i = 0; i < 4; i++)
                    {
                        ArrayPool<float>.Shared.Return(_swin[i]);
                    }
                }
            }

            private readonly List<float[]> _prevBlock;
            private readonly List<float[]> _nextBlock;

            internal HybridMDCT()
            {
                _prevBlock = [];
                _nextBlock = [];
            }

            internal void Reset()
            {
                _prevBlock.Clear();
                _nextBlock.Clear();
            }

            private void GetPrevBlock(int channel, out Span<float> prevBlock, out Span<float> nextBlock)
            {
                while (_prevBlock.Count <= channel)
                {
                    _prevBlock.Add(new float[SSLIMIT * LookupTables.SBLIMIT]);
                }
                while (_nextBlock.Count <= channel)
                {
                    _nextBlock.Add(new float[SSLIMIT * LookupTables.SBLIMIT]);
                }

                float[] prevArray = _prevBlock[channel];
                float[] nextArray = _nextBlock[channel];

                // Swap the blocks
                _nextBlock[channel] = prevArray;
                _prevBlock[channel] = nextArray;

                prevBlock = prevArray.AsSpan();
                nextBlock = nextArray.AsSpan();
            }

            internal void Apply(Span<float> fsIn, int channel, int blockType, bool doMixed)
            {
                // Get the previous & next blocks so we can overlap correctly
                GetPrevBlock(channel, out Span<float> prevblck, out Span<float> nextblck);

                // Determine starting subband
                int start = 0;
                if (doMixed)
                {
                    // A mixed block always has the first two subbands as blocktype 0
                    LongImpl(fsIn, 0, 2, nextblck, 0);
                    start = 2;
                }

                if (blockType == 2)
                {
                    // Handle short blocks
                    ShortImpl(fsIn, start, nextblck);
                }
                else
                {
                    // Handle long blocks
                    LongImpl(fsIn, start, LookupTables.SBLIMIT, nextblck, blockType);
                }

                // Overlap
                for (int i = 0; i < SSLIMIT * LookupTables.SBLIMIT; i++)
                {
                    fsIn[i] += prevblck[i];
                }
            }

            private readonly float[] _imdctTemp = new float[SSLIMIT];
            private readonly float[] _imdctResult = new float[SSLIMIT * 2];

            private void LongImpl(Span<float> fsIn, int sbStart, int sbLimit, Span<float> nextblck, int blockType)
            {
                Span<float> win = _swin[blockType].AsSpan();

                for (int sb = sbStart, ofs = sbStart * SSLIMIT; sb < sbLimit; sb++, ofs += SSLIMIT)
                {
                    // IMDCT
                    fsIn.Slice(ofs, SSLIMIT).CopyTo(_imdctTemp);
                    LongIMDCT(_imdctTemp, _imdctResult);

                    // Window and overlap
                    for (int i = 0; i < SSLIMIT; i++)
                    {
                        fsIn[ofs + i] = _imdctResult[i] * win[i];
                    }
                    for (int i = SSLIMIT; i < SSLIMIT * 2; i++)
                    {
                        nextblck[ofs + i - SSLIMIT] = _imdctResult[i] * win[i];
                    }
                }
            }

            private static void LongIMDCT(ReadOnlySpan<float> invec, Span<float> outvec)
            {
                Span<float> H = stackalloc float[17];
                Span<float> h = stackalloc float[18];
                Span<float> even = stackalloc float[9];
                Span<float> odd = stackalloc float[9];
                Span<float> even_idct = stackalloc float[9];
                Span<float> odd_idct = stackalloc float[9];

                // Perform the H calculation with SIMD optimization
                if (System.Numerics.Vector.IsHardwareAccelerated)
                {
                    int vecLength = System.Numerics.Vector<float>.Count;
                    int i = 0;

                    for (; i <= 16 - vecLength; i += vecLength)
                    {
                        System.Numerics.Vector<float> vecIn1 = new(invec.Slice(i, vecLength));
                        System.Numerics.Vector<float> vecIn2 = new(invec.Slice(i + 1, vecLength));
                        System.Numerics.Vector<float> vecResult = vecIn1 + vecIn2;
                        for (int j = 0; j < vecLength; j++)
                        {
                            H[i + j] = vecResult[j];
                        }
                    }

                    // Handle remaining elements
                    for (; i < 17; i++)
                    {
                        H[i] = invec[i] + invec[i + 1];
                    }
                }
                else
                {
                    for (int i = 0; i < 17; i++)
                        H[i] = invec[i] + invec[i + 1];
                }

                even[0] = invec[0];
                odd[0] = H[0];
                for (int i = 1, idx = 0; i < 9; i++, idx += 2)
                {
                    even[i] = H[idx + 1];
                    odd[i] = H[idx] + H[idx + 2];
                }

                Imdct_9pt(even, even_idct);
                Imdct_9pt(odd, odd_idct);

                for (int i = 0; i < 9; i++)
                {
                    odd_idct[i] *= LookupTables.Icos72_table[4 * i + 1];
                    h[i] = (even_idct[i] + odd_idct[i]) * LookupTables.Icos72_table[2 * i];
                }
                for (int i = 9; i < 18; i++)
                {
                    h[i] = (even_idct[17 - i] - odd_idct[17 - i]) * LookupTables.Icos72_table[2 * i];
                }

                for (int i = 0; i < 9; i++)
                {
                    float value = h[9 + i];
                    outvec[i] = value;
                    outvec[17 - i] = -value;
                }
                for (int i = 0; i < 9; i++)
                {
                    float value = -h[8 - i];
                    outvec[18 + i] = value;
                    outvec[35 - i] = value;
                }
            }

            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            private static void Imdct_9pt(ReadOnlySpan<float> invec, Span<float> outvec)
            {
                Span<float> even_idct = stackalloc float[5];
                Span<float> odd_idct = stackalloc float[4];

                // BEGIN 5 Point IMDCT
                float t0 = invec[6] * 0.5f + invec[0];
                float t1 = invec[0] - invec[6];
                float t2 = invec[2] - invec[4] - invec[8];

                even_idct[0] = t0 + invec[2] * 0.939692621f + invec[4] * 0.766044443f + invec[8] * 0.173648178f;
                even_idct[1] = t2 * 0.5f + t1;
                even_idct[2] = t0 - invec[2] * 0.173648178f - invec[4] * 0.939692621f + invec[8] * 0.766044443f;
                even_idct[3] = t0 - invec[2] * 0.766044443f + invec[4] * 0.173648178f - invec[8] * 0.939692621f;
                even_idct[4] = t1 - t2;
                // END 5 Point IMDCT

                // BEGIN 4 Point IMDCT
                float odd1 = invec[1] + invec[3];
                float odd2 = invec[3] + invec[5];
                t0 = (invec[5] + invec[7]) * 0.5f + invec[1];

                odd_idct[0] = t0 + odd1 * 0.939692621f + odd2 * 0.766044443f;
                odd_idct[1] = (invec[1] - invec[5]) * 1.5f - invec[7];
                odd_idct[2] = t0 - odd1 * 0.173648178f - odd2 * 0.939692621f;
                odd_idct[3] = t0 - odd1 * 0.766044443f + odd2 * 0.173648178f;
                // END 4 Point IMDCT

                // Adjust for non power of 2 IDCT
                float invec7 = invec[7];
                odd_idct[0] += invec7 * 0.173648178f;
                odd_idct[1] -= invec7 * 0.5f;
                odd_idct[2] += invec7 * 0.766044443f;
                odd_idct[3] -= invec7 * 0.939692621f;

                // Post-Twiddle
                odd_idct[0] *= 0.5f / 0.984807753f;
                odd_idct[1] *= 0.5f / 0.866025404f;
                odd_idct[2] *= 0.5f / 0.64278761f;
                odd_idct[3] *= 0.5f / 0.342020143f;

                // Combine even and odd parts
                for (int i = 0; i < 4; i++)
                {
                    outvec[i] = even_idct[i] + odd_idct[i];
                }
                outvec[4] = even_idct[4];
                // Mirror into the other half of the vector
                for (int i = 5; i < 9; i++)
                {
                    outvec[i] = even_idct[8 - i] - odd_idct[8 - i];
                }
            }

            private void ShortImpl(Span<float> fsIn, int sbStart, Span<float> nextblck)
            {
                Span<float> win = _swin[2].AsSpan();

                for (int sb = sbStart, ofs = sbStart * SSLIMIT; sb < LookupTables.SBLIMIT; sb++, ofs += SSLIMIT)
                {
                    // Rearrange vectors
                    for (int i = 0, tmpptr = 0; i < 3; i++)
                    {
                        int v = ofs + i;
                        for (int j = 0; j < 6; j++)
                        {
                            _imdctTemp[tmpptr + j] = fsIn[v];
                            v += 3;
                        }
                        tmpptr += 6;
                    }

                    // Short blocks involve 3 separate IMDCTs with overlap in two different buffers

                    fsIn.Slice(ofs, 6).Clear();

                    // Process the first 6 samples
                    ShortIMDCT(_imdctTemp, 0, _imdctResult);
                    _imdctResult.AsSpan(0, 12).CopyTo(fsIn.Slice(ofs + 6, 12));

                    // Process the next 6 samples
                    ShortIMDCT(_imdctTemp, 6, _imdctResult);
                    for (int i = 0; i < 6; i++)
                    {
                        fsIn[ofs + i + 12] += _imdctResult[i];
                    }
                    _imdctResult.AsSpan(6, 6).CopyTo(nextblck.Slice(ofs, 6));

                    // Process the final 6 samples
                    ShortIMDCT(_imdctTemp, 12, _imdctResult);
                    for (int i = 0; i < 6; i++)
                    {
                        nextblck[ofs + i] += _imdctResult[i];
                    }
                    _imdctResult.AsSpan(6, 6).CopyTo(nextblck.Slice(ofs + 6, 6));
                    nextblck.Slice(ofs + 12, 6).Clear();
                }
            }

            private const float sqrt32 = 0.8660254037844385965883020617184229195117950439453125f;

            private static void ShortIMDCT(ReadOnlySpan<float> invec, int inIdx, Span<float> outvec)
            {
                Span<float> H = stackalloc float[6];
                Span<float> h = stackalloc float[6];
                Span<float> even_idct = stackalloc float[3];
                Span<float> odd_idct = stackalloc float[3];
                float t0, t1, t2;

                /* Preprocess the input to the two 3-point IDCT's */
                for (int i = 1, idx = inIdx; i < 6; i++)
                {
                    H[i] = invec[idx];
                    H[i] += invec[++idx];
                }

                /* 3-point IMDCT */
                t0 = H[4] * 0.5f + invec[inIdx];
                t1 = H[2] * sqrt32;
                even_idct[0] = t0 + t1;
                even_idct[1] = invec[inIdx] - H[4];
                even_idct[2] = t0 - t1;

                /* 3-point IMDCT */
                t2 = H[3] + H[5];
                t0 = t2 * 0.5f + H[1];
                t1 = (H[1] + H[3]) * sqrt32;
                odd_idct[0] = t0 + t1;
                odd_idct[1] = H[1] - t2;
                odd_idct[2] = t0 - t1;

                /* Post-Twiddle */
                odd_idct[0] *= 0.51763809f;
                odd_idct[1] *= 0.707106781f;
                odd_idct[2] *= 1.931851653f;

                h[0] = (even_idct[0] + odd_idct[0]) * 0.50431448f;
                h[1] = (even_idct[1] + odd_idct[1]) * 0.5411961f;
                h[2] = (even_idct[2] + odd_idct[2]) * 0.630236207f;

                h[3] = (even_idct[2] - odd_idct[2]) * 0.821339816f;
                h[4] = (even_idct[1] - odd_idct[1]) * 1.306562965f;
                h[5] = (even_idct[0] - odd_idct[0]) * 3.830648788f;

                /* Rearrange the 6 values from the IDCT to the output vector */
                outvec[0] = h[3] * _swin[2][0];
                outvec[1] = h[4] * _swin[2][1];
                outvec[2] = h[5] * _swin[2][2];
                outvec[3] = -h[5] * _swin[2][3];
                outvec[4] = -h[4] * _swin[2][4];
                outvec[5] = -h[3] * _swin[2][5];
                outvec[6] = -h[2] * _swin[2][6];
                outvec[7] = -h[1] * _swin[2][7];
                outvec[8] = -h[0] * _swin[2][8];
                outvec[9] = -h[0] * _swin[2][9];
                outvec[10] = -h[1] * _swin[2][10];
                outvec[11] = -h[2] * _swin[2][11];
            }
        }
    }
}
