using System.Buffers;
using System.Text;

namespace FastCopy.Transports;

/// <summary>
/// A stream wrapper that creates a tar archive on the fly from a source stream.
/// Implements the tar file format with proper headers and padding.
/// This is a pure in-memory streaming operation - no temporary files or disk buffering occurs.
/// All data flows through memory using Memory&lt;byte&gt; and Span&lt;byte&gt; for zero-copy performance.
/// </summary>
public sealed class TarStream : Stream
{
    private readonly Stream _source;
    private readonly string _fileName;
    private readonly long _fileSize;
    private readonly long _maxBytesPerSecond;
    
    private long _position;
    private long _sourcePosition;
    private bool _headerWritten;
    private bool _contentWritten;
    private bool _footerWritten;
    
    private readonly byte[] _header;
    private readonly int _paddingSize;
    
    // Token bucket for rate limiting
    private long _tokens;
    private long _lastTick;

    public TarStream(Stream source, string fileName, long fileSize, long maxBytesPerSecond = 0)
    {
        _source = source ?? throw new ArgumentNullException(nameof(source));
        _fileName = fileName ?? throw new ArgumentNullException(nameof(fileName));
        _fileSize = fileSize;
        _maxBytesPerSecond = maxBytesPerSecond;
        
        _header = CreateTarHeader(fileName, fileSize);
        _paddingSize = CalculatePadding(fileSize);
        
        _tokens = maxBytesPerSecond;
        _lastTick = Environment.TickCount64;
    }

    public override bool CanRead => true;
    public override bool CanSeek => false;
    public override bool CanWrite => false;
    public override long Length => _header.Length + _fileSize + _paddingSize + 1024; // +1024 for EOF
    public override long Position 
    { 
        get => _position;
        set => throw new NotSupportedException();
    }

