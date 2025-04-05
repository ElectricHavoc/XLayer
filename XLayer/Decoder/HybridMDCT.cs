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
using System.Collections.Generic;

namespace XLayer.Decoder
{
sealed partial class LayerIIIDecoder
    {
        // This class is based on the Fluendo hybrid logic.
        class HybridMDCT
        {
            const float PI = (float)Math.PI;

            static float[][] _swin;

            static HybridMDCT()
            {
                _swin = new float[][] { new float[36], new float[36], new float[36], new float[36] };

                int i;

                /* type 0 */
                for (i = 0; i < 36; i++)
                    _swin[0][i] = (float)Math.Sin(PI / 36 * (i + 0.5));

                /* type 1 */
                for (i = 0; i < 18; i++)
                    _swin[1][i] = (float)Math.Sin(PI / 36 * (i + 0.5));
                for (i = 18; i < 24; i++)
                    _swin[1][i] = 1.0f;
                for (i = 24; i < 30; i++)
                    _swin[1][i] = (float)Math.Sin(PI / 12 * (i + 0.5 - 18));
                for (i = 30; i < 36; i++)
                    _swin[1][i] = 0.0f;

                /* type 3 */
                for (i = 0; i < 6; i++)
                    _swin[3][i] = 0.0f;
                for (i = 6; i < 12; i++)
                    _swin[3][i] = (float)Math.Sin(PI / 12 * (i + 0.5 - 6));
                for (i = 12; i < 18; i++)
                    _swin[3][i] = 1.0f;
                for (i = 18; i < 36; i++)
                    _swin[3][i] = (float)Math.Sin(PI / 36 * (i + 0.5));

                /* type 2 */
                for (i = 0; i < 12; i++)
                    _swin[2][i] = (float)Math.Sin(PI / 12 * (i + 0.5));
                for (i = 12; i < 36; i++)
                    _swin[2][i] = 0.0f;
            }

            #region Tables

            static float[] icos72_table = {
                                          5.004763425816599609063928255636710673570632934570312500000000e-01f,
                                          5.019099187716736798492433990759309381246566772460937500000000e-01f,
                                          5.043144802900764167574720886477734893560409545898437500000000e-01f,
                                          5.077133059428725614381505693017970770597457885742187500000000e-01f,
                                          5.121397571572545714957414020318537950515747070312500000000000e-01f,
                                          5.176380902050414789528076653368771076202392578125000000000000e-01f,
                                          5.242645625704053236049162478593643754720687866210937500000000e-01f,
                                          5.320888862379560269033618169487453997135162353515625000000000e-01f,
                                          5.411961001461970122150546558259520679712295532226562500000000e-01f,
                                          5.516889594812458552652856269560288637876510620117187500000000e-01f,
                                          5.636909734331712051869089918909594416618347167968750000000000e-01f,
                                          5.773502691896257310588680411456152796745300292968750000000000e-01f,
                                          5.928445237170802961657045671017840504646301269531250000000000e-01f,
                                          6.103872943807280293526673631276935338973999023437500000000000e-01f,
                                          6.302362070051321651931175438221544027328491210937500000000000e-01f,
                                          6.527036446661392821155800447741057723760604858398437500000000e-01f,
                                          6.781708524546284921896699415810871869325637817382812500000000e-01f,
                                          7.071067811865474617150084668537601828575134277343750000000000e-01f,
                                          7.400936164611303658134033867099788039922714233398437500000000e-01f,
                                          7.778619134302061643992942663317080587148666381835937500000000e-01f,
                                          8.213398158522907666068135768000502139329910278320312500000000e-01f,
                                          8.717233978105488612087015098950359970331192016601562500000000e-01f,
                                          9.305794983517888807611484480730723589658737182617187500000000e-01f,
                                          9.999999999999997779553950749686919152736663818359375000000000e-01f,
                                          1.082840285100100219395358180918265134096145629882812500000000e+00f,
                                          1.183100791576249255498964885191526263952255249023437500000000e+00f,
                                          1.306562964876376353728915091778617352247238159179687500000000e+00f,
                                          1.461902200081543146126250576344318687915802001953125000000000e+00f,
                                          1.662754761711521034328598034335300326347351074218750000000000e+00f,
                                          1.931851652578135070115195048856548964977264404296875000000000e+00f,
                                          2.310113157672649020213384574162773787975311279296875000000000e+00f,
                                          2.879385241571815523542454684502445161342620849609375000000000e+00f,
                                          3.830648787770197127855453800293616950511932373046875000000000e+00f,
                                          5.736856622834929808618653623852878808975219726562500000000000e+00f,
                                          1.146279281302667207853573927422985434532165527343750000000000e+01f
                                          };

