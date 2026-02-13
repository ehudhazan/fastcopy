using System;
using System.IO;
using ZstdSharp;

namespace FastCopy.Services;

// Wraps a Stream, decompressing with ZstdSharp
public sealed class DecompressionStream : Stream
{
    private readonly Decompressor _decompressor;
    private readonly Stream _input;
    private readonly byte[] _inputBuffer;
    private readonly byte[] _outputBuffer;
    private int _outputOffset;
    private int _outputCount;
    private bool _disposed;
    private bool _eof;

    public DecompressionStream(Stream input, int inputBufferSize = 65536, int outputBufferSize = 65536)
    {
        _decompressor = new Decompressor();
        _input = input;
        _inputBuffer = new byte[inputBufferSize];
        _outputBuffer = new byte[outputBufferSize];
        _outputOffset = 0;
        _outputCount = 0;
    }

    public override bool CanRead => !_disposed;
    public override bool CanWrite => false;
    public override bool CanSeek => false;
    public override long Length => throw new NotSupportedException();
    public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
    public override void Flush() { }
    public override void SetLength(long value) => throw new NotSupportedException();
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

    public override int Read(byte[] buffer, int offset, int count)
    {
        if (_eof)
            return 0;
        int bytesReadTotal = 0;
        while (count > 0)
        {
            if (_outputOffset < _outputCount)
            {
                int copy = Math.Min(_outputCount - _outputOffset, count);
                Buffer.BlockCopy(_outputBuffer, _outputOffset, buffer, offset, copy);
                _outputOffset += copy;
                offset += copy;
                count -= copy;
                bytesReadTotal += copy;
                if (count <= 0) break;
            }
            else
            {
                int read = _input.Read(_inputBuffer, 0, _inputBuffer.Length);
                if (read == 0)
                {
                    _eof = true;
                    break;
                }
                _outputCount = _decompressor.Unwrap(_inputBuffer, 0, read, _outputBuffer, 0, _outputBuffer.Length);
                _outputOffset = 0;
                if (_outputCount == 0)
                {
                    // Got nothing, input is over
                    _eof = true;
                    break;
                }
            }
        }
        return bytesReadTotal;
    }

    protected override void Dispose(bool disposing)
    {
        if (!_disposed)
        {
            _disposed = true;
            _decompressor.Dispose();
            _input.Dispose();
        }
        base.Dispose(disposing);
    }

    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        var arr = new byte[buffer.Length];
        int read = await Task.Run(() => Read(arr, 0, arr.Length), cancellationToken);
        if (read > 0) arr.AsSpan(0, read).CopyTo(buffer.Span);
        return read;
    }
}