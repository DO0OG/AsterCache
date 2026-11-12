namespace AsterCache.Tray;

internal sealed class FileMetadataCache<T> where T : class
{
    private readonly object _gate = new();
    private string? _path;
    private FileStamp _stamp;
    private T? _value;
    private bool _hasValue;

    public T? Get(string path, Func<T?> read, bool cacheNull = true)
    {
        lock (_gate)
        {
            FileStamp before = Stamp(path);
            if (_hasValue && string.Equals(_path, path, StringComparison.OrdinalIgnoreCase) && _stamp == before)
            {
                return _value;
            }

            T? value = read();
            FileStamp after = Stamp(path);
            if (before == after && (value is not null || cacheNull))
            {
                _path = path;
                _stamp = after;
                _value = value;
                _hasValue = true;
            }
            else
            {
                _hasValue = false;
            }

            return value;
        }
    }

    public void Invalidate()
    {
        lock (_gate)
        {
            _hasValue = false;
        }
    }

    private static FileStamp Stamp(string path)
    {
        var file = new FileInfo(path);
        file.Refresh();
        return file.Exists ? new FileStamp(true, file.Length, file.LastWriteTimeUtc) : default;
    }

    private readonly record struct FileStamp(bool Exists, long Length, DateTime LastWriteTimeUtc);
}
