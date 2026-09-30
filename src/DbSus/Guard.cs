namespace DbSus;

// Argument checks for both targets: the throw helpers exist on net10.0 but not on netstandard2.0.
internal static class Guard
{
    public static void NotNull(object? value, string paramName)
    {
#if NET
        ArgumentNullException.ThrowIfNull(value, paramName);
#else
        if (value is null)
        {
            throw new ArgumentNullException(paramName);
        }
#endif
    }

    public static void NotDisposed(bool disposed, object instance)
    {
#if NET
        ObjectDisposedException.ThrowIf(disposed, instance);
#else
        if (disposed)
        {
            throw new ObjectDisposedException(instance.GetType().FullName);
        }
#endif
    }

    public static void Positive(int value, string paramName)
    {
#if NET
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(value, paramName);
#else
        if (value <= 0)
        {
            throw new ArgumentOutOfRangeException(paramName, value, "Value must be positive.");
        }
#endif
    }
}
