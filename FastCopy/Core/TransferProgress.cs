namespace FastCopy.Core;

public readonly ref struct TransferProgress
{
    public readonly long BytesTransferred;
    public readonly long TotalBytes;
    public readonly double BytesPerSecond;

    public TransferProgress(long bytesTransferred, long totalBytes, double bytesPerSecond)
    {
        BytesTransferred = bytesTransferred;
        TotalBytes = totalBytes;
        BytesPerSecond = bytesPerSecond;
    }
}
