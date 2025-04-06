/*
 * XLayer - A C# MPEG1/2/2.5 audio decoder
 * 
 */

using System;

namespace XLayer.Decoder
{
    // Layers I & II are basically identical...  Layer II adds sample grouping, per subband allocation schemes, and granules
    // Because of this fact, we can use the same decoder for both
    internal abstract class LayerIIDecoderBase : LayerDecoderBase
    {
        protected const int SSLIMIT = 12;

        static protected bool GetCRC(MpegFrame frame, ReadOnlySpan<int> rateTable, ReadOnlySpan<int[]> allocLookupTable, bool readScfsiBits, ref uint crc)
        {
            // Keep track of how many active subbands we need to read selection info for
            int scfsiBits = 0;

            // Only read as many subbands as we actually need; pay attention to the intensity stereo subbands
            int subbandCount = rateTable.Length;
            int jsbound = subbandCount;
            if (frame.ChannelMode == MpegChannelMode.JointStereo)
            {
                jsbound = frame.ChannelModeExtension * 4 + 4;
            }

            // Read the full stereo subbands
            int channels = frame.ChannelMode == MpegChannelMode.Mono ? 1 : 2;
            for (int sb = 0; sb < subbandCount; sb++)
            {
                int bits = allocLookupTable[rateTable[sb]][0];
                if (sb < jsbound)
                {
                    for (int ch = 0; ch < channels; ch++)
                    {
                        int alloc = frame.ReadBits(bits);
                        if (alloc > 0) scfsiBits += 2;

                        MpegFrame.UpdateCRC(alloc, bits, ref crc);
                    }
                }
                else
                {
                    int alloc = frame.ReadBits(bits);
                    if (alloc > 0) scfsiBits += channels * 2;

                    MpegFrame.UpdateCRC(alloc, bits, ref crc);
                }
            }

            // Finally, read the scalefac selection bits
            if (readScfsiBits)
            {
                while (scfsiBits >= 2)
                {
                    MpegFrame.UpdateCRC(frame.ReadBits(2), 2, ref crc);
                    scfsiBits -= 2;
                }
            }

            return true;
        }

        // this is from the formula: C = 1 / (1 / (1 << (Bits / 2 + Bits % 2 - 1)) + .5f)
        // index by real bits (Bits / 2 + Bits % 2 - 1)
        private static readonly float[] _groupedC = [0, 0, 1.33333333333f, 1.60000000000f, 1.77777777777f];

        // these are always -0.5
        // index by real bits (Bits / 2 + Bits % 2 - 1)
        private static readonly float[] _groupedD = [0, 0, -0.5f, -0.5f, -0.5f];

        // this is from the formula: 1 / (1 - (1f / (1 << Bits)))
        // index by bits
        private static readonly float[] _C = [
                                         0.00000000000f,
                                         0.00000000000f, 1.33333333333f, 1.14285714286f, 1.06666666666f, 1.03225806452f, 1.01587301587f, 1.00787401575f, 1.00392156863f,
                                         1.00195694716f, 1.00097751711f, 1.00048851979f, 1.00024420024f, 1.00012208522f, 1.00006103888f, 1.00003051851f, 1.00001525902f
                                     ];

        // this is from the formula: 1f / (1 << Bits - 1) - 1
        // index by bits
        private static readonly float[] _D = [
                                         0.00000000000f - 0f,
                                         0.00000000000f - 0f, 0.50000000000f - 1f, 0.25000000000f - 1f, 0.12500000000f - 1f, 0.062500000000f - 1f, 0.03125000000f - 1f, 0.01562500000f - 1f, 0.00781250000f - 1f,
                                         0.00390625000f - 1f, 0.00195312500f - 1f, 0.00097656250f - 1f, 0.00048828125f - 1f, 0.000244140630f - 1f, 0.00012207031f - 1f, 0.00006103516f - 1f, 0.00003051758f - 1f
                                     ];

        private int _channels;
        private int _jsbound;
        private readonly int _granuleCount;
        private readonly int[][] _allocLookupTable, _scfsi, _samples;
        private readonly int[][][] _scalefac;
        private readonly int[][] _allocation;

        protected LayerIIDecoderBase(int[][] allocLookupTable, int granuleCount)
            : base()
        {
            _allocLookupTable = allocLookupTable;
            _granuleCount = granuleCount;

            _allocation = [new int[LookupTables.SBLIMIT], new int[LookupTables.SBLIMIT]];
            _scfsi = [new int[LookupTables.SBLIMIT], new int[LookupTables.SBLIMIT]];
            _samples = [new int[LookupTables.SBLIMIT * SSLIMIT * _granuleCount], new int[LookupTables.SBLIMIT * SSLIMIT * _granuleCount]];

            // NB: ReadScaleFactors(...) requires all three granules, even in Layer I
            _scalefac = [new int[3][], new int[3][]];
            for (int i = 0; i < 3; i++)
            {
                _scalefac[0][i] = new int[LookupTables.SBLIMIT];
                _scalefac[1][i] = new int[LookupTables.SBLIMIT];
            }

            // _polyPhaseBuf = new float[LookupTables.SBLIMIT];
        }

