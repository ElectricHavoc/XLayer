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

namespace XLayer.Decoder
{
    /// <summary>
    /// Class Implementing Layer 3 Decoder.
    /// </summary>
    sealed partial class LayerIIIDecoder : LayerDecoderBase
    {
        const int SSLIMIT = 18;

        static internal bool GetCRC(MpegFrame frame, ref uint crc)
        {
            var cnt = frame.GetSideDataSize();
            while (--cnt >= 0)
            {
                MpegFrame.UpdateCRC(frame.ReadBits(8), 8, ref crc);
            }
            return true;
        }

        readonly HybridMDCT _hybrid = new();
        readonly BitReservoir _bitRes = new();

        internal LayerIIIDecoder()
        {
            _tableSelect =
            [
                [new int[3], new int[3]],
                [new int[3], new int[3]],
            ];

            _subblockGain =
            [
                [new float[3], new float[3]],
                [new float[3], new float[3]],
            ];
        }

        internal override int DecodeFrame(IMpegFrame frame, float[] ch0, float[] ch1)
        {
            // load the frame information
            ReadSideInfo(frame);

            // load the frame's main data
            if (!_bitRes.AddBits(frame, _mainDataBegin))
            {
                return 0;
            }

            // prep the reusable tables
            PrepTables(frame);

            // do our stereo mode setup
            var chanBufs = new float[2][];
            var startChannel = 0;
            var endChannel = _channels - 1;
            if (_channels == 1 || StereoMode == StereoMode.LeftOnly || StereoMode == StereoMode.DownmixToMono)
            {
                chanBufs[0] = ch0;
                endChannel = 0;
            }
            else if (StereoMode == StereoMode.RightOnly)
            {
                chanBufs[1] = ch0;  // this is correct... if there's only a single channel output, it goes in channel 0's buffer
                startChannel = 1;
            }
            else    // MpegStereoMode.Both
            {
                chanBufs[0] = ch0;
                chanBufs[1] = ch1;
            }

            // get the granule count
            int granules;
            if (frame.Version == MpegVersion.Version1)
            {
                granules = 2;
            }
            else
            {
                granules = 1;
            }

            // decode the audio data
            int offset = 0;
            for (var gr = 0; gr < granules; gr++)
            {
                for (var ch = 0; ch < _channels; ch++)
                {
                    // read scale factors
                    int sfbits;
                    if (frame.Version == MpegVersion.Version1)
                    {
                        sfbits = ReadScalefactors(gr, ch);
                    }
                    else
                    {
                        sfbits = ReadLsfScalefactors(gr, ch, frame.ChannelModeExtension);
                    }

                    // huffman & dequant
                    ReadSamples(sfbits, gr, ch);
                }

                // stereo processing
                Stereo(frame.ChannelMode, frame.ChannelModeExtension, gr, frame.Version != MpegVersion.Version1);

                for (int ch = startChannel; ch <= endChannel; ch++)
                {
                    // pull some values so we don't have to index them again later
                    var buf = _samples[ch];
                    var blockType = _blockType[gr][ch];
                    var blockSplit = _blockSplitFlag[gr][ch];
                    var mixedBlock = _mixedBlockFlag[gr][ch];

                    // do the short/long/mixed logic here so it's only done once per channel per granule
                    if (blockSplit && blockType == 2)
                    {
                        if (mixedBlock)
                        {
                            // reorder & antialias mixed blocks
                            Reorder(buf, true);
                            AntiAlias(buf, true);
                        }
                        else
                        {
                            // reorder short blocks
                            Reorder(buf, false);
                        }
                    }
                    else
                    {
                        // antialias long blocks
                        AntiAlias(buf, false);
                    }

                    // hybrid processing
                    _hybrid.Apply(buf, ch, blockType, blockSplit && mixedBlock);

                    // frequency inversion
                    FrequencyInversion(buf);

                    // inverse polyphase
                    InversePolyphase(buf, ch, offset, chanBufs[ch]);
                }

                offset += SBLIMIT * SSLIMIT;
            }

            return offset;
        }

        internal override void ResetForSeek()
        {
            base.ResetForSeek();

            _hybrid.Reset();

            _bitRes.Reset();
        }

        int _channels, _privBits, _mainDataBegin;

        readonly int[][] _scfsi = [new int[4], new int[4]];                //     ch, scfsi_band
        readonly int[][] _part23Length = [new int[2], new int[2]];         // gr, ch
        readonly int[][] _bigValues = [new int[2], new int[2]];            // gr, ch
        readonly float[][] _globalGain = [new float[2], new float[2]];     // gr, ch
        readonly int[][] _scalefacCompress = [new int[2], new int[2]];     // gr, ch
        readonly bool[][] _blockSplitFlag = [new bool[2], new bool[2]];    // gr, ch
        readonly bool[][] _mixedBlockFlag = [new bool[2], new bool[2]];    // gr, ch
        readonly int[][] _blockType = [new int[2], new int[2]];            // gr, ch
        readonly int[][][] _tableSelect;                                     // gr, ch, region
        readonly float[][][] _subblockGain;                                  // gr, ch, window
        readonly int[][] _regionAddress1 = [new int[2], new int[2]];       // gr, ch
        readonly int[][] _regionAddress2 = [new int[2], new int[2]];       // gr, ch
        readonly int[][] _preflag = [new int[2], new int[2]];              // gr, ch
        readonly float[][] _scalefacScale = [new float[2], new float[2]];  // gr, ch
        readonly int[][] _count1TableSelect = [new int[2], new int[2]];    // gr, ch