            #endregion

            List<float[]> _prevBlock;
            List<float[]> _nextBlock;

            internal HybridMDCT()
            {
                _prevBlock = new List<float[]>();
                _nextBlock = new List<float[]>();
            }

            internal void Reset()
            {
                _prevBlock.Clear();
                _nextBlock.Clear();
            }

            void GetPrevBlock(int channel, out float[] prevBlock, out float[] nextBlock)
            {
                while (_prevBlock.Count <= channel)
                {
                    _prevBlock.Add(new float[SSLIMIT * SBLIMIT]);
                }
                while (_nextBlock.Count <= channel)
                {
                    _nextBlock.Add(new float[SSLIMIT * SBLIMIT]);
                }
                prevBlock = _prevBlock[channel];
                nextBlock = _nextBlock[channel];

                // now swap them (see Apply(...) below)
                _nextBlock[channel] = prevBlock;
                _prevBlock[channel] = nextBlock;
            }

            internal void Apply(float[] fsIn, int channel, int blockType, bool doMixed)
            {
                // get the previous & next blocks so we can overlap correctly
                //  NB: we swap each pass so we can add the previous block in a single pass
                float[] prevblck, nextblck;
                GetPrevBlock(channel, out prevblck, out nextblck);

                // now we have a few options for processing blocks...
                int start = 0;
                if (doMixed)
                {
                    // a mixed block always has the first two subbands as blocktype 0
                    LongImpl(fsIn, 0, 2, nextblck, 0);
                    start = 2;
                }

                if (blockType == 2)
                {
                    // this is the only place we care about short blocks
                    ShortImpl(fsIn, start, nextblck);
                }
                else
                {
                    LongImpl(fsIn, start, SBLIMIT, nextblck, blockType);
                }

                // overlap
                for (int i = 0; i < SSLIMIT * SBLIMIT; i++)
                {
                    fsIn[i] += prevblck[i];
                }
            }

            float[] _imdctTemp = new float[SSLIMIT];
            float[] _imdctResult = new float[SSLIMIT * 2];

            void LongImpl(float[] fsIn, int sbStart, int sbLimit, float[] nextblck, int blockType)
            {
                for (int sb = sbStart, ofs = sbStart * SSLIMIT; sb < sbLimit; sb++)
                {
                    // IMDCT
                    Array.Copy(fsIn, ofs, _imdctTemp, 0, SSLIMIT);
                    LongIMDCT(_imdctTemp, _imdctResult);

                    // window
                    var win = _swin[blockType];
                    int i = 0;
                    for (; i < SSLIMIT; i++)
                    {
                        fsIn[ofs++] = _imdctResult[i] * win[i];
                    }
                    ofs -= 18;
                    for (; i < SSLIMIT * 2; i++)
                    {
                        nextblck[ofs++] = _imdctResult[i] * win[i];
                    }
                }
            }

