namespace FastCopy.Core;

public sealed class ActiveTransfer
{
    public string Source { get; set; } = string.Empty;
    public string Destination { get; set; } = string.Empty;
    public long TotalBytes { get; set; }
    public long BytesTransferred { get; set; }
    public double BytesPerSecond { get; set; }
    public string Status { get; set; } = "Pending";
}