        static readonly float[] GAIN_TAB =
        [
            1.57009245868378E-16f, 1.86716512307887E-16f, 2.22044604925031E-16f, 2.64057024024816E-16f, 3.14018491736756E-16f, 3.73433024615774E-16f, 4.44089209850063E-16f, 5.28114048049630E-16f,
            6.28036983473509E-16f, 7.46866049231544E-16f, 8.88178419700125E-16f, 1.05622809609926E-15f, 1.25607396694702E-15f, 1.49373209846309E-15f, 1.77635683940025E-15f, 2.11245619219853E-15f,
            2.51214793389404E-15f, 2.98746419692619E-15f, 3.55271367880050E-15f, 4.22491238439706E-15f, 5.02429586778810E-15f, 5.97492839385238E-15f, 7.10542735760100E-15f, 8.44982476879408E-15f,
            1.00485917355761E-14f, 1.19498567877047E-14f, 1.42108547152020E-14f, 1.68996495375882E-14f, 2.00971834711523E-14f, 2.38997135754094E-14f, 2.84217094304040E-14f, 3.37992990751764E-14f,
            4.01943669423047E-14f, 4.77994271508190E-14f, 5.68434188608080E-14f, 6.75985981503528E-14f, 8.03887338846093E-14f, 9.55988543016378E-14f, 1.13686837721616E-13f, 1.35197196300706E-13f,
            1.60777467769219E-13f, 1.91197708603275E-13f, 2.27373675443232E-13f, 2.70394392601411E-13f, 3.21554935538437E-13f, 3.82395417206551E-13f, 4.54747350886464E-13f, 5.40788785202823E-13f,
            6.43109871076876E-13f, 7.64790834413101E-13f, 9.09494701772928E-13f, 1.08157757040564E-12f, 1.28621974215375E-12f, 1.52958166882621E-12f, 1.81898940354586E-12f, 2.16315514081129E-12f,
            2.57243948430750E-12f, 3.05916333765241E-12f, 3.63797880709171E-12f, 4.32631028162258E-12f, 5.14487896861500E-12f, 6.11832667530482E-12f, 7.27595761418343E-12f, 8.65262056324518E-12f,
            1.02897579372300E-11f, 1.22366533506096E-11f, 1.45519152283669E-11f, 1.73052411264903E-11f, 2.05795158744600E-11f, 2.44733067012193E-11f, 2.91038304567337E-11f, 3.46104822529806E-11f,
            4.11590317489199E-11f, 4.89466134024385E-11f, 5.82076609134674E-11f, 6.92209645059613E-11f, 8.23180634978400E-11f, 9.78932268048772E-11f, 1.16415321826935E-10f, 1.38441929011922E-10f,
            1.64636126995680E-10f, 1.95786453609754E-10f, 2.32830643653870E-10f, 2.76883858023845E-10f, 3.29272253991360E-10f, 3.91572907219509E-10f, 4.65661287307739E-10f, 5.53767716047690E-10f,
            6.58544507982719E-10f, 7.83145814439016E-10f, 9.31322574615479E-10f, 1.10753543209538E-09f, 1.31708901596544E-09f, 1.56629162887804E-09f, 1.86264514923096E-09f, 2.21507086419076E-09f,
            2.63417803193088E-09f, 3.13258325775607E-09f, 3.72529029846191E-09f, 4.43014172838152E-09f, 5.26835606386176E-09f, 6.26516651551212E-09f, 7.45058059692383E-09f, 8.86028345676304E-09f,
            1.05367121277235E-08f, 1.25303330310243E-08f, 1.49011611938477E-08f, 1.77205669135261E-08f, 2.10734242554471E-08f, 2.50606660620485E-08f, 2.98023223876953E-08f, 3.54411338270521E-08f,
            4.21468485108941E-08f, 5.01213321240971E-08f, 5.96046447753906E-08f, 7.08822676541044E-08f, 8.42936970217880E-08f, 1.00242664248194E-07f, 1.19209289550781E-07f, 1.41764535308209E-07f,
            1.68587394043576E-07f, 2.00485328496388E-07f, 2.38418579101562E-07f, 2.83529070616417E-07f, 3.37174788087152E-07f, 4.00970656992777E-07f, 4.76837158203125E-07f, 5.67058141232835E-07f,
            6.74349576174305E-07f, 8.01941313985553E-07f, 9.53674316406250E-07f, 1.13411628246567E-06f, 1.34869915234861E-06f, 1.60388262797110E-06f, 1.90734863281250E-06f, 2.26823256493134E-06f,
            2.69739830469722E-06f, 3.20776525594221E-06f, 3.81469726562500E-06f, 4.53646512986268E-06f, 5.39479660939444E-06f, 6.41553051188442E-06f, 7.62939453125000E-06f, 9.07293025972536E-06f,
            1.07895932187889E-05f, 1.28310610237688E-05f, 1.52587890625000E-05f, 1.81458605194507E-05f, 2.15791864375777E-05f, 2.56621220475377E-05f, 3.05175781250000E-05f, 3.62917210389014E-05f,
            4.31583728751555E-05f, 5.13242440950754E-05f, 6.10351562500000E-05f, 7.25834420778029E-05f, 8.63167457503110E-05f, 1.02648488190151E-04f, 1.22070312500000E-04f, 1.45166884155606E-04f,
            1.72633491500622E-04f, 2.05296976380301E-04f, 2.44140625000000E-04f, 2.90333768311211E-04f, 3.45266983001244E-04f, 4.10593952760603E-04f, 4.88281250000000E-04f, 5.80667536622423E-04f,
            6.90533966002488E-04f, 8.21187905521206E-04f, 9.76562500000000E-04f, 1.16133507324485E-03f, 1.38106793200498E-03f, 1.64237581104241E-03f, 1.95312500000000E-03f, 2.32267014648969E-03f,
            2.76213586400995E-03f, 3.28475162208482E-03f, 3.90625000000000E-03f, 4.64534029297938E-03f, 5.52427172801990E-03f, 6.56950324416964E-03f, 7.81250000000000E-03f, 9.29068058595876E-03f,
            1.10485434560398E-02f, 1.31390064883393E-02f, 1.56250000000000E-02f, 1.85813611719175E-02f, 2.20970869120796E-02f, 2.62780129766786E-02f, 3.12500000000000E-02f, 3.71627223438350E-02f,
            4.41941738241592E-02f, 5.25560259533572E-02f, 6.25000000000000E-02f, 7.43254446876701E-02f, 8.83883476483184E-02f, 1.05112051906714E-01f, 1.25000000000000E-01f, 1.48650889375340E-01f,
            1.76776695296637E-01f, 2.10224103813429E-01f, 2.50000000000000E-01f, 2.97301778750680E-01f, 3.53553390593274E-01f, 4.20448207626857E-01f, 5.00000000000000E-01f, 5.94603557501361E-01f,
            7.07106781186547E-01f, 8.40896415253715E-01f, 1.00000000000000E+00f, 1.18920711500272E+00f, 1.41421356237310E+00f, 1.68179283050743E+00f, 2.00000000000000E+00f, 2.37841423000544E+00f,
            2.82842712474619E+00f, 3.36358566101486E+00f, 4.00000000000000E+00f, 4.75682846001088E+00f, 5.65685424949238E+00f, 6.72717132202972E+00f, 8.00000000000000E+00f, 9.51365692002177E+00f,
            1.13137084989848E+01f, 1.34543426440594E+01f, 1.60000000000000E+01f, 1.90273138400435E+01f, 2.26274169979695E+01f, 2.69086852881189E+01f, 3.20000000000000E+01f, 3.80546276800871E+01f,
            4.52548339959390E+01f, 5.38173705762377E+01f, 6.40000000000000E+01f, 7.61092553601742E+01f, 9.05096679918781E+01f, 1.07634741152475E+02f, 1.28000000000000E+02f, 1.52218510720348E+02f,
            1.81019335983756E+02f, 2.15269482304951E+02f, 2.56000000000000E+02f, 3.04437021440696E+02f, 3.62038671967512E+02f, 4.30538964609902E+02f, 5.12000000000000E+02f, 6.08874042881393E+02f,
            7.24077343935025E+02f, 8.61077929219803E+02f, 1.02400000000000E+03f, 1.21774808576279E+03f, 1.44815468787005E+03f, 1.72215585843961E+03f, 2.04800000000000E+03f, 2.43549617152557E+03f,
        ];