        internal override int DecodeFrame(IMpegFrame frame, Span<float> ch0, Span<float> ch1)
        {
            InitFrame(frame);

            ReadOnlySpan<int> rateTable = GetRateTable(frame);

            ReadAllocation(frame, rateTable);

            Span<int> scfsi0 = _scfsi[0].AsSpan();
            Span<int> scfsi1 = _scfsi[1].AsSpan();
            Span<int> allocation0 = _allocation[0].AsSpan();
            Span<int> allocation1 = _allocation[1].AsSpan();

            for (int i = 0; i < scfsi0.Length; i++)
            {
                scfsi0[i] = allocation0[i] != 0 ? 2 : -1;
                scfsi1[i] = allocation1[i] != 0 ? 2 : -1;
            }

            ReadScaleFactorSelection(frame, _scfsi, _channels);

            ReadScaleFactors(frame);

            ReadSamples(frame);

            return DecodeSamples(ch0, ch1);
        }

        // this just reads the channel mode and set a few flags
        private void InitFrame(IMpegFrame frame)
        {
            switch (frame.ChannelMode)
            {
                case MpegChannelMode.Mono:
                    _channels = 1;
                    _jsbound = LookupTables.SBLIMIT;
                    break;
                case MpegChannelMode.JointStereo:
                    _channels = 2;
                    _jsbound = frame.ChannelModeExtension * 4 + 4;
                    break;
                default:
                    _channels = 2;
                    _jsbound = LookupTables.SBLIMIT;
                    break;
            }
        }

        abstract protected int[] GetRateTable(IMpegFrame frame);

        private void ReadAllocation(IMpegFrame frame, ReadOnlySpan<int> rateTable)
        {
            int _subBandCount = rateTable.Length;
            if (_jsbound > _subBandCount) _jsbound = _subBandCount;

            _allocation[0].AsSpan().Clear();
            _allocation[1].AsSpan().Clear();

            for (int sb = 0; sb < _subBandCount; sb++)
            {
                ReadOnlySpan<int> table = _allocLookupTable[rateTable[sb]];
                int bits = table[0];

                if (sb < _jsbound)
                {
                    for (int ch = 0; ch < _channels; ch++)
                    {
                        _allocation[ch][sb] = table[frame.ReadBits(bits) + 1];
                    }
                }
                else
                {
                    int allocValue = table[frame.ReadBits(bits) + 1];
                    _allocation[0][sb] = allocValue;
                    _allocation[1][sb] = allocValue;
                }
            }
        }

        abstract protected void ReadScaleFactorSelection(IMpegFrame frame, int[][] scfsi, int channels);

        private void ReadScaleFactors(IMpegFrame frame)
        {
            for (int sb = 0; sb < LookupTables.SBLIMIT; sb++)
            {
                for (int ch = 0; ch < _channels; ch++)
                {
                    switch (_scfsi[ch][sb])
                    {
                        case 0:
                            // all three
                            _scalefac[ch][0][sb] = frame.ReadBits(6);
                            _scalefac[ch][1][sb] = frame.ReadBits(6);
                            _scalefac[ch][2][sb] = frame.ReadBits(6);
                            break;
                        case 1:
                            // only two (2 = 1)
                            _scalefac[ch][0][sb] =
                            _scalefac[ch][1][sb] = frame.ReadBits(6);
                            _scalefac[ch][2][sb] = frame.ReadBits(6);
                            break;
                        case 2:
                            // only one (3 = 2 = 1)
                            _scalefac[ch][0][sb] =
                            _scalefac[ch][1][sb] =
                            _scalefac[ch][2][sb] = frame.ReadBits(6);
                            break;
                        case 3:
                            // only two (3 = 2)
                            _scalefac[ch][0][sb] = frame.ReadBits(6);
                            _scalefac[ch][1][sb] =
                            _scalefac[ch][2][sb] = frame.ReadBits(6);
                            break;
                        default:
                            // none
                            _scalefac[ch][0][sb] = 63;
                            _scalefac[ch][1][sb] = 63;
                            _scalefac[ch][2][sb] = 63;
                            break;
                    }
                }
            }
        }