            static void LongIMDCT(float[] invec, float[] outvec)
            {
                int i;
                float[] H = new float[17], h = new float[18], even = new float[9], odd = new float[9], even_idct = new float[9], odd_idct = new float[9];

                for (i = 0; i < 17; i++)
                    H[i] = invec[i] + invec[i + 1];

                even[0] = invec[0];
                odd[0] = H[0];
                var idx = 0;
                for (i = 1; i < 9; i++, idx += 2)
                {
                    even[i] = H[idx + 1];
                    odd[i] = H[idx] + H[idx + 2];
                }

                imdct_9pt(even, even_idct);
                imdct_9pt(odd, odd_idct);

                for (i = 0; i < 9; i++)
                {
                    odd_idct[i] *= ICOS36_A(i);
                    h[i] = (even_idct[i] + odd_idct[i]) * ICOS72_A(i);
                }
                for ( /* i = 9 */ ; i < 18; i++)
                {
                    h[i] = (even_idct[17 - i] - odd_idct[17 - i]) * ICOS72_A(i);
                }

                /* Rearrange the 18 values from the IDCT to the output vector */
                outvec[0] = h[9];
                outvec[1] = h[10];
                outvec[2] = h[11];
                outvec[3] = h[12];
                outvec[4] = h[13];
                outvec[5] = h[14];
                outvec[6] = h[15];
                outvec[7] = h[16];
                outvec[8] = h[17];

                outvec[9] = -h[17];
                outvec[10] = -h[16];
                outvec[11] = -h[15];
                outvec[12] = -h[14];
                outvec[13] = -h[13];
                outvec[14] = -h[12];
                outvec[15] = -h[11];
                outvec[16] = -h[10];
                outvec[17] = -h[9];

                outvec[35] = outvec[18] = -h[8];
                outvec[34] = outvec[19] = -h[7];
                outvec[33] = outvec[20] = -h[6];
                outvec[32] = outvec[21] = -h[5];
                outvec[31] = outvec[22] = -h[4];
                outvec[30] = outvec[23] = -h[3];
                outvec[29] = outvec[24] = -h[2];
                outvec[28] = outvec[25] = -h[1];
                outvec[27] = outvec[26] = -h[0];
            }

            static float ICOS72_A(int i)
            {
                return icos72_table[2 * i];
            }

            static float ICOS36_A(int i)
            {
                return icos72_table[4 * i + 1];
            }

            static void imdct_9pt(float[] invec, float[] outvec)
            {
                int i;
                float[] even_idct = new float[5], odd_idct = new float[4];
                float t0, t1, t2;

                /* BEGIN 5 Point IMDCT */
                t0 = invec[6] / 2.0f + invec[0];
                t1 = invec[0] - invec[6];
                t2 = invec[2] - invec[4] - invec[8];

                even_idct[0] = t0 + invec[2] * 0.939692621f
                    + invec[4] * 0.766044443f + invec[8] * 0.173648178f;

                even_idct[1] = t2 / 2.0f + t1;
                even_idct[2] = t0 - invec[2] * 0.173648178f
                    - invec[4] * 0.939692621f + invec[8] * 0.766044443f;

                even_idct[3] = t0 - invec[2] * 0.766044443f
                    + invec[4] * 0.173648178f - invec[8] * 0.939692621f;

                even_idct[4] = t1 - t2;
                /* END 5 Point IMDCT */

                /* BEGIN 4 Point IMDCT */
                {
                    float odd1, odd2;
                    odd1 = invec[1] + invec[3];
                    odd2 = invec[3] + invec[5];
                    t0 = (invec[5] + invec[7]) * 0.5f + invec[1];

                    odd_idct[0] = t0 + odd1 * 0.939692621f + odd2 * 0.766044443f;
                    odd_idct[1] = (invec[1] - invec[5]) * 1.5f - invec[7];
                    odd_idct[2] = t0 - odd1 * 0.173648178f - odd2 * 0.939692621f;
                    odd_idct[3] = t0 - odd1 * 0.766044443f + odd2 * 0.173648178f;
                }
                /* END 4 Point IMDCT */

                /* Adjust for non power of 2 IDCT */
                odd_idct[0] += invec[7] * 0.173648178f;
                odd_idct[1] -= invec[7] * 0.5f;
                odd_idct[2] += invec[7] * 0.766044443f;
                odd_idct[3] -= invec[7] * 0.939692621f;

                /* Post-Twiddle */
                odd_idct[0] *= 0.5f / 0.984807753f;
                odd_idct[1] *= 0.5f / 0.866025404f;
                odd_idct[2] *= 0.5f / 0.64278761f;
                odd_idct[3] *= 0.5f / 0.342020143f;

                for (i = 0; i < 4; i++)
                {
                    outvec[i] = even_idct[i] + odd_idct[i];
                }
                outvec[4] = even_idct[4];
                /* Mirror into the other half of the vector */
                for (i = 5; i < 9; i++)
                {
                    outvec[i] = even_idct[8 - i] - odd_idct[8 - i];
                }
            }

