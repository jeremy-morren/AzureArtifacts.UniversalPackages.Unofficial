// ReSharper disable UnusedType.Global

#if !NET5_0_OR_GREATER
namespace System.Runtime.CompilerServices
{
    internal static class IsExternalInit;
}

#endif

#if !NETSTANDARD2_1_OR_GREATER
internal static class StringExtensions
{
    public static bool EndsWith(this string str, char value) => str.EndsWith(value.ToString());
}
#endif