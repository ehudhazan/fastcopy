namespace FastCopy.UI;

public sealed class TransferItem
{
    public string FileName { get; set; } = string.Empty;
    public double Speed { get; set; }
    public double Progress { get; set; }
    public string Status { get; set; } = "Pending";
}
