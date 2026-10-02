namespace DbSus.Dbc;

/// <summary>
/// One DBC part of a table read from streams: the stream and, optionally, a name for messages and
/// for <see cref="DbcReadOptions.ChecksumMismatch"/> (typically the file name).
/// </summary>
public sealed class DbcPart
{
    /// <summary>Creates a part without a name: messages call it <c>part N</c> (nothing for a table with a single part).</summary>
    /// <param name="stream">A readable stream positioned at the start of the DBC data; never seeked.</param>
    public DbcPart(Stream stream)
        : this(null, stream)
    {
    }

    /// <summary>Creates a part.</summary>
    /// <param name="name">
    /// The part's name, or <see langword="null"/>: messages then say <c>part N</c> (nothing for a
    /// table with a single part).
    /// </param>
    /// <param name="stream">A readable stream positioned at the start of the DBC data; never seeked.</param>
    /// <exception cref="ArgumentException"><paramref name="name"/> is empty.</exception>
    public DbcPart(string? name, Stream stream)
    {
        Guard.NotNull(stream, nameof(stream));
        if (name is { Length: 0 })
        {
            throw new ArgumentException("A part name cannot be empty; use null for no name.", nameof(name));
        }

        Name = name;
        Stream = stream;
    }

    /// <summary>Gets the part's name, or <see langword="null"/>.</summary>
    public string? Name { get; }

    /// <summary>Gets the part's DBC stream.</summary>
    public Stream Stream { get; }
}
