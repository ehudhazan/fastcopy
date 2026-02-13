using Terminal.Gui;

namespace FastCopy.UI;

// Implement ITableSource for high performance virtualization
public sealed class TransferItemTableSource : ITableSource
{
    private readonly List<TransferItem> _data;
    private readonly string[] _columnNames = { "File Name", "Speed (MB/s)", "Progress", "Status" };

    public TransferItemTableSource(List<TransferItem> data)
    {
        _data = data;
    }

    public object this[int row, int col]
    {
        get
        {
            var item = _data[row];
            return col switch
            {
                0 => item.FileName,
                1 => $"{item.Speed:F1} MB/s",
                2 => GenerateProgressBar(item.Progress),
                3 => item.Status,
                _ => string.Empty
            };
        }
    }

    public int Rows => _data.Count;

    public int Columns => _columnNames.Length;

    public string[] ColumnNames => _columnNames;

    // Helper to make a text-based progress bar for the cell
    private string GenerateProgressBar(double progress)
    {
        const int width = 10;
        int filled = (int)(progress * width);
        return "[" + new string('#', filled).PadRight(width, '-') + "]";
    }
}
