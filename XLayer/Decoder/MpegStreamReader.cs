﻿using System;
using System.IO;
using System.Threading;

namespace XLayer.Decoder;

internal class MpegStreamReader
{
    private ID3Frame _id3Frame, _id3v1Frame;
    private RiffHeaderFrame _riffHeaderFrame;
    private VBRInfo _vbrInfo;
    private MpegFrame _first, _current, _last, _lastFree;

    private long _readOffset, _eofOffset;
    private readonly Stream _source;
    private readonly bool _canSeek;
    private bool _endFound;
    private bool _mixedFrameSize;
    private readonly Lock _readLock = new();
    private readonly Lock _frameLock = new();

    internal MpegStreamReader(Stream source)
    {
        ArgumentNullException.ThrowIfNull(source, nameof(source));
        if (!source.CanRead) throw new ArgumentException("Stream must be readable!", nameof(source));

        _source = source;
        _canSeek = source.CanSeek;
        _readOffset = 0L;
        _eofOffset = long.MaxValue;

        // Attempt to find the first MPEG frame
        while (true)
        {
            var frame = FindNextFrame();
            if (frame is MpegFrame)
            {
                _current = _first;
                break;
            }
            if (frame is null)
            {
                throw new InvalidDataException("Not a valid MPEG file!");
            }
        }
    }

    private FrameBase FindNextFrame()
    {
        // if we've found the end, don't bother looking for anything else
        if (_endFound) return null;

        MpegFrame freeFrame = _lastFree;
        long lastFrameStart = _readOffset;

        lock (_frameLock)
        {
            // read 3 bytes
            Span<byte> syncBuf = stackalloc byte[4];
            try
            {
                if (Read(_readOffset, syncBuf, 0, 4) == 4)
                {
                    // now loop until a frame is found
                    do
                    {
                        uint sync = (uint)(syncBuf[0] << 24 | syncBuf[1] << 16 | syncBuf[2] << 8 | syncBuf[3]);

                        lastFrameStart = _readOffset;

                        // try ID3 first (for v2 frames)
                        if (_id3Frame == null)
                        {
                            ID3Frame f = ID3Frame.TrySync(sync);
                            if (f != null)
                            {
                                if (f.Validate(_readOffset, this))
                                {
                                    if (!_canSeek) f.SaveBuffer();

                                    _readOffset += f.Length;
                                    DiscardThrough(_readOffset, true);

                                    return _id3Frame = f;
                                }
                            }
                        }

                        // now look for a RIFF header
                        if (_first == null && _riffHeaderFrame == null)
                        {
                            RiffHeaderFrame f = RiffHeaderFrame.TrySync(sync);
                            if (f != null)
                            {
                                if (f.Validate(_readOffset, this))
                                {
                                    _readOffset += f.Length;
                                    DiscardThrough(_readOffset, true);

                                    return _riffHeaderFrame = f;
                                }
                            }
                        }

                        // finally, just try for an MPEG frame
                        MpegFrame frame = MpegFrame.TrySync(sync);
                        if (frame != null)
                        {
                            if (frame.Validate(_readOffset, this)
                               && !(freeFrame != null
                                   && (frame.Layer != freeFrame.Layer
                                      || frame.Version != freeFrame.Version
                                      || frame.SampleRate != freeFrame.SampleRate
                                      || frame.BitRateIndex > 0
                                      )
                                   )
                               )
                            {
                                if (!_canSeek)
                                {
                                    frame.SaveBuffer();
                                    DiscardThrough(_readOffset + frame.FrameLength, true);
                                }

                                _readOffset += frame.FrameLength;

                                if (_first == null)
                                {
                                    if (_vbrInfo == null && (_vbrInfo = frame.ParseVBR()) != null)
                                    {
                                        return FindNextFrame();
                                    }
                                    else
                                    {
                                        frame.Number = 0;
                                        _first = _last = frame;
                                    }
                                }
                                else
                                {
                                    if (frame.SampleCount != _first.SampleCount)
                                    {
                                        _mixedFrameSize = true;
                                    }

                                    frame.SampleOffset = _last.SampleCount + _last.SampleOffset;
                                    frame.Number = _last.Number + 1;
                                    _last = _last.Next = frame;
                                }

                                if (frame.BitRateIndex == 0)
                                {
                                    _lastFree = frame;
                                }

                                return frame;
                            }
                        }

                        // if we've read MPEG frames and can't figure out what frame type we have, try looking for a new ID3 tag
                        if (_last != null)
                        {
                            ID3Frame f = ID3Frame.TrySync(sync);
                            if (f != null)
                            {
                                if (f.Validate(_readOffset, this))
                                {
                                    if (!_canSeek) f.SaveBuffer();

                                    // if it's a v1 tag, go ahead and parse it
                                    if (f.Version == 1)
                                    {
                                        _id3v1Frame = f;
                                    }
                                    else
                                    {
                                        // grrr...  the ID3 2.4 spec says tags can be anywhere in the file and that later tags can override earlier ones...  boo
                                        ID3Frame.Merge(f);
                                    }

                                    _readOffset += f.Length;
                                    DiscardThrough(_readOffset, true);

                                    return f;
                                }
                            }
                        }

                        // well, we didn't find anything, so rinse and repeat with the next byte
                        ++_readOffset;
                        if (_first == null || !_canSeek) DiscardThrough(_readOffset, true);
                        syncBuf[1..].CopyTo(syncBuf[0..3]);
                        // Buffer.BlockCopy(syncBuf, 1, syncBuf, 0, 3);
                    } while (Read(_readOffset + 3, syncBuf, 3, 1) == 1);
                }

                // move the "end of frame" marker for the last free format frame (in case we have one)
                // this is because we don't include the last four bytes otherwise
                lastFrameStart += 4;

                _endFound = true;
                return null;
            }
            finally
            {
                if (freeFrame != null)
                {
                    freeFrame.Length = (int)(lastFrameStart - freeFrame.Offset);

                    if (!_canSeek)
                    {
                        // gotta finish filling the buffer!!
                        throw new InvalidOperationException("Free frames cannot be read properly from forward-only streams!");
                    }

                    // if _lastFree hasn't changed (we got a non-MPEG frame), clear it out
                    if (_lastFree == freeFrame)
                    {
                        _lastFree = null;
                    }
                }
            }
        }
    }

    private class ReadBuffer
    {
        public byte[] Data;
        public long BaseOffset;
        public int End;
        public int DiscardCount;

        private readonly Lock _localLock = new();

        public ReadBuffer(int initialSize)
        {
            initialSize = 2 << (int)Math.Log(initialSize, 2);

            Data = new byte[initialSize];
        }

        public int Read(MpegStreamReader reader, long offset, Span<byte> buffer, int index, int count)
        {
            lock (_localLock)
            {
                int startIdx = EnsureFilled(reader, offset, ref count);

                Data.AsSpan(startIdx, count).CopyTo(buffer.Slice(index, count));
            }
            return count;
        }

        public int ReadByte(MpegStreamReader reader, long offset)
        {
            lock (_localLock)
            {
                int count = 1;
                int startIdx = EnsureFilled(reader, offset, ref count);
                if (count == 1)
                {
                    return Data[startIdx];
                }
            }
            return -1;
        }

        private int EnsureFilled(MpegStreamReader reader, long offset, ref int count)
        {
            // if the offset & count are inside our buffer's range, just return the appropriate index
            int startIdx = (int)(offset - BaseOffset);
            int endIdx = startIdx + count;
            if (startIdx < 0 || endIdx > End)
            {
                int readStart = 0, readCount = 0, moveCount = 0;
                long readOffset = 0;

                if (startIdx < 0)
                {
                    // if we can't seek, there's nothing we can do
                    if (!reader._source.CanSeek) throw new InvalidOperationException("Cannot seek backwards on a forward-only stream!");

                    // if there's data in the buffer, try to keep it (up to doubling the buffer size)
                    if (End > 0)
                    {
                        // if doubling the buffer would push it past the max size, don't check it
                        if ((startIdx + Data.Length > 0) || (Data.Length * 2 <= 16384 && startIdx + Data.Length * 2 > 0))
                        {
                            endIdx = End;
                        }
                    }

                    // we know we'll have to start reading here
                    readOffset = offset;

                    // if the end of the request is before the start of our buffer...
                    if (endIdx < 0)
                    {
                        // ... just truncate and move on
                        Truncate();

                        // set up our read parameters
                        BaseOffset = offset;
                        startIdx = 0;
                        endIdx = count;

                        // how much do we need to read?
                        readCount = count;
                    }
                    else // i.e., endIdx >= 0
                    {
                        // we have overlap with existing data...  save as much as possible
                        moveCount = -endIdx;
                        readCount = -startIdx;
                    }
                }
                else // i.e., startIdx >= 0
                {
                    // we only get to here if at least one byte of the request is past the end of the read data
                    // start with the simplest scenario and work our way up

                    // 1) We just need to fill the buffer a bit more
                    if (endIdx < Data.Length)
                    {
                        readCount = endIdx - End;
                        readStart = End;
                        readOffset = BaseOffset + readStart;
                    }
                    // 2) We need to discard some bytes, then fill the buffer
                    else if (endIdx - DiscardCount < Data.Length)
                    {
                        moveCount = DiscardCount;
                        readStart = End;
                        readCount = endIdx - readStart;
                        readOffset = BaseOffset + readStart;
                    }
                    // 3) We need to expand the buffer to hold all the existing & requested data
                    else if (Data.Length * 2 <= 16384)
                    {
                        // by definition, we discard
                        moveCount = DiscardCount;
                        readStart = End;
                        readCount = endIdx - End;
                        readOffset = BaseOffset + readStart;
                    }
                    // 4) We have to throw away some data that hasn't been discarded
                    else
                    {
                        // just truncate
                        Truncate();

                        // set up our read parameters
                        BaseOffset = offset;
                        readOffset = offset;
                        startIdx = 0;
                        endIdx = count;

                        // how much do we have to read?
                        readCount = count;
                    }
                }

                if (endIdx - moveCount > Data.Length || readStart + readCount - moveCount > Data.Length)
                {
                    int newSize = Data.Length * 2;
                    while (newSize < endIdx - moveCount)
                    {
                        newSize *= 2;
                    }

                    byte[] newBuf = new byte[newSize];
                    if (moveCount < 0)
                    {
                        // reverse copy
                        Buffer.BlockCopy(Data, 0, newBuf, -moveCount, End + moveCount);

                        DiscardCount = 0;
                    }
                    else
                    {
                        // forward or neutral copy
                        Buffer.BlockCopy(Data, moveCount, newBuf, 0, End - moveCount);

                        DiscardCount -= moveCount;
                    }
                    Data = newBuf;
                }
                else if (moveCount != 0)
                {
                    if (moveCount > 0)
                    {
                        // forward move
                        Buffer.BlockCopy(Data, moveCount, Data, 0, End - moveCount);

                        DiscardCount -= moveCount;
                    }
                    else
                    {
                        // backward move
                        for (int i = 0, srcIdx = Data.Length - 1, destIdx = Data.Length - 1 - moveCount; i < moveCount; i++, srcIdx--, destIdx--)
                        {
                            Data[destIdx] = Data[srcIdx];
                        }

                        DiscardCount = 0;
                    }
                }

                BaseOffset += moveCount;
                readStart -= moveCount;
                startIdx -= moveCount;
                endIdx -= moveCount;
                End -= moveCount;

                lock (reader._readLock)
                {
                    if (readCount > 0 && reader._source.Position != readOffset && readOffset < reader._eofOffset)
                    {
                        if (reader._canSeek)
                        {
                            try
                            {
                                reader._source.Position = readOffset;
                            }
                            catch (EndOfStreamException)
                            {
                                reader._eofOffset = reader._source.Length;
                                readCount = 0;
                            }
                        }
                        else
                        {
                            // ugh, gotta read bytes until we've reached the desired offset
                            long seekCount = readOffset - reader._source.Position;
                            while (--seekCount >= 0)
                            {
                                if (reader._source.ReadByte() == -1)
                                {
                                    reader._eofOffset = reader._source.Position;
                                    readCount = 0;
                                    break;
                                }
                            }
                        }
                    }

                    while (readCount > 0 && readOffset < reader._eofOffset)
                    {
                        int temp = reader._source.Read(Data, readStart, readCount);
                        if (temp == 0)
                        {
                            break;
                        }
                        readStart += temp;
                        readOffset += temp;
                        readCount -= temp;
                    }

                    if (readStart > End)
                    {
                        End = readStart;
                    }

                    if (End < endIdx)
                    {
                        // we didn't get a full read...
                        count = Math.Max(0, End - startIdx);
                    }
                    // NB: if desired, switch to "minimal reads" by commenting-out this clause
                    else if (End < Data.Length)
                    {
                        // try to finish filling the buffer
                        int temp = reader._source.Read(Data, End, Data.Length - End);
                        End += temp;
                    }
                }
            }

            return startIdx;
        }

