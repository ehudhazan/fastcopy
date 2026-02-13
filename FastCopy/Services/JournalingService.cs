using System.IO.Hashing;
using System.IO.MemoryMappedFiles;
using System.Runtime.InteropServices;
using System.Text;

namespace FastCopy.Services;

public sealed class JournalingService : IDisposable
{
    private const string JournalFileName = "fastcopy.journal";
    private const int TargetNameSize = 512;
    private const int EntrySize = sizeof(ulong) + sizeof(long) + TargetNameSize; // 8 + 8 + 512 = 528 bytes
    private const long ExpansionSize = 1024 * 1024; // 1MB

    private readonly string _journalPath;
    private readonly Lock _lock = new();
    
    private MemoryMappedFile? _mmf;
    private MemoryMappedViewAccessor? _accessor;
    private long _capacity;
    
    // In-memory index for fast lookups (SourceHash -> EntryIndex)
    private readonly Dictionary<ulong, int> _entryIndexMap = new();
    private readonly Queue<int> _freeIndices = new();

    public JournalingService()
    {
        _journalPath = Path.GetFullPath(JournalFileName);
        InitializeJournal();
    }

    private void InitializeJournal()
    {
        var fileInfo = new FileInfo(_journalPath);
        
        if (!fileInfo.Exists)
        {
            _capacity = ExpansionSize;
            // Create new file with initial capacity
            using var fs = new FileStream(_journalPath, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None);
            fs.SetLength(_capacity);
        }
        else
        {
            _capacity = fileInfo.Length;
            // Ensure capacity is multiple of ExpansionSize (sanity check)
            if (_capacity == 0) _capacity = ExpansionSize;
        }

        OpenMmf();
    }

    private void OpenMmf()
    {
        _mmf = MemoryMappedFile.CreateFromFile(
            _journalPath,
            FileMode.Open,
            null,
            _capacity,
            MemoryMappedFileAccess.ReadWrite);

        _accessor = _mmf.CreateViewAccessor();
    }

    public IEnumerable<(ulong SourceHash, string TargetName, long LastSuccessOffset)> Resume()
    {
        lock (_lock)
        {
            _entryIndexMap.Clear();
            _freeIndices.Clear();

            int maxEntries = (int)(_capacity / EntrySize);
            var results = new List<(ulong, string, long)>();

            for (int i = 0; i < maxEntries; i++)
            {
                long position = i * EntrySize;
                ulong hash = _accessor!.ReadUInt64(position);

                if (hash == 0)
                {
                    _freeIndices.Enqueue(i);
                    continue;
                }

                long offset = _accessor.ReadInt64(position + 8);
                
                // Read TargetName
                byte[] nameBuffer = new byte[TargetNameSize];
                _accessor.ReadArray(position + 16, nameBuffer, 0, TargetNameSize);
                
                // Find null terminator or end of buffer
                int nameLength = 0;
                while (nameLength < TargetNameSize && nameBuffer[nameLength] != 0)
                {
                    nameLength++;
                }

                string targetName = Encoding.UTF8.GetString(nameBuffer, 0, nameLength);

                _entryIndexMap[hash] = i;
                results.Add((hash, targetName, offset));
            }

            return results;
        }
    }

    public void Update(string sourcePath, string targetName, long offset)
    {
        // Use XXHash3 for source path
        byte[] sourceBytes = Encoding.UTF8.GetBytes(sourcePath);
        ulong hash = XxHash3.HashToUInt64(sourceBytes);

        Update(hash, targetName, offset);
    }

    public void Update(ulong sourceHash, string targetName, long offset)
    {
        lock (_lock)
        {
            if (_entryIndexMap.TryGetValue(sourceHash, out int index))
            {
                // Update existing entry (only offset)
                // We assume TargetName doesn't change for the same SourceHash in a session
                _accessor!.Write(index * EntrySize + 8, offset);
            }
            else
            {
                // New entry
                AddEntry(sourceHash, targetName, offset);
            }
        }
    }

    public void Complete(string sourcePath)
    {
        byte[] sourceBytes = Encoding.UTF8.GetBytes(sourcePath);
        ulong hash = XxHash3.HashToUInt64(sourceBytes);
        Complete(hash);
    }

    public void Complete(ulong sourceHash)
    {
        lock (_lock)
        {
            if (_entryIndexMap.TryGetValue(sourceHash, out int index))
            {
                // Clear the hash to mark as empty
                _accessor!.Write(index * EntrySize, 0UL);
                
                // Remove from map and add to free list
                _entryIndexMap.Remove(sourceHash);
                _freeIndices.Enqueue(index);
            }
        }
    }

    private void AddEntry(ulong hash, string targetName, long offset)
    {
        if (_freeIndices.Count == 0)
        {
            ExpandJournal();
        }

        if (_freeIndices.Count > 0)
        {
            int index = _freeIndices.Dequeue();
            long position = index * EntrySize;

            // Write Hash
            _accessor!.Write(position, hash);
            
            // Write Offset
            _accessor.Write(position + 8, offset);
            
            // Write TargetName (Zero out buffer first? Or just write and null terminate?)
            // To be safe, let's write the name and a null terminator if it's shorter than buffer
            byte[] nameBytes = Encoding.UTF8.GetBytes(targetName);
            byte[] buffer = new byte[TargetNameSize]; // Zero-initialized
            
            int copyLength = Math.Min(nameBytes.Length, TargetNameSize);
            Array.Copy(nameBytes, buffer, copyLength);
            
            _accessor.WriteArray(position + 16, buffer, 0, TargetNameSize);

            _entryIndexMap[hash] = index;
        }
    }

    private void ExpandJournal()
    {
        // Close existing maps
        _accessor?.Dispose();
        _mmf?.Dispose();

        // Expand file
        _capacity += ExpansionSize;
        
        using (var fs = new FileStream(_journalPath, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        {
            fs.SetLength(_capacity);
        }

        // Re-open
        OpenMmf();

        // Add new free indices
        int oldMaxEntries = (int)((_capacity - ExpansionSize) / EntrySize);
        int newMaxEntries = (int)(_capacity / EntrySize);
        
        for (int i = oldMaxEntries; i < newMaxEntries; i++)
        {
            _freeIndices.Enqueue(i);
        }
    }

    /// <summary>
    /// Flushes all pending writes to disk.
    /// Call this before application shutdown to ensure data integrity.
    /// </summary>
    public void Flush()
    {
        lock (_lock)
        {
            _accessor?.Flush();
        }
    }

    public void Dispose()
    {
        // Ensure all data is flushed before disposing
        Flush();
        
        _accessor?.Dispose();
        _mmf?.Dispose();
    }
}