        void ReadSideInfo(IMpegFrame frame)
        {
            if (frame.Version == MpegVersion.Version1)
            {
                // main_data_begin      9
                _mainDataBegin = frame.ReadBits(9);

                // private_bits         3 or 5
                if (frame.ChannelMode == MpegChannelMode.Mono)
                {
                    _privBits = frame.ReadBits(5);
                    _channels = 1;
                }
                else
                {
                    _privBits = frame.ReadBits(3);
                    _channels = 2;
                }

                for (var ch = 0; ch < _channels; ch++)
                {
                    // scfsi[ch][0...3]     1 x4
                    _scfsi[ch][0] = frame.ReadBits(1);
                    _scfsi[ch][1] = frame.ReadBits(1);
                    _scfsi[ch][2] = frame.ReadBits(1);
                    _scfsi[ch][3] = frame.ReadBits(1);
                }

                for (var gr = 0; gr < 2; gr++)
                {
                    for (var ch = 0; ch < _channels; ch++)
                    {
                        // part2_3_length[gr][ch]        12
                        _part23Length[gr][ch] = frame.ReadBits(12);
                        // big_values[gr][ch]            9
                        _bigValues[gr][ch] = frame.ReadBits(9);
                        // global_gain[gr][ch]           8
                        _globalGain[gr][ch] = GAIN_TAB[frame.ReadBits(8)];
                        // scalefac_compress[gr][ch]     4
                        _scalefacCompress[gr][ch] = frame.ReadBits(4);
                        // blocksplit_flag[gr][ch]       1
                        _blockSplitFlag[gr][ch] = frame.ReadBits(1) == 1;
                        if (_blockSplitFlag[gr][ch])
                        {
                            //   block_type[gr][ch]              2
                            _blockType[gr][ch] = frame.ReadBits(2);
                            //   switch_point[gr][ch]            1
                            _mixedBlockFlag[gr][ch] = frame.ReadBits(1) == 1;
                            //   table_select[gr][ch][0..1]      5 x2
                            _tableSelect[gr][ch][0] = frame.ReadBits(5);
                            _tableSelect[gr][ch][1] = frame.ReadBits(5);
                            _tableSelect[gr][ch][2] = 0;
                            // set the region information
                            if (_blockType[gr][ch] == 2 && !_mixedBlockFlag[gr][ch])
                            {
                                _regionAddress1[gr][ch] = 8;
                            }
                            else
                            {
                                _regionAddress1[gr][ch] = 7;
                            }
                            _regionAddress2[gr][ch] = 20 - _regionAddress1[gr][ch];
                            //   subblock_gain[gr][ch][0..2]     3 x3
                            _subblockGain[gr][ch][0] = frame.ReadBits(3) * -2f;
                            _subblockGain[gr][ch][1] = frame.ReadBits(3) * -2f;
                            _subblockGain[gr][ch][2] = frame.ReadBits(3) * -2f;
                        }
                        else
                        {
                            //   table_select[0..2][gr][ch]      5 x3
                            _tableSelect[gr][ch][0] = frame.ReadBits(5);
                            _tableSelect[gr][ch][1] = frame.ReadBits(5);
                            _tableSelect[gr][ch][2] = frame.ReadBits(5);
                            //   region_address1[gr][ch]         4
                            _regionAddress1[gr][ch] = frame.ReadBits(4);
                            //   region_address2[gr][ch]         3
                            _regionAddress2[gr][ch] = frame.ReadBits(3);
                            // set the block type so it doesn't accidentally carry
                            _blockType[gr][ch] = 0;

                            // make subblock gain equal unity
                            _subblockGain[gr][ch][0] = 0;
                            _subblockGain[gr][ch][1] = 0;
                            _subblockGain[gr][ch][2] = 0;
                        }
                        // preflag[gr][ch]               1
                        _preflag[gr][ch] = frame.ReadBits(1);
                        // scalefac_scale[gr][ch]        1
                        _scalefacScale[gr][ch] = .5f * (1f + frame.ReadBits(1));
                        // count1table_select[gr][ch]    1
                        _count1TableSelect[gr][ch] = frame.ReadBits(1);
                    }
                }
            }
            else    // MPEG 2+
            {
                // main_data_begin      8
                _mainDataBegin = frame.ReadBits(8);

                // private_bits         1 or 2
                if (frame.ChannelMode == MpegChannelMode.Mono)
                {
                    _privBits = frame.ReadBits(1);
                    _channels = 1;
                }
                else
                {
                    _privBits = frame.ReadBits(2);
                    _channels = 2;
                }

                var gr = 0;
                for (var ch = 0; ch < _channels; ch++)
                {
                    // part2_3_length[gr][ch]        12
                    _part23Length[gr][ch] = frame.ReadBits(12);
                    // big_values[gr][ch]            9
                    _bigValues[gr][ch] = frame.ReadBits(9);
                    // global_gain[gr][ch]           8
                    _globalGain[gr][ch] = GAIN_TAB[frame.ReadBits(8)];
                    // scalefac_compress[gr][ch]     9
                    _scalefacCompress[gr][ch] = frame.ReadBits(9);
                    // blocksplit_flag[gr][ch]       1
                    _blockSplitFlag[gr][ch] = frame.ReadBits(1) == 1;
                    if (_blockSplitFlag[gr][ch])
                    {
                        //   block_type[gr][ch]              2
                        _blockType[gr][ch] = frame.ReadBits(2);
                        //   switch_point[gr][ch]            1
                        _mixedBlockFlag[gr][ch] = frame.ReadBits(1) == 1;
                        //   table_select[gr][ch][0..1]      5 x2
                        _tableSelect[gr][ch][0] = frame.ReadBits(5);
                        _tableSelect[gr][ch][1] = frame.ReadBits(5);
                        _tableSelect[gr][ch][2] = 0;
                        // set the region information
                        if (_blockType[gr][ch] == 2 && !_mixedBlockFlag[gr][ch])
                        {
                            _regionAddress1[gr][ch] = 8;
                        }
                        else
                        {
                            _regionAddress1[gr][ch] = 7;
                        }
                        _regionAddress2[gr][ch] = 20 - _regionAddress1[gr][ch];
                        //   subblock_gain[gr][ch][0..2]     3 x3
                        _subblockGain[gr][ch][0] = frame.ReadBits(3) * -2f;
                        _subblockGain[gr][ch][1] = frame.ReadBits(3) * -2f;
                        _subblockGain[gr][ch][2] = frame.ReadBits(3) * -2f;
                    }
                    else
                    {
                        //   table_select[0..2][gr][ch]      5 x3
                        _tableSelect[gr][ch][0] = frame.ReadBits(5);
                        _tableSelect[gr][ch][1] = frame.ReadBits(5);
                        _tableSelect[gr][ch][2] = frame.ReadBits(5);
                        //   region_address1[gr][ch]         4
                        _regionAddress1[gr][ch] = frame.ReadBits(4);
                        //   region_address2[gr][ch]         3
                        _regionAddress2[gr][ch] = frame.ReadBits(3);
                        // set the block type so it doesn't accidentally carry
                        _blockType[gr][ch] = 0;

                        // make subblock gain equal unity
                        _subblockGain[gr][ch][0] = 0;
                        _subblockGain[gr][ch][1] = 0;
                        _subblockGain[gr][ch][2] = 0;
                    }
                    // scalefac_scale[gr][ch]        1
                    _scalefacScale[gr][ch] = .5f * (1f + frame.ReadBits(1));
                    // count1table_select[gr][ch]    1
                    _count1TableSelect[gr][ch] = frame.ReadBits(1);
                }
            }
        }

        int[] _sfBandIndexL, _sfBandIndexS;

        // these are byte[] to save memory
        readonly byte[] _cbLookupL = new byte[SSLIMIT * SBLIMIT], _cbLookupS = new byte[SSLIMIT * SBLIMIT], _cbwLookupS = new byte[SSLIMIT * SBLIMIT];
        int _cbLookupSR;

        static readonly int[][] _sfBandIndexLTable = [
                                                         // MPEG 1
                                                         // 44.1 kHz
                                                         [0, 4, 8, 12, 16, 20, 24, 30, 36, 44, 52, 62, 74, 90, 110, 134, 162, 196, 238, 288, 342, 418, 576],
                                                         // 48 kHz
                                                         [0, 4, 8, 12, 16, 20, 24, 30, 36, 42, 50, 60, 72, 88, 106, 128, 156, 190, 230, 276, 330, 384, 576],
                                                         // 32 kHz
                                                         [0, 4, 8, 12, 16, 20, 24, 30, 36, 44, 54, 66, 82, 102, 126, 156, 194, 240, 296, 364, 448, 550, 576],

                                                         // MPEG 2
                                                         // 22.05 kHz
                                                         [0, 6, 12, 18, 24, 30, 36, 44, 54, 66, 80, 96, 116, 140, 168, 200, 238, 284, 336, 396, 464, 522, 576],
                                                         // 24 kHz
                                                         [0, 6, 12, 18, 24, 30, 36, 44, 54, 66, 80, 96, 114, 136, 162, 194, 232, 278, 330, 394, 464, 540, 576],
                                                         // 16 kHz
                                                         [0, 6, 12, 18, 24, 30, 36, 44, 54, 66, 80, 96, 116, 140, 168, 200, 238, 284, 336, 396, 464, 522, 576],

                                                         // MPEG 2.5
                                                         // 11.025 kHz
                                                         [0, 6, 12, 18, 24, 30, 36, 44, 54, 66, 80, 96, 116, 140, 168, 200, 238, 284, 336, 396, 464, 522, 576],
                                                         // 12 kHz
                                                         [0, 6, 12, 18, 24, 30, 36, 44, 54, 66, 80, 96, 116, 140, 168, 200, 238, 284, 336, 396, 464, 522, 576],
                                                         // 8 kHz
                                                         [0, 12, 24, 36, 48, 60, 72, 88, 108, 132, 160, 192, 232, 280, 336, 400, 476, 566, 568, 570, 572, 574, 576],
                                                     ];