        public void DiscardThrough(long offset)
        {
            lock (_localLock)
            {
                int count = (int)(offset - BaseOffset);
                DiscardCount = Math.Max(count, DiscardCount);

                if (DiscardCount >= Data.Length) CommitDiscard();
            }
        }

        private void Truncate()
        {
            End = 0;
            DiscardCount = 0;
        }

        private void CommitDiscard()
        {
            if (DiscardCount >= Data.Length || DiscardCount >= End)
            {
                // we have been told to discard the entire buffer
                BaseOffset += DiscardCount;
                End = 0;
            }
            else
            {
                // just discard the first part...
                //Array.Copy(_readBuf, _readBufDiscardCount, _readBuf, 0, _readBufEnd - _readBufDiscardCount);
                Buffer.BlockCopy(Data, DiscardCount, Data, 0, End - DiscardCount);
                BaseOffset += DiscardCount;
                End -= DiscardCount;
            }
            DiscardCount = 0;
        }
    }

    private readonly ReadBuffer _readBuf = new(2048);

    internal int Read(long offset, Span<byte> buffer, int index, int count)
    {
        // make sure the offset is at least positive
        ArgumentOutOfRangeException.ThrowIfLessThan(offset, 0L);

        // make sure the buffer is valid
        if (index < 0 || index + count > buffer.Length) throw new ArgumentOutOfRangeException(nameof(index));

        return _readBuf.Read(this, offset, buffer, index, count);
    }

