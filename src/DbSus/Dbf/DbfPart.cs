namespace DbSus.Dbf;

/// <summary>
/// One physical file (or stream) of a DBF table that may be split into parts, as DATASUS does
/// with SIA-PA (<c>PAMG2407a.dbc</c>, <c>PAMG2407b.dbc</c>, ...).
/// </summary>
internal sealed class DbfPart
{
    private readonly IDisposable[] _owned;
    private readonly IDisposable[] _created;
    private bool _released;

    /// <param name="name">Name used in messages (a file name or "part N"), or null for a lone stream.</param>
    /// <param name="stream">The DBF data, positioned at its start.</param>
    /// <param name="owned">Disposed when the part has been read or the reader closes.</param>
    /// <param name="created">
    /// Disposed if opening the reader fails. Only what DbSus itself created: never a caller's stream.
    /// </param>
    /// <param name="drainAtEnd">Read the stream to its end after the last record (DBC checksum).</param>
    public DbfPart(string? name, Stream stream, IDisposable[] owned, IDisposable[] created, bool drainAtEnd)
    {
        Name = name;
        Stream = stream;
        _owned = owned;
        _created = created;
        DrainAtEnd = drainAtEnd;
    }

    public string? Name { get; }

    public Stream Stream { get; }

    public bool DrainAtEnd { get; }

    public DbfHeader? Header { get; set; }

    public bool Finished { get; set; }

    public string Prefix => Name is null ? string.Empty : Name + ": ";

    public static void DisposeCreated(IEnumerable<DbfPart> parts)
    {
        foreach (var part in parts)
        {
            foreach (var resource in part._created)
            {
                resource.Dispose();
            }
        }
    }

    public void Release()
    {
        if (_released)
        {
            return;
        }

        _released = true;
        foreach (var resource in _owned)
        {
            resource.Dispose();
        }
    }
}