        static readonly int[][] _sfBandIndexSTable = [
                                                         // MPEG 1
                                                         // 44.1 kHz
                                                         [0, 4, 8, 12, 16, 22, 30, 40, 52, 66, 84, 106, 136, 192],
                                                         // 48 kHz
                                                         [0, 4, 8, 12, 16, 22, 28, 38, 50, 64, 80, 100, 126, 192],
                                                         // 32 kHz
                                                         [0, 4, 8, 12, 16, 22, 30, 42, 58, 78, 104, 138, 180, 192],

                                                         // MPEG 2
                                                         // 22.05 kHz
                                                         [0, 4, 8, 12, 18, 24, 32, 42, 56, 74, 100, 132, 174, 192],
                                                         // 24 kHz
                                                         [0, 4, 8, 12, 18, 26, 36, 48, 62, 80, 104, 136, 180, 192],
                                                         // 16 kHz
                                                         [0, 4, 8, 12, 18, 26, 36, 48, 62, 80, 104, 134, 174, 192],

                                                         // MPEG 2.5
                                                         // 11.025 kHz
                                                         [0, 4, 8, 12, 18, 26, 36, 48, 62, 80, 104, 134, 174, 192],
                                                         // 12 kHz
                                                         [0, 4, 8, 12, 18, 26, 36, 48, 62, 80, 104, 134, 174, 192],
                                                         // 8 kHz
                                                         [0, 8, 16, 24, 36, 52, 72, 96, 124, 160, 162, 164, 166, 192],
                                                     ];

        void PrepTables(IMpegFrame frame)
        {
            if (_cbLookupSR != frame.SampleRate)
            {
                switch (frame.SampleRate)
                {
                    case 44100:
                        _sfBandIndexL = _sfBandIndexLTable[0];
                        _sfBandIndexS = _sfBandIndexSTable[0];
                        break;
                    case 48000:
                        _sfBandIndexL = _sfBandIndexLTable[1];
                        _sfBandIndexS = _sfBandIndexSTable[1];
                        break;
                    case 32000:
                        _sfBandIndexL = _sfBandIndexLTable[2];
                        _sfBandIndexS = _sfBandIndexSTable[2];
                        break;

                    case 22050:
                        _sfBandIndexL = _sfBandIndexLTable[3];
                        _sfBandIndexS = _sfBandIndexSTable[3];
                        break;
                    case 24000:
                        _sfBandIndexL = _sfBandIndexLTable[4];
                        _sfBandIndexS = _sfBandIndexSTable[4];
                        break;
                    case 16000:
                        _sfBandIndexL = _sfBandIndexLTable[5];
                        _sfBandIndexS = _sfBandIndexSTable[5];
                        break;

                    case 11025:
                        _sfBandIndexL = _sfBandIndexLTable[6];
                        _sfBandIndexS = _sfBandIndexSTable[6];
                        break;
                    case 12000:
                        _sfBandIndexL = _sfBandIndexLTable[7];
                        _sfBandIndexS = _sfBandIndexSTable[7];
                        break;
                    case 8000:
                        _sfBandIndexL = _sfBandIndexLTable[8];
                        _sfBandIndexS = _sfBandIndexSTable[8];
                        break;
                }

                // precalculate the critical bands per bucket
                int cbL = 0, cbS = 0;
                int next_cbL = _sfBandIndexL[1], next_cbS = _sfBandIndexS[1] * 3;
                for (int i = 0; i < 576; i++)
                {
                    if (i == next_cbL)
                    {
                        ++cbL;
                        next_cbL = _sfBandIndexL[cbL + 1];
                    }
                    if (i == next_cbS)
                    {
                        ++cbS;
                        next_cbS = _sfBandIndexS[cbS + 1] * 3;
                    }
                    _cbLookupL[i] = (byte)cbL;
                    _cbLookupS[i] = (byte)cbS;
                }

                // set up the short block windows
                int idx = 0;
                for (cbS = 0; cbS < 12; cbS++)
                {
                    var width = _sfBandIndexS[cbS + 1] - _sfBandIndexS[cbS];
                    for (int i = 0; i < 3; i++)
                    {
                        for (int j = 0; j < width; j++, idx++)
                        {
                            _cbwLookupS[idx] = (byte)i;
                        }
                    }
                }

                _cbLookupSR = frame.SampleRate;
            }
        }

        readonly int[][][] _scalefac = [   // ch, window, cb
                                  [new int[13], new int[13], new int[13], new int[23]],
                                  [new int[13], new int[13], new int[13], new int[23]]
                              ];

        static readonly int[][] _slen = [
                                            [0, 0, 0, 0, 3, 1, 1, 1, 2, 2, 2, 3, 3, 3, 4, 4],
                                            [0, 1, 2, 3, 0, 1, 2, 3, 1, 2, 3, 1, 2, 3, 2, 3]
                                        ];

        static readonly int[][][] _sfbBlockCntTab = [
                                                        [[6, 5, 5, 5],   [9, 9, 9, 9],    [6, 9, 9, 9]],
                                                        [[6, 5, 7, 3],   [9, 9, 12, 6],   [6, 9, 12, 6]],
                                                        [[11, 10, 0, 0], [18, 18, 0, 0],  [15, 18, 0, 0]],
                                                        [[7, 7, 7, 0],   [12, 12, 12, 0], [6, 15, 12, 0]],
                                                        [[6, 6, 6, 3],   [12, 9, 9, 6],   [6, 12, 9, 6]],
                                                        [[8, 8, 5, 0],   [15, 12, 9, 0],  [6, 18, 9, 0]],
                                                    ];