    public override async Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken ct)
    {
        return await ReadAsync(buffer.AsMemory(offset, count), ct);
    }

    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default)
    {
        int totalRead = 0;

        // 1. Write tar header (512 bytes)
        if (!_headerWritten)
        {
            int headerBytesToCopy = Math.Min(buffer.Length, _header.Length - (int)_position);
            _header.AsSpan((int)_position, headerBytesToCopy).CopyTo(buffer.Span);
            _position += headerBytesToCopy;
            totalRead += headerBytesToCopy;

            if (_position >= _header.Length)
            {
                _headerWritten = true;
                _position = 0; // Reset for content phase
            }

            return totalRead;
        }

        // 2. Stream file content
        if (!_contentWritten && _sourcePosition < _fileSize)
        {
            int remainingInBuffer = buffer.Length - totalRead;
            long remainingInSource = _fileSize - _sourcePosition;
            int toRead = (int)Math.Min(remainingInBuffer, remainingInSource);

            if (toRead > 0)
            {
                // Apply rate limiting if configured
                if (_maxBytesPerSecond > 0)
                {
                    await ThrottleAsync(toRead, ct);
                }

                int bytesRead = await _source.ReadAsync(buffer.Slice(totalRead, toRead), ct);
                
                if (bytesRead > 0)
                {
                    _sourcePosition += bytesRead;
                    totalRead += bytesRead;
                }
                else if (_sourcePosition < _fileSize)
                {
                    // Source ended prematurely
                    throw new InvalidOperationException(
                        $"Source stream ended at {_sourcePosition} bytes, expected {_fileSize} bytes");
                }
            }

            if (_sourcePosition >= _fileSize)
            {
                _contentWritten = true;
                _position = 0; // Reset for padding phase
            }

            if (totalRead > 0)
            {
                return totalRead;
            }
        }

        // 3. Write padding to 512-byte boundary
        if (_contentWritten && !_footerWritten && _position < _paddingSize)
        {
            int paddingToWrite = Math.Min(buffer.Length - totalRead, _paddingSize - (int)_position);
            buffer.Slice(totalRead, paddingToWrite).Span.Clear(); // Write zeros
            _position += paddingToWrite;
            totalRead += paddingToWrite;

            if (_position >= _paddingSize)
            {
                _position = 0; // Reset for footer phase
            }

            if (totalRead > 0)
            {
                return totalRead;
            }
        }

        // 4. Write EOF markers (two 512-byte zero blocks)
        if (_contentWritten && _position >= _paddingSize && !_footerWritten)
        {
            const int eofSize = 1024; // Two 512-byte blocks
            int footerBytesToWrite = Math.Min(buffer.Length - totalRead, eofSize - (int)_position);
            buffer.Slice(totalRead, footerBytesToWrite).Span.Clear(); // Write zeros
            _position += footerBytesToWrite;
            totalRead += footerBytesToWrite;

            if (_position >= eofSize)
            {
                _footerWritten = true;
            }

            return totalRead;
        }

        // All done
        return 0;
    }

    public override int Read(byte[] buffer, int offset, int count)
    {
        return ReadAsync(buffer, offset, count, CancellationToken.None).GetAwaiter().GetResult();
    }

    private async ValueTask ThrottleAsync(int bytesToRead, CancellationToken ct)
    {
        while (bytesToRead > 0)
        {
            // Refill tokens based on elapsed time
            long currentTick = Environment.TickCount64;
            long elapsed = currentTick - _lastTick;

            if (elapsed > 0)
            {
                long newTokens = (elapsed * _maxBytesPerSecond) / 1000;
                _tokens = Math.Min(_maxBytesPerSecond, _tokens + newTokens);
                _lastTick = currentTick;
            }

            // Check if we have enough tokens
            if (_tokens >= bytesToRead)
            {
                _tokens -= bytesToRead;
                return;
            }

            // Wait for more tokens
            long tokensNeeded = bytesToRead - _tokens;
            long waitMilliseconds = (tokensNeeded * 1000) / _maxBytesPerSecond;
            
            if (waitMilliseconds > 0)
            {
                await Task.Delay((int)Math.Min(waitMilliseconds, 100), ct);
            }
        }
    }

    /// <summary>
    /// Creates a tar header (512 bytes) for the file.
    /// </summary>
    private static byte[] CreateTarHeader(string fileName, long fileSize)
    {
        byte[] header = new byte[512];
        
        // Normalize path (remove leading /)
        string normalizedName = fileName.TrimStart('/');
        
        // File name (0-99): max 100 bytes
        byte[] nameBytes = Encoding.ASCII.GetBytes(normalizedName);
        int nameToCopy = Math.Min(nameBytes.Length, 100);
        Array.Copy(nameBytes, 0, header, 0, nameToCopy);
        
        // File mode (100-107): "0000644\0" (octal)
        WriteOctalString(header, 100, 0000644, 7);
        
        // Owner ID (108-115): "0000000\0"
        WriteOctalString(header, 108, 0, 7);
        
        // Group ID (116-123): "0000000\0"
        WriteOctalString(header, 116, 0, 7);
        
        // File size (124-135): octal size
        WriteOctalString(header, 124, fileSize, 11);
        
        // Modification time (136-147): Unix timestamp in octal
        long unixTime = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        WriteOctalString(header, 136, unixTime, 11);
        
        // Checksum (148-155): filled with spaces initially
        Array.Fill<byte>(header, (byte)' ', 148, 8);
        
        // Type flag (156): '0' = regular file
        header[156] = (byte)'0';
        
        // Magic (257-262): "ustar\0"
        byte[] magic = Encoding.ASCII.GetBytes("ustar\0");
        Array.Copy(magic, 0, header, 257, magic.Length);
        
        // Version (263-264): "00"
        header[263] = (byte)'0';
        header[264] = (byte)'0';
        
        // Calculate and write checksum
        int checksum = 0;
        for (int i = 0; i < header.Length; i++)
        {
            checksum += header[i];
        }
        
        WriteOctalString(header, 148, checksum, 6);
        header[154] = 0; // Null terminator
        header[155] = (byte)' '; // Space
        
        return header;
    }

    /// <summary>
    /// Writes an octal number as ASCII string to the header buffer.
    /// </summary>
    private static void WriteOctalString(byte[] buffer, int offset, long value, int length)
    {
        string octal = Convert.ToString(value, 8).PadLeft(length, '0');
        byte[] octalBytes = Encoding.ASCII.GetBytes(octal);
        int toCopy = Math.Min(octalBytes.Length, length);
        Array.Copy(octalBytes, 0, buffer, offset, toCopy);
        
        // Add null terminator if space allows
        if (offset + length < buffer.Length)
        {
            buffer[offset + length] = 0;
        }
    }

    /// <summary>
    /// Calculates padding needed to align to 512-byte boundary.
    /// </summary>
    private static int CalculatePadding(long fileSize)
    {
        int remainder = (int)(fileSize % 512);
        return remainder == 0 ? 0 : 512 - remainder;
    }

    public override void Flush() { }
    public override Task FlushAsync(CancellationToken ct) => Task.CompletedTask;
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
}
