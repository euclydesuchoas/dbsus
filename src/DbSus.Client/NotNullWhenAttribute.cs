#if !NET
namespace System.Diagnostics.CodeAnalysis;

// The attribute exists on net10.0 but not on net472 or netstandard2.0; the compiler only needs its name.
[AttributeUsage(AttributeTargets.Parameter)]
internal sealed class NotNullWhenAttribute(bool returnValue) : Attribute
{
    public bool ReturnValue { get; } = returnValue;
}
#endif