        int ReadScalefactors(int gr, int ch)
        {
            var slen0 = _slen[0][_scalefacCompress[gr][ch]];
            var slen1 = _slen[1][_scalefacCompress[gr][ch]];
            int bits;

            int cb = 0;
            if (_blockSplitFlag[gr][ch] && _blockType[gr][ch] == 2)
            {
                if (slen0 > 0)
                {
                    bits = slen0 * 18;

                    if (_mixedBlockFlag[gr][ch])
                    {
                        // mixed has bands 0..7 of long, then 3..11 of short
                        for (; cb < 8; cb++)
                        {
                            _scalefac[ch][3][cb] = _bitRes.GetBits(slen0);
                        }
                        cb = 3;
                        bits -= slen0;  // mixed blocks need slen0 fewer bits
                    }

                    // short / mixed: just read from wherever cb happens to be through 11
                    for (; cb < 6; cb++)
                    {
                        _scalefac[ch][0][cb] = _bitRes.GetBits(slen0);
                        _scalefac[ch][1][cb] = _bitRes.GetBits(slen0);
                        _scalefac[ch][2][cb] = _bitRes.GetBits(slen0);
                    }
                }
                else
                {
                    Array.Clear(_scalefac[ch][3], 0, 8);
                    Array.Clear(_scalefac[ch][0], 0, 6);
                    Array.Clear(_scalefac[ch][1], 0, 6);
                    Array.Clear(_scalefac[ch][2], 0, 6);
                    bits = 0;
                }

                if (slen1 > 0)
                {
                    bits += slen1 * 18;

                    for (cb = 6; cb < 12; cb++)
                    {
                        _scalefac[ch][0][cb] = _bitRes.GetBits(slen1);
                        _scalefac[ch][1][cb] = _bitRes.GetBits(slen1);
                        _scalefac[ch][2][cb] = _bitRes.GetBits(slen1);
                    }
                }
                else
                {
                    Array.Clear(_scalefac[ch][0], 6, 6);
                    Array.Clear(_scalefac[ch][1], 6, 6);
                    Array.Clear(_scalefac[ch][2], 6, 6);
                }
            }
            else
            {
                // long: read if gr == 0, otherwise honor scfsi for the channel
                bits = 0;
                if (gr == 0 || _scfsi[ch][0] == 0)
                {
                    if (slen0 > 0)
                    {
                        bits += slen0 * 6;
                        _scalefac[ch][3][0] = _bitRes.GetBits(slen0);
                        _scalefac[ch][3][1] = _bitRes.GetBits(slen0);
                        _scalefac[ch][3][2] = _bitRes.GetBits(slen0);
                        _scalefac[ch][3][3] = _bitRes.GetBits(slen0);
                        _scalefac[ch][3][4] = _bitRes.GetBits(slen0);
                        _scalefac[ch][3][5] = _bitRes.GetBits(slen0);
                    }
                    else
                    {
                        Array.Clear(_scalefac[ch][3], 0, 6);
                    }
                }
                if (gr == 0 || _scfsi[ch][1] == 0)
                {
                    if (slen0 > 0)
                    {
                        bits += slen0 * 5;
                        _scalefac[ch][3][6] = _bitRes.GetBits(slen0);
                        _scalefac[ch][3][7] = _bitRes.GetBits(slen0);
                        _scalefac[ch][3][8] = _bitRes.GetBits(slen0);
                        _scalefac[ch][3][9] = _bitRes.GetBits(slen0);
                        _scalefac[ch][3][10] = _bitRes.GetBits(slen0);
                    }
                    else
                    {
                        Array.Clear(_scalefac[ch][3], 6, 5);
                    }
                }
                if (gr == 0 || _scfsi[ch][2] == 0)
                {
                    if (slen1 > 0)
                    {
                        bits += slen1 * 5;
                        _scalefac[ch][3][11] = _bitRes.GetBits(slen1);
                        _scalefac[ch][3][12] = _bitRes.GetBits(slen1);
                        _scalefac[ch][3][13] = _bitRes.GetBits(slen1);
                        _scalefac[ch][3][14] = _bitRes.GetBits(slen1);
                        _scalefac[ch][3][15] = _bitRes.GetBits(slen1);
                    }
                    else
                    {
                        Array.Clear(_scalefac[ch][3], 11, 5);
                    }
                }
                if (gr == 0 || _scfsi[ch][3] == 0)
                {
                    if (slen1 > 0)
                    {
                        bits += slen1 * 5;
                        _scalefac[ch][3][16] = _bitRes.GetBits(slen1);
                        _scalefac[ch][3][17] = _bitRes.GetBits(slen1);
                        _scalefac[ch][3][18] = _bitRes.GetBits(slen1);
                        _scalefac[ch][3][19] = _bitRes.GetBits(slen1);
                        _scalefac[ch][3][20] = _bitRes.GetBits(slen1);
                    }
                    else
                    {
                        Array.Clear(_scalefac[ch][3], 16, 5);
                    }
                }
            }

            return bits;
        }

        int ReadLsfScalefactors(int gr, int ch, int chanModeExt)
        {
            var sfc = _scalefacCompress[gr][ch];

            int blockTypeNumber;
            // block type number = 2 if mixed short, 1 if pure short, otherwise 0
            if (_blockType[gr][ch] == 2)
            {
                if (_mixedBlockFlag[gr][ch])
                {
                    blockTypeNumber = 2;
                }
                else
                {
                    blockTypeNumber = 1;
                }
            }
            else
            {
                blockTypeNumber = 0;
            }

            int[] slen = new int[4];
            int blockNumber;
            if ((chanModeExt & 1) == 1 && ch == 1)
            {
                var tsfc = sfc >> 1;
                if (tsfc < 180)
                {
                    slen[0] = tsfc / 36;                    // <= 4, 15
                    slen[1] = (tsfc % 36) / 6;              // <= 5, 31
                    slen[2] = tsfc % 6;                     // <= 5, 31
                    slen[3] = 0;
                    _preflag[gr][ch] = 0;
                    blockNumber = 3;
                }
                else if (tsfc < 244)
                {
                    slen[0] = ((tsfc - 180) % 64) >> 4;     // <= 3, 7
                    slen[1] = ((tsfc - 180) % 16) >> 2;     // <= 3, 7
                    slen[2] = ((tsfc - 180) % 4);           // <= 3, 7
                    slen[3] = 0;
                    _preflag[gr][ch] = 0;
                    blockNumber = 4;
                }
                else if (tsfc < 255)
                {
                    slen[0] = (tsfc - 244) / 3;             // <= 3, 7
                    slen[1] = (tsfc - 244) % 3;             // <= 1, 1
                    slen[2] = 0;
                    slen[3] = 0;
                    _preflag[gr][ch] = 0;
                    blockNumber = 5;
                }
                else
                {
                    blockNumber = 0;
                }
            }
            else
            {
                //   if scalefac_comp < 400
                if (sfc < 400)
                {
                    slen[0] = (sfc >> 4) / 5;               // <= 4, 15
                    slen[1] = (sfc >> 4) % 5;               // <= 4, 15
                    slen[2] = (sfc & 15) >> 2;              // <= 3, 7
                    slen[3] = sfc & 3;                      // <= 3, 7
                    _preflag[gr][ch] = 0;
                    blockNumber = 0;
                }
                else if (sfc < 500)
                {
                    slen[0] = ((sfc - 400) >> 2) / 5;       // <= 4, 15
                    slen[1] = ((sfc - 400) >> 2) % 5;       // <= 4, 15
                    slen[2] = (sfc - 400) & 3;              // <= 3, 7
                    slen[3] = 0;
                    _preflag[gr][ch] = 0;
                    blockNumber = 1;
                }
                else if (sfc < 512)
                {
                    slen[0] = (sfc - 500) / 3;              // <= 3, 7
                    slen[1] = (sfc - 500) % 3;              // <= 2, 3
                    slen[2] = 0;
                    slen[3] = 0;
                    _preflag[gr][ch] = 1;
                    blockNumber = 2;
                }
                else
                {
                    blockNumber = 0;
                }
            }

            // now we populate our buffer...
            var buffer = new int[54];

            var k = 0;
            var blkCnt = _sfbBlockCntTab[blockNumber][blockTypeNumber];
            for (int i = 0; i < 4; i++)
            {
                if (slen[i] != 0)
                {
                    for (int j = 0; j < blkCnt[i]; j++, k++)
                    {
                        buffer[k] = _bitRes.GetBits(slen[i]);
                    }
                }
                else
                {
                    k += blkCnt[i];
                }
            }

            // now that we have that done, let's assign our scalefactors
            k = 0;
            int sfb = 0;
            if (_blockSplitFlag[gr][ch] && _blockType[gr][ch] == 2)
            {
                if (_mixedBlockFlag[gr][ch])
                {
                    for (; sfb < 8; sfb++)
                    {
                        _scalefac[ch][3][sfb] = buffer[k++];
                    }
                    sfb = 3;
                }

                for (; sfb < 12; sfb++)
                {
                    for (int window = 0; window < 3; window++)
                    {
                        _scalefac[ch][window][sfb] = buffer[k++];
                    }
                }
                _scalefac[ch][0][12] = 0;
                _scalefac[ch][1][12] = 0;
                _scalefac[ch][2][12] = 0;
            }
            else
            {
                for (; sfb < 21; sfb++)
                {
                    _scalefac[ch][3][sfb] = buffer[k++];
                }
                _scalefac[ch][3][22] = 0;
            }

            return slen[0] * blkCnt[0] + slen[1] * blkCnt[1] + slen[2] * blkCnt[2] + slen[3] * blkCnt[3];
        }

