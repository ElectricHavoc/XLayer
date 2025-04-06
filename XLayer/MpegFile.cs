using System;
using System.Threading;
using System.Runtime.InteropServices;

namespace XLayer
{
    public class MpegFile : IDisposable
    {
        private bool _closeStream, _eofFound;
        private Decoder.MpegStreamReader _reader;
        private MpegFrameDecoder _decoder;
        private System.IO.Stream _stream;
        private readonly Lock _seekLock = new();
        private long _position;

        public MpegFile(string fileName) => Init(System.IO.File.Open(fileName, System.IO.FileMode.Open, System.IO.FileAccess.Read, System.IO.FileShare.Read), true);

        public MpegFile(System.IO.Stream stream) => Init(stream, false);

        private void Init(System.IO.Stream stream, bool closeStream)
        {
            _stream = stream;
            _closeStream = closeStream;
            _reader = new Decoder.MpegStreamReader(_stream);
            _decoder = new MpegFrameDecoder();
        }

        public void Dispose()
        {
            if (_closeStream)
            {
                GC.SuppressFinalize(this);
                _stream.Dispose();
                _closeStream = false;
            }
        }

        public int SampleRate => _reader.SampleRate;
        public int Channels => _reader.Channels;
        public bool CanSeek => _reader.CanSeek;
        public long Length => _reader.SampleCount * _reader.Channels;

        public TimeSpan Duration
        {
            get
            {
                long len = _reader.SampleCount;
                if (len == -1) return TimeSpan.Zero;
                return TimeSpan.FromSeconds((double)len / _reader.SampleRate);
            }
        }

        public long Position
        {
            get { return _position; }
            set
            {
                if (!_reader.CanSeek) throw new InvalidOperationException("Cannot Seek!");
                ArgumentOutOfRangeException.ThrowIfLessThan(value, 0L);

                long samples = value / _reader.Channels;
                int sampleOffset = 0;

                // seek to the frame preceding the one we want (unless we're seeking to the first frame)
                if (samples >= _reader.FirstFrameSampleCount)
                {
                    sampleOffset = _reader.FirstFrameSampleCount;
                    samples -= sampleOffset;
                }

                lock (_seekLock)
                {
                    // seek the stream
                    long newPos = _reader.SeekTo(samples);
                    if (newPos == -1) throw new ArgumentOutOfRangeException(nameof(value));

                    _decoder.Reset();

                    // if we have a sample offset, decode the next frame
                    if (sampleOffset != 0)
                    {
                        _decoder.DecodeFrame(_reader.NextFrame(), _readBuf, 0); // throw away a frame (but allow the decoder to resync)
                        newPos += sampleOffset;
                    }

                    _position = newPos * _reader.Channels;
                    _eofFound = false;

                    // clear the decoder & buffer
                    _readBufOfs = _readBufLen = 0;
                }
            }
        }

        public TimeSpan Time
        {
            get { return TimeSpan.FromSeconds((double)_position / _reader.Channels / _reader.SampleRate); }
            set { Position = (long)(value.TotalSeconds * _reader.SampleRate * _reader.Channels); }
        }

        public void SetEQ(Span<float> eq) => _decoder.SetEQ(eq);

        public StereoMode StereoMode
        {
            get { return _decoder.StereoMode; }
            set { _decoder.StereoMode = value; }
        }

        private readonly float[] _readBuf = new float[1152 * 2];
        private int _readBufLen, _readBufOfs;

        public int ReadSamples(Span<float> buffer)
        {
            int cnt = 0;

            // lock around the entire read operation so seeking doesn't bork our buffers as we decode
            lock (_seekLock)
            {
                while (cnt < buffer.Length)
                {
                    if (_readBufLen > _readBufOfs)
                    {
                        int temp = Math.Min(_readBuf.Length - _readBufOfs, buffer.Length - cnt);
                        _readBuf.AsSpan(_readBufOfs, temp).CopyTo(buffer[cnt..(cnt + temp)]);

                        cnt += temp;

                        _position += temp;
                        _readBufOfs += temp;

                        // finally, mark the buffer as empty if we've read everything in it
                        if (_readBufOfs == _readBufLen)
                        {
                            _readBufLen = 0;
                        }
                    }

                    // if the buffer is empty, try to fill it
                    if (_readBufLen == 0)
                    {
                        if (_eofFound)
                        {
                            break;
                        }

                        // decode the next frame (update _readBufXXX)
                        Decoder.MpegFrame frame = _reader.NextFrame();
                        if (frame == null)
                        {
                            _eofFound = true;
                            break;
                        }

                        try
                        {
                            _readBufLen = _decoder.DecodeFrame(frame, _readBuf, 0);
                            _readBufOfs = 0;
                        }
                        catch (System.IO.InvalidDataException)
                        {
                            // bad frame...  try again...
                            _decoder.Reset();
                            _readBufOfs = _readBufLen = 0;
                            continue;
                        }
                        catch (System.IO.EndOfStreamException)
                        {
                            // no more frames
                            _eofFound = true;
                            break;
                        }
                        finally
                        {
                            frame.ClearBuffer();
                        }
                    }
                }
            }
            return cnt;
        }
    }
}