            void ShortImpl(float[] fsIn, int sbStart, float[] nextblck)
            {
                var win = _swin[2];

                for (int sb = sbStart, ofs = sbStart * SSLIMIT; sb < SBLIMIT; sb++, ofs += SSLIMIT)
                {
                    // rearrange vectors
                    for (int i = 0, tmpptr = 0; i < 3; i++)
                    {
                        var v = ofs + i;
                        for (int j = 0; j < 6; j++)
                        {
                            _imdctTemp[tmpptr + j] = fsIn[v];
                            v += 3;
                        }
                        tmpptr += 6;
                    }

                    // short blocks are fun...  3 separate IMDCT's with overlap in two different buffers

                    Array.Clear(fsIn, ofs, 6);

                    // do the first 6 samples
                    ShortIMDCT(_imdctTemp, 0, _imdctResult);
                    Array.Copy(_imdctResult, 0, fsIn, ofs + 6, 12);

                    // now the next 6
                    ShortIMDCT(_imdctTemp, 6, _imdctResult);
                    for (int i = 0; i < 6; i++)
                    {
                        // add the first half to tsOut
                        fsIn[ofs + i + 12] += _imdctResult[i];
                    }
                    Array.Copy(_imdctResult, 6, nextblck, ofs, 6);

                    // now the final 6
                    ShortIMDCT(_imdctTemp, 12, _imdctResult);
                    for (int i = 0; i < 6; i++)
                    {
                        // add the first half to nextblck
                        nextblck[ofs + i] += _imdctResult[i];
                    }
                    Array.Copy(_imdctResult, 6, nextblck, ofs + 6, 6);
                    Array.Clear(nextblck, ofs + 12, 6);
                }
            }

            const float sqrt32 = 0.8660254037844385965883020617184229195117950439453125f;

            static void ShortIMDCT(float[] invec, int inIdx, float[] outvec)
            {
                int i;
                float[] H = new float[6], h = new float[6], even_idct = new float[3], odd_idct = new float[3];
                float t0, t1, t2;

                /* Preprocess the input to the two 3-point IDCT's */
                var idx = inIdx;
                for (i = 1; i < 6; i++)
                {
                    H[i] = invec[idx];
                    H[i] += invec[++idx];
                }

                /* 3-point IMDCT */
                t0 = H[4] / 2.0f + invec[inIdx];
                t1 = H[2] * sqrt32;
                even_idct[0] = t0 + t1;
                even_idct[1] = invec[inIdx] - H[4];
                even_idct[2] = t0 - t1;
                /* END 3-point IMDCT */

                /* 3-point IMDCT */
                t2 = H[3] + H[5];

                t0 = (t2) / 2.0f + H[1];
                t1 = (H[1] + H[3]) * sqrt32;
                odd_idct[0] = t0 + t1;
                odd_idct[1] = H[1] - t2;
                odd_idct[2] = t0 - t1;
                /* END 3-point IMDCT */

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

        #endregion
    }
}