        readonly float[][] _samples = [new float[SSLIMIT * SBLIMIT + 3], new float[SSLIMIT * SBLIMIT + 3]];

        static readonly int[] PRETAB = [0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 1, 1, 1, 1, 2, 2, 3, 3, 3, 2, 0];

        static readonly float[] POW2_TAB =
        [
            1.000000000000000E-00f, 7.071067811865470E-01f, 5.000000000000000E-01f, 3.535533905932740E-01f, 2.500000000000000E-01f, 1.767766952966370E-01f, 1.250000000000000E-01f, 8.838834764831840E-02f,
            6.250000000000000E-02f, 4.419417382415920E-02f, 3.125000000000000E-02f, 2.209708691207960E-02f, 1.562500000000000E-02f, 1.104854345603980E-02f, 7.812500000000000E-03f, 5.524271728019900E-03f,
            3.906250000000000E-03f, 2.762135864009950E-03f, 1.953125000000000E-03f, 1.381067932004980E-03f, 9.765625000000000E-04f, 6.905339660024880E-04f, 4.882812500000000E-04f, 3.452669830012440E-04f,
            2.441406250000000E-04f, 1.726334915006220E-04f, 1.220703125000000E-04f, 8.631674575031100E-05f, 6.103515625000000E-05f, 4.315837287515550E-05f, 3.051757812500000E-05f, 2.157918643757770E-05f,
            1.525878906250000E-05f, 1.078959321878890E-05f, 7.629394531250000E-06f, 5.394796609394440E-06f, 3.814697265625000E-06f, 2.697398304697220E-06f, 1.907348632812500E-06f, 1.348699152348610E-06f,
            9.536743164062500E-07f, 6.743495761743050E-07f, 4.768371582031250E-07f, 3.371747880871520E-07f, 2.384185791015620E-07f, 1.685873940435760E-07f, 1.192092895507810E-07f, 8.429369702178800E-08f,
            5.960464477539060E-08f, 4.214684851089410E-08f, 2.980232238769530E-08f, 2.107342425544710E-08f, 1.490116119384770E-08f, 1.053671212772350E-08f, 7.450580596923830E-09f, 5.268356063861760E-09f,
            3.725290298461910E-09f, 2.634178031930880E-09f, 1.862645149230960E-09f, 1.317089015965440E-09f, 9.313225746154790E-10f, 6.585445079827190E-10f, 4.656612873077390E-10f, 3.292722539913600E-10f,
        ];

        void ReadSamples(int sfBits, int gr, int ch)
        {
            int region1Start, region2Start;
            if (_blockSplitFlag[gr][ch] && _blockType[gr][ch] == 2)
            {
                region1Start = 36;
                region2Start = 576;
            }
            else
            {
                region1Start = _sfBandIndexL[_regionAddress1[gr][ch] + 1];
                region2Start = _sfBandIndexL[Math.Min(_regionAddress1[gr][ch] + _regionAddress2[gr][ch] + 2, 22)];
            }

            var part3end = _bitRes.BitsRead - sfBits + _part23Length[gr][ch];

            int idx = 0, h = _tableSelect[gr][ch][0];

            // bigvalues section
            int bigValueCount = _bigValues[gr][ch] * 2;
            float x, y;
            while (idx < bigValueCount && idx < region1Start)
            {
                Huffman.Decode(_bitRes, h, out x, out y);
                _samples[ch][idx] = Dequantize(idx, x, gr, ch); ++idx;
                _samples[ch][idx] = Dequantize(idx, y, gr, ch); ++idx;
            }
            h = _tableSelect[gr][ch][1];
            while (idx < bigValueCount && idx < region2Start)
            {
                Huffman.Decode(_bitRes, h, out x, out y);
                _samples[ch][idx] = Dequantize(idx, x, gr, ch); ++idx;
                _samples[ch][idx] = Dequantize(idx, y, gr, ch); ++idx;
            }
            h = _tableSelect[gr][ch][2];
            while (idx < bigValueCount)
            {
                Huffman.Decode(_bitRes, h, out x, out y);
                _samples[ch][idx] = Dequantize(idx, x, gr, ch); ++idx;
                _samples[ch][idx] = Dequantize(idx, y, gr, ch); ++idx;
            }

            // count1 section
            h = _count1TableSelect[gr][ch] + 32;

            // - 3 to ensure that we never get an out of range exception
            while (part3end > _bitRes.BitsRead && idx < SBLIMIT * SSLIMIT - 3)
            {
                Huffman.Decode(_bitRes, h, out x, out y, out float v, out float w);
                _samples[ch][idx] = Dequantize(idx, v, gr, ch); ++idx;
                _samples[ch][idx] = Dequantize(idx, w, gr, ch); ++idx;
                _samples[ch][idx] = Dequantize(idx, x, gr, ch); ++idx;
                _samples[ch][idx] = Dequantize(idx, y, gr, ch); ++idx;
            }

            // adjust the bit stream if we're off somehow
            if (_bitRes.BitsRead > part3end)
            {
                _bitRes.RewindBits((int)(_bitRes.BitsRead - part3end));

                idx -= 4;
                if (idx < 0) idx = 0;
            }

            if (_bitRes.BitsRead < part3end)
            {
                _bitRes.SkipBits((int)(part3end - _bitRes.BitsRead));
            }

            // zero out the highest samples (defined as 0 in the standard)
            if (idx < SBLIMIT * SSLIMIT)
            {
                Array.Clear(_samples[ch], idx, SBLIMIT * SSLIMIT + 3 - idx);
            }
        }

        float Dequantize(int idx, float val, int gr, int ch)
        {
            if (val != 0f)
            {
                int cb, window;

                if (_blockSplitFlag[gr][ch] && _blockType[gr][ch] == 2 && !(_mixedBlockFlag[gr][ch] && idx < _sfBandIndexL[8]))
                {
                    // short / mixed short section
                    cb = _cbLookupS[idx];
                    window = _cbwLookupS[idx];

                    return val * _globalGain[gr][ch] * POW2_TAB[(int)(-2 * (_subblockGain[gr][ch][window] - (_scalefacScale[gr][ch] * _scalefac[ch][window][cb])))];
                }
                else
                {
                    // long / mixed long section
                    cb = _cbLookupL[idx];

                    return val * _globalGain[gr][ch] * POW2_TAB[(int)(2 * _scalefacScale[gr][ch] * (_scalefac[ch][3][cb] + _preflag[gr][ch] * PRETAB[cb]))];
                }

            }
            return 0f;
        }

        static readonly float[][] _isRatio = [
                                                 [0f, 0.211324865405187f, 0.366025403784439f, 0.5f, 0.633974596215561f, 0.788675134594813f, 1f],
                                                 [1f, 0.788675134594813f, 0.633974596215561f, 0.5f, 0.366025403784439f, 0.211324865405187f, 0f]
                                             ];

