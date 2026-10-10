namespace DbSus.Client;

/// <summary>
/// A table on the DATASUS server, found by <see cref="DatasusClient.ListTablesAsync"/> or
/// <see cref="DatasusClient.FindTableAsync"/>: one file, or the parts of a table split into several.
/// </summary>
public sealed class DatasusTableInfo
{
    internal DatasusTableInfo(DatasusDataset dataset, DatasusUf? uf, DatasusPeriod period, bool isPreliminary, string path, IReadOnlyList<DatasusFileInfo> parts)
    {
        Dataset = dataset;
        Uf = uf;
        Period = period;
        IsPreliminary = isPreliminary;
        Path = path;
        Parts = parts;
        long size = 0;
        foreach (var part in parts)
        {
            size += part.Size;
        }

        Size = size;
    }

    /// <summary>Gets the dataset the table belongs to.</summary>
    public DatasusDataset Dataset { get; }

    /// <summary>
    /// Gets the state of the table, or <see langword="null"/> for a table of the whole country
    /// (this version finds none).
    /// </summary>
    public DatasusUf? Uf { get; }

    /// <summary>Gets the month or the year of the table.</summary>
    public DatasusPeriod Period { get; }

    /// <summary>
    /// Gets a value indicating whether the table is in the folder where DATASUS publishes
    /// preliminary data, which it may still revise, rather than in the folder of final data.
    /// </summary>
    public bool IsPreliminary { get; }

    /// <summary>
    /// Gets the path of the table, relative to <see cref="DatasusClientOptions.RootPath"/> and with
    /// the name in the letter case the server lists it in. For a table split into parts it is the
    /// name without a part letter, which is not a file by itself; the files are in <see cref="Parts"/>.
    /// </summary>
    public string Path { get; }

    /// <summary>
    /// Gets the files of the table, in order: one for a table that is not split, otherwise its
    /// parts. From <see cref="DatasusClient.FindTableAsync"/> each has its exact size and time; from
    /// <see cref="DatasusClient.ListTablesAsync"/> they are as the listing shows them, with no time.
    /// </summary>
    public IReadOnlyList<DatasusFileInfo> Parts { get; }

    /// <summary>Gets the size of the table in bytes, compressed: the sum of its parts.</summary>
    public long Size { get; }

    /// <inheritdoc/>
    public override string ToString() => Path;
}