    internal int ReadByte(long offset)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(offset, 0L);

        return _readBuf.ReadByte(this, offset);
    }

    internal void DiscardThrough(long offset, bool minimalRead)
    {
        _readBuf.DiscardThrough(offset);
    }


    internal void ReadToEnd()
    {
        try
        {
            int maxAllocation = 40000;
            if (_id3Frame != null)
            {
                maxAllocation += _id3Frame.Length;
            }

            while (!_endFound)
            {
                FindNextFrame();

                while (!_canSeek && FrameBase.TotalAllocation >= maxAllocation)
                {
                    System.Threading.Tasks.Task.Delay(500).Wait();
                }
            }
        }
        catch (ObjectDisposedException)
        {
        }
    }


    internal bool CanSeek => _canSeek;

    internal long SampleCount
    {
        get
        {
            if (_vbrInfo != null) return _vbrInfo.VBRStreamSampleCount;

            if (!_canSeek) return -1;

            ReadToEnd();
            return _last.SampleCount + _last.SampleOffset;
        }
    }

    internal int SampleRate
    {
        get
        {
            if (_vbrInfo != null) return _vbrInfo.SampleRate;
            return _first.SampleRate;
        }
    }

    internal int Channels
    {
        get
        {
            if (_vbrInfo != null) return _vbrInfo.Channels;
            return _first.Channels;
        }
    }

    internal int FirstFrameSampleCount => _first != null ? _first.SampleCount : 0;

    internal long SeekTo(long sampleNumber)
    {
        if (!_canSeek) throw new InvalidOperationException("Cannot seek!");

        // first try to "seek" by calculating the frame number
        int cnt = (int)(sampleNumber / _first.SampleCount);
        MpegFrame frame = _first;
        if (_current != null && _current.Number <= cnt && _current.SampleOffset <= sampleNumber)
        {
            // if this fires, we can short-circuit things a bit...
            frame = _current;
            cnt -= frame.Number;
        }
        while (!_mixedFrameSize && --cnt >= 0 && frame != null)
        {
            // make sure we have more frames to look at
            if (frame == _last && !_endFound)
            {
                do
                {
                    FindNextFrame();
                } while (frame == _last && !_endFound);
            }

            // if we've found a different frame size, fall through...
            if (_mixedFrameSize)
            {
                break;
            }

            frame = frame.Next;
        }

        // this should not run unless we found mixed frames...
        while (frame != null && frame.SampleOffset + frame.SampleCount < sampleNumber)
        {
            if (frame == _last && !_endFound)
            {
                do
                {
                    FindNextFrame();
                } while (frame == _last && !_endFound);
            }

            frame = frame.Next;
        }
        if (frame == null) return -1;
        return (_current = frame).SampleOffset;
    }

    internal MpegFrame NextFrame()
    {
        if (_current == null) return null;

        if (_canSeek)
        {
            _current.SaveBuffer();
            DiscardThrough(_current.Offset + _current.FrameLength, false);
        }

        if (_current == _last && !_endFound)
        {
            while (_current == _last && !_endFound)
            {
                FindNextFrame();
            }
        }

        var frame = _current;
        _current = _current.Next;

        if (!_canSeek)
        {
            lock (_frameLock)
            {
                _first = _first?.Next;
            }
        }

        return frame;
    }
}
