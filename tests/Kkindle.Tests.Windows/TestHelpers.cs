namespace Kkindle.Tests.Windows;

internal static class TestHelpers
{
    // Deliberately duplicated from Kkindle.Tests rather than shared: the two
    // test assemblies target different frameworks, and a shared project for
    // three lines of test double would couple them for no benefit. This is the
    // only helper the device tests borrow.
    internal sealed class InlineProgress<T>(Action<T> callback) : IProgress<T>
    {
        public void Report(T value) => callback(value);
    }
}