        private void ReadSamples(IMpegFrame frame)
        {
            // load in all the data for this frame (1152 samples in this case)
            // NB: we flatten these into output order
            for (int ss = 0, idx = 0; ss < SSLIMIT; ss++, idx += LookupTables.SBLIMIT * (_granuleCount - 1))
            {
                for (int sb = 0; sb < LookupTables.SBLIMIT; sb++, idx++)
                {
                    for (int ch = 0; ch < _channels; ch++)
                    {
                        if (ch == 0 || sb < _jsbound)
                        {
                            int alloc = _allocation[ch][sb];
                            if (alloc != 0)
                            {
                                if (alloc < 0)
                                {
                                    // grouping (Layer II only, so we don't have to play with the granule count)
                                    int val = frame.ReadBits(-alloc);
                                    int levels = (1 << (-alloc / 2 + -alloc % 2 - 1)) + 1;

                                    _samples[ch][idx] = val % levels;
                                    val /= levels;
                                    _samples[ch][idx + LookupTables.SBLIMIT] = val % levels;
                                    _samples[ch][idx + LookupTables.SBLIMIT * 2] = val / levels;
                                }
                                else
                                {
                                    // non-grouping
                                    for (int gr = 0; gr < _granuleCount; gr++)
                                    {
                                        _samples[ch][idx + LookupTables.SBLIMIT * gr] = frame.ReadBits(alloc);
                                    }
                                }
                            }
                            else
                            {
                                // no energy...  zero out the samples
                                for (int gr = 0; gr < _granuleCount; gr++)
                                {
                                    _samples[ch][idx + LookupTables.SBLIMIT * gr] = 0;
                                }
                            }
                        }
                        else
                        {
                            // copy chan 0 to chan 1
                            for (int gr = 0; gr < _granuleCount; gr++)
                            {
                                _samples[1][idx + LookupTables.SBLIMIT * gr] = _samples[0][idx + LookupTables.SBLIMIT * gr];
                            }
                        }
                    }
                }
            }
        }

        private int DecodeSamples(Span<float> ch0, Span<float> ch1)
        {
            // Determine which channels to process based on stereo mode
            int startChannel = 0;
            int endChannel = _channels - 1;
            bool useLeftChannel = _channels == 1 || StereoMode != StereoMode.RightOnly;
            bool useRightChannel = _channels == 2 && StereoMode != StereoMode.LeftOnly;
            Span<float> _polyPhaseBuf = stackalloc float[LookupTables.SBLIMIT];

            int idx = 0;
            for (int ch = startChannel; ch <= endChannel; ch++)
            {
                idx = 0;
                for (int gr = 0; gr < _granuleCount; gr++)
                {
                    for (int ss = 0; ss < SSLIMIT; ss++)
                    {
                        for (int sb = 0; sb < LookupTables.SBLIMIT; sb++, idx++)
                        {
                            // NB: Layers I & II use the same algorithm here...  Grouping changes the bit counts, but doesn't change the algo
                            //     - Up to 65534 possible values (65535 does not appear to be usable)
                            //     - All values can be handled with 16-bit logic as long as the correct C and D constants are used
                            //     - Make sure to normalize each sample to 16 bits!

                            var alloc = _allocation[ch][sb];
                            if (alloc != 0)
                            {
                                float[] c, d;
                                if (alloc < 0)
                                {
                                    alloc = -alloc / 2 + -alloc % 2 - 1;
                                    c = _groupedC;
                                    d = _groupedD;
                                }
                                else
                                {
                                    c = _C;
                                    d = _D;
                                }

                                // read sample; normalize, scale & center to [-0.999984741f..0.999984741f]; apply scalefactor
                                _polyPhaseBuf[sb] = c[alloc] * ((_samples[ch][idx] << (16 - alloc)) / 32768f + d[alloc]) * LookupTables.DenormalMultiplier[_scalefac[ch][gr][sb]];
                            }
                            else
                            {
                                // no transmitted energy...
                                _polyPhaseBuf[sb] = 0f;
                            }
                        }

                        // do the polyphase output for this channel, section, and granule
                        InversePolyPhase(ch, _polyPhaseBuf);

                        // Copy to the appropriate output buffer
                        if (ch == 0 && useLeftChannel)
                        {
                            _polyPhaseBuf.CopyTo(ch0.Slice(idx - LookupTables.SBLIMIT, LookupTables.SBLIMIT));
                        }
                        else if (ch == 1 && useRightChannel)
                        {
                            _polyPhaseBuf.CopyTo(ch1.Slice(idx - LookupTables.SBLIMIT, LookupTables.SBLIMIT));
                        }
                        else if (StereoMode == StereoMode.RightOnly && ch == 0)
                        {
                            // Special case: right-only mode but processing left channel
                            _polyPhaseBuf.CopyTo(ch0.Slice(idx - LookupTables.SBLIMIT, LookupTables.SBLIMIT));
                        }
                    }
                }
            }

            if (_channels == 2 && StereoMode == StereoMode.DownmixToMono)
            {
                for (int i = 0; i < idx; i++)
                {
                    ch0[i] = (ch0[i] + ch1[i]) / 2;
                }
            }

            return idx;
        }
    }
}
