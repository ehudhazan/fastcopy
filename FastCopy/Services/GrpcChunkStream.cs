using System;
using System.Buffers;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Google.Protobuf;
using Grpc.Core;

namespace FastCopy.Services;

public class GrpcChunkStream : Stream
{
    private readonly IAsyncStreamReader<FileChunk> _reader;
    private ReadOnlyMemory<byte> _currentBuffer;
    private bool _finished;

    public GrpcChunkStream(IAsyncStreamReader<FileChunk> reader, FileChunk? firstChunk = null)
    {
        _reader = reader;
        if (firstChunk != null)
        {
            _currentBuffer = firstChunk.Content.Memory;
        }
    }

    public override bool CanRead => true;
    public override bool CanSeek => false;
    public override bool CanWrite => false;
    public override long Length => throw new NotSupportedException();
    public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }

    public override void Flush() { }

    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        if (_currentBuffer.IsEmpty)
        {
            if (_finished) return 0;

            if (!await _reader.MoveNext(cancellationToken))
            {
                _finished = true;
                return 0;
            }
            
            var chunk = _reader.Current;
            _currentBuffer = chunk.Content.Memory;
        }

        int bytesToCopy = Math.Min(buffer.Length, _currentBuffer.Length);
        _currentBuffer.Slice(0, bytesToCopy).CopyTo(buffer);
        _currentBuffer = _currentBuffer.Slice(bytesToCopy);

        return bytesToCopy;
    }

    public override int Read(byte[] buffer, int offset, int count) 
        => ReadAsync(buffer.AsMemory(offset, count)).AsTask().GetAwaiter().GetResult();
        
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
}