        static readonly float[][][] _lsfRatio = [   // sfc%2, ch, isPos
                                                    [
                                                        [1f, 0.840896415256f, 1f, 0.707106781190391f, 1f, 0.594603557506209f, 1f, 0.500000000005436f, 1f, 0.420448207632571f, 1f, 0.353553390599039f, 1f, 0.297301778756337f, 1f, 0.250000000005436f, 1f, 0.210224103818571f, 1f, 0.176776695301441f, 1f, 0.148650889379784f, 1f, 0.125000000004077f, 1f, 0.105112051910428f, 1f, 0.0883883476516816f, 1f, 0.0743254446907002f, 1f, 0.0625000000027179f],
                                                        [1f, 1f, 0.840896415256f, 1f, 0.707106781190391f, 1f, 0.594603557506209f, 1f, 0.500000000005436f, 1f, 0.420448207632571f, 1f, 0.353553390599039f, 1f, 0.297301778756337f, 1f, 0.250000000005436f, 1f, 0.210224103818571f, 1f, 0.176776695301441f, 1f, 0.148650889379784f, 1f, 0.125000000004077f, 1f, 0.105112051910428f, 1f, 0.0883883476516816f, 1f, 0.0743254446907002f, 1f],
                                                    ],
                                                    [
                                                        [1f, 0.707106781188f, 1f, 0.500000000002054f, 1f, 0.353553390595452f, 1f, 0.250000000002054f, 1f, 0.176776695298452f, 1f, 0.125000000001541f, 1f, 0.0883883476495893f, 1f, 0.062500000001027f, 1f, 0.0441941738249762f, 1f, 0.0312500000006419f, 1f, 0.0220970869125789f, 1f, 0.0156250000003851f, 1f, 0.0110485434563348f, 1f, 0.00781250000022466f, 1f, 0.00552427172819011f, 1f, 0.00390625000012838f],
                                                        [1f, 1f, 0.707106781188f, 1f, 0.500000000002054f, 1f, 0.353553390595452f, 1f, 0.250000000002054f, 1f, 0.176776695298452f, 1f, 0.125000000001541f, 1f, 0.0883883476495893f, 1f, 0.062500000001027f, 1f, 0.0441941738249762f, 1f, 0.0312500000006419f, 1f, 0.0220970869125789f, 1f, 0.0156250000003851f, 1f, 0.0110485434563348f, 1f, 0.00781250000022466f, 1f, 0.00552427172819011f, 1f],
                                                    ],
                                                ];


        void Stereo(MpegChannelMode channelMode, int chanModeExt, int gr, bool lsf)
        {
            // do the stereo decoding as needed...  This really only applies in two cases:
            //  1) Joint Stereo and one (or both) of the extensions are enabled, or
            //  2) We're doing a downmix to mono

            if (channelMode == MpegChannelMode.JointStereo && chanModeExt != 0)
            {
                var midSide = (chanModeExt & 0x2) == 2;

                if ((chanModeExt & 0x1) == 1)
                {
                    // do the intensity stereo processing


                    // find the highest sample index with a value in channel 1
                    //   - now each step only has to start from there
                    int lastValueIdx = -1;
                    for (int i = SBLIMIT * SSLIMIT - (SBLIMIT + 1); i >= 0; i--)
                    {
                        if (_samples[1][i] != 0f)
                        {
                            lastValueIdx = i;
                            break;
                        }
                    }

                    // figure up which passes we'll need and for which ranges
                    int lEnd = -1, sStart = -1;
                    if (_blockSplitFlag[gr][0] && _blockType[gr][0] == 2)
                    {
                        if (_mixedBlockFlag[gr][0])
                        {
                            // 0 through 8 of long, then 3 through 12 of short
                            if (lastValueIdx < _sfBandIndexL[8])
                            {
                                lEnd = 8;
                            }
                            sStart = 3;
                        }
                        else
                        {
                            // 0 through 12 of short
                            sStart = 0;
                        }
                    }
                    else
                    {
                        // 0 through 21 of long
                        lEnd = 21;
                    }

                    // long processing is far simpler than short...  just process from the start of the scalefactor band after the last non-zero sample
                    // we also don't have to mess with "finding" again; it was done above
                    var sfb = 0;
                    if (lastValueIdx > -1)
                    {
                        sfb = _cbLookupL[lastValueIdx] + 1;
                    }

                    // make sure we do the mid/side processing on the lower bands (if needed)
                    if (sfb > 0 && sStart == -1)
                    {
                        if (midSide)
                        {
                            ApplyMidSide(0, _sfBandIndexL[sfb]);
                        }
                        else
                        {
                            ApplyFullStereo(0, _sfBandIndexL[sfb]);
                        }
                    }

                    // now process the intensity bands
                    for (; sfb < lEnd; sfb++)
                    {
                        var i = _sfBandIndexL[sfb];
                        var width = _sfBandIndexL[sfb + 1] - _sfBandIndexL[sfb];
                        var isPos = _scalefac[1][3][sfb];
                        if (isPos == 7)
                        {
                            if (midSide)
                            {
                                ApplyMidSide(i, width);
                            }
                            else
                            {
                                ApplyFullStereo(i, width);
                            }
                        }
                        else if (lsf)
                        {
                            ApplyLsfIStereo(i, width, isPos, _scalefacCompress[gr][0]);
                        }
                        else
                        {
                            ApplyIStereo(i, width, isPos);
                        }
                    }

                    if (sStart <= -1)
                    {
                        // do final long processing
                        var isPos = _scalefac[1][3][20];
                        if (isPos == 7)
                        {
                            if (midSide)
                            {
                                ApplyMidSide(_sfBandIndexL[21], 576 - _sfBandIndexL[21]);
                            }
                            else
                            {
                                ApplyFullStereo(_sfBandIndexL[21], 576 - _sfBandIndexL[21]);
                            }
                        }
                        else if (lsf)
                        {
                            ApplyLsfIStereo(_sfBandIndexL[21], 576 - _sfBandIndexL[21], isPos, _scalefacCompress[gr][0]);
                        }
                        else
                        {
                            ApplyIStereo(_sfBandIndexL[21], 576 - _sfBandIndexL[21], isPos);
                        }
                    }

                    // short processing requires that each window be looked at separately... they are interleaved, so it gets really interesting...
                    // on the plus side, whichever window the {lastValueIdx} is in is already found... :)
                    else
                    {
                        // find where each window starts intensity processing
                        var sSfb = new int[] { -1, -1, -1 };
                        int window;
                        if (lastValueIdx > -1)
                        {
                            sfb = _cbLookupS[lastValueIdx];
                            window = _cbwLookupS[lastValueIdx];
                            sSfb[window] = sfb;
                        }
                        else
                        {
                            sfb = 12;
                            window = 3; // NB: 3 is correct!
                        }

                        window = (window - 1) % 3;
                        for (; sfb >= sStart && window >= 0; window = (window - 1) % 3)
                        {
                            if (sSfb[window] != -1)
                            {
                                if (sSfb[0] != -1 && sSfb[1] != -1 && sSfb[2] != -1)
                                {
                                    break;
                                }
                                continue;
                            }

                            var width = _sfBandIndexS[sfb + 1] - _sfBandIndexS[sfb];
                            var i = _sfBandIndexS[sfb] * 3 + width * (window + 1);

                            while (--width >= -1)
                            {
                                if (_samples[1][--i] != 0f)
                                {
                                    sSfb[window] = sfb;
                                    break;
                                }
                            }

                            if (window == 0)
                            {
                                --sfb;
                            }
                        }

                        // now apply the intensity processing for each window & scalefactor band
                        sfb = sStart;
                        for (; sfb < 12; sfb++)
                        {
                            var width = _sfBandIndexS[sfb + 1] - _sfBandIndexS[sfb];
                            var i = _sfBandIndexS[sfb] * 3;

                            for (window = 0; window < 3; window++)
                            {
                                if (sfb > sSfb[window])
                                {
                                    var isPos = _scalefac[1][window][sfb];
                                    if (isPos == 7)
                                    {
                                        if (midSide)
                                        {
                                            ApplyMidSide(i, width);
                                        }
                                        else
                                        {
                                            ApplyFullStereo(i, width);
                                        }
                                    }
                                    else if (lsf)
                                    {
                                        ApplyLsfIStereo(i, width, isPos, _scalefacCompress[gr][0]);
                                    }
                                    else
                                    {
                                        ApplyIStereo(i, width, isPos);
                                    }
                                }
                                else if (midSide)
                                {
                                    ApplyMidSide(i, width);
                                }
                                else
                                {
                                    ApplyFullStereo(i, width);
                                }

                                i += width;
                            }
                        }

                        // do final short processing
                        var finalWidth = _sfBandIndexS[13] - _sfBandIndexS[12];
                        for (window = 0; window < 3; window++)
                        {
                            var isPos = _scalefac[1][window][11];
                            if (isPos == 7)
                            {
                                if (midSide)
                                {
                                    ApplyMidSide(_sfBandIndexS[11] * 3 + finalWidth * window, finalWidth);
                                }
                                else
                                {
                                    ApplyFullStereo(_sfBandIndexS[11] * 3 + finalWidth * window, finalWidth);
                                }
                            }
                            else if (lsf)
                            {
                                ApplyLsfIStereo(_sfBandIndexS[11] * 3 + finalWidth * window, finalWidth, isPos, _scalefacCompress[gr][0]);
                            }
                            else
                            {
                                ApplyIStereo(_sfBandIndexS[11] * 3 + finalWidth * window, finalWidth, isPos);
                            }
                        }
                    }
                }
                else if (midSide)
                {
                    // just do mid/side processing for everything
                    ApplyMidSide(0, SBLIMIT * SSLIMIT);
                }
                else
                {
                    // this is a no-op most of the time
                    ApplyFullStereo(0, SSLIMIT * SBLIMIT);
                }
            }
            else if (_channels != 1)
            {
                // this is a no-op most of the time
                ApplyFullStereo(0, SSLIMIT * SBLIMIT);
            }
        }

        void ApplyIStereo(int i, int sb, int isPos)
        {
            if (StereoMode == StereoMode.DownmixToMono)
            {
                for (; sb > 0; sb--, i++)
                {
                    _samples[0][i] /= 2f; // scale appropriately
                }
            }
            else
            {
                var ratio0 = _isRatio[0][isPos];
                var ratio1 = _isRatio[1][isPos];
                for (; sb > 0; sb--, i++)
                {
                    _samples[1][i] = _samples[0][i] * ratio1;
                    _samples[0][i] *= ratio0;
                }
            }
        }

        void ApplyLsfIStereo(int i, int sb, int isPos, int scalefacCompress)
        {
            var k0 = _lsfRatio[scalefacCompress % 1][isPos][0];
            var k1 = _lsfRatio[scalefacCompress % 1][isPos][1];
            if (StereoMode == XLayer.StereoMode.DownmixToMono)
            {
                var ratio = 1 / (k0 + k1);
                for (; sb > 0; sb--, i++)
                {
                    _samples[0][i] *= ratio;
                }
            }
            else
            {
                for (; sb > 0; sb--, i++)
                {
                    _samples[1][i] = _samples[0][i] * k1;
                    _samples[0][i] *= k0;
                }
            }
        }

        void ApplyMidSide(int i, int sb)
        {
            if (StereoMode == StereoMode.DownmixToMono)
            {
                for (; sb > 0; sb--, i++)
                {
                    _samples[0][i] *= 0.707106781f; // scale appropriately
                }
            }
            else
            {
                for (; sb > 0; sb--, i++)
                {
                    // apply the mid/side
                    var a = _samples[0][i];
                    var b = _samples[1][i];
                    _samples[0][i] = (a + b) * 0.707106781f;
                    _samples[1][i] = (a - b) * 0.707106781f;
                }
            }
        }

        void ApplyFullStereo(int i, int sb)
        {
            if (StereoMode == XLayer.StereoMode.DownmixToMono)
            {
                for (; sb > 0; sb--, i++)
                {
                    _samples[0][i] = (_samples[0][i] + _samples[1][i]) / 2f;
                }
            }
        }

        readonly float[] _reorderBuf = new float[SBLIMIT * SSLIMIT];

        void Reorder(float[] buf, bool mixedBlock)
        {
            // reorder into _reorderBuf, then copy back
            int sfb = 0;

            if (mixedBlock)
            {
                // mixed... copy the first two bands and reorder the rest
                Array.Copy(buf, 0, _reorderBuf, 0, SSLIMIT * 2);

                sfb = 3;
            }

            while (sfb < 13)
            {
                int sfb_start = _sfBandIndexS[sfb];
                int sfb_lines = _sfBandIndexS[sfb + 1] - sfb_start;

                for (int window = 0; window < 3; window++)
                {
                    for (int freq = 0; freq < sfb_lines; freq++)
                    {
                        var src_line = sfb_start * 3 + window * sfb_lines + freq;
                        var des_line = (sfb_start * 3) + window + (freq * 3);
                        _reorderBuf[des_line] = buf[src_line];
                    }
                }

                ++sfb;
            }

            Array.Copy(_reorderBuf, buf, SSLIMIT * SBLIMIT);
        }

        static readonly float[] _scs = [
                                        0.85749292571254400f,  0.88174199731770500f,  0.94962864910273300f,  0.98331459249179000f,
                                        0.99551781606758600f,  0.99916055817814800f,  0.99989919524444700f,  0.99999315507028000f,
                                       ];

        static readonly float[] _sca = [
                                       -0.51449575542752700f, -0.47173196856497200f, -0.31337745420390200f, -0.18191319961098100f,
                                       -0.09457419252642070f, -0.04096558288530410f, -0.01419856857247120f, -0.00369997467376004f,
                                       ];

        static void AntiAlias(float[] buf, bool mixedBlock)
        {
            int sblim;
            if (mixedBlock)
            {
                sblim = 1;
            }
            else
            {
                sblim = SBLIMIT - 1;
            }

            for (int sb = 0, offset = 0; sb < sblim; sb++, offset += SSLIMIT)
            {
                for (int ss = 0, buOfs = offset + SSLIMIT - 1, bdOfs = offset + SSLIMIT; ss < 8; ss++, buOfs--, bdOfs++)
                {
                    var bu = buf[buOfs];
                    var bd = buf[bdOfs];
                    buf[buOfs] = (bu * _scs[ss]) - (bd * _sca[ss]);
                    buf[bdOfs] = (bd * _scs[ss]) + (bu * _sca[ss]);
                }
            }
        }

        static void FrequencyInversion(float[] buf)
        {
            for (int ss = 1; ss < SSLIMIT; ss += 2)
            {
                for (int sb = 1; sb < SBLIMIT; sb += 2)
                {
                    buf[sb * SSLIMIT + ss] = -buf[sb * SSLIMIT + ss];
                }
            }
        }

        readonly float[] _polyPhase = new float[SBLIMIT];

        // Layer III interleaves the samples, so we have to make them linear again
        void InversePolyphase(float[] buf, int ch, int ofs, float[] outBuf)
        {
            for (int ss = 0; ss < SSLIMIT; ss++, ofs += SBLIMIT)
            {
                for (int sb = 0; sb < SBLIMIT; sb++)
                {
                    _polyPhase[sb] = buf[sb * SSLIMIT + ss];
                }

                base.InversePolyPhase(ch, _polyPhase);
                Array.Copy(_polyPhase, 0, outBuf, ofs, SBLIMIT);
            }
        }
    }
}
