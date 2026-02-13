using System.Runtime.InteropServices;

namespace FastCopy.Core;

[StructLayout(LayoutKind.Sequential)]
public readonly struct CopyJob
{
    public readonly string Source;
    public readonly string Destination;
    public readonly long FileSize;

    public CopyJob(string source, string destination, long fileSize)
    {
        Source = source;
        Destination = destination;
        FileSize = fileSize;
    }
}
