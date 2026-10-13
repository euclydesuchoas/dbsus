namespace DbSus.Dbf;

/// <summary>
/// What <see cref="DbfReader"/> does with the content of a numeric (<c>N</c> or <c>F</c>) or date
/// (<c>D</c>) field that is not a number or a date. Set it in
/// <see cref="DbfReadOptions.UnconvertibleValues"/>.
/// </summary>
/// <remarks>
/// <para>
/// A blank field is always null, and so is a date made of zeros: they are not content that does
/// not convert. This is about a field that holds something else, such as a field filled with
/// asterisks, a date with slashes or a letter in a number.
/// </para>
/// <para>
/// It applies to <c>N</c>, <c>F</c> and <c>D</c> fields. It does not apply to logical (<c>L</c>)
/// fields, nor to character (<c>C</c>) fields converted with <see cref="DbfFieldConversion"/>:
/// content of those that does not convert throws <see cref="FormatException"/> in every mode.
/// </para>
/// <para>
/// A value read as null here cannot be told from a blank field through the typed members:
/// <see cref="DbfReader.GetRawBytes(int)"/> returns the bytes of the field in every mode.
/// </para>
/// </remarks>
public enum DbfUnconvertibleValueHandling
{
    /// <summary>
    /// The content throws <see cref="FormatException"/>, which names the field and the record and
    /// never has the content. This is the default.
    /// </summary>
    Throw = 0,

    /// <summary>
    /// A field that, without the spaces at its ends, holds asterisks (<c>*</c>) and nothing else is
    /// read as null. Any other content that does not convert throws <see cref="FormatException"/>.
    /// </summary>
    AsterisksAsNull = 1,

    /// <summary>Any content that does not convert is read as null.</summary>
    Null = 2,
}
