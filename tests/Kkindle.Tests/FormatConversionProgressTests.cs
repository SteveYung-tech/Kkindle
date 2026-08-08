using Kkindle.Core;

namespace Kkindle.Tests;

public sealed class FormatConversionProgressTests
{
    [Theory]
    [InlineData(-10, 0)]
    [InlineData(12.4, 12)]
    [InlineData(12.6, 13)]
    [InlineData(100, 100)]
    [InlineData(140, 100)]
    public void RoundsAndClampsPercentage(double percentage, int expected)
    {
        var progress = new FormatConversionProgress(percentage, "转换中");

        Assert.Equal(expected, progress.RoundedPercentage);
    }
}
