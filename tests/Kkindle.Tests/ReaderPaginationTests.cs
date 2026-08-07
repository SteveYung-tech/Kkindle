using Kkindle.Core;

namespace Kkindle.Tests;

public sealed class ReaderPaginationTests
{
    [Fact]
    public void SnapStartsAtTheFirstColumnPadding()
    {
        var snapped = ReaderPaginationPolicy.SnapScrollLeft(
            scrollLeft: 0,
            clientWidth: 1000,
            scrollWidth: 6000);

        Assert.Equal(ReaderPaginationDefaults.HorizontalPadding, snapped);
        Assert.False(ReaderPaginationPolicy.CanTurn(0, -1, 1000, 6000));
        Assert.True(ReaderPaginationPolicy.CanTurn(0, 1, 1000, 6000));
    }

    [Fact]
    public void SnapUsesTheScrollContainerWidth()
    {
        var snapped = ReaderPaginationPolicy.SnapScrollLeft(
            scrollLeft: 1130,
            clientWidth: 997.5,
            scrollWidth: 6000);

        Assert.Equal(1021.5, snapped, precision: 6);
    }

    [Theory]
    [InlineData(-1, 1024, 24)]
    [InlineData(1, 1024, 2024)]
    public void TurnTargetAdvancesByOneViewport(int direction, double expectedCurrent, double expectedTarget)
    {
        var target = ReaderPaginationPolicy.GetTurnTarget(
            scrollLeft: expectedCurrent,
            direction,
            clientWidth: 1000,
            scrollWidth: 6000);

        Assert.Equal(expectedTarget, target, precision: 6);
    }

    [Fact]
    public void TurnTargetClampsAtTheLastScrollablePosition()
    {
        var target = ReaderPaginationPolicy.GetTurnTarget(
            scrollLeft: 5000,
            direction: 1,
            clientWidth: 1000,
            scrollWidth: 5500);

        Assert.Equal(4500, target);
        Assert.False(ReaderPaginationPolicy.CanTurn(4500, 1, 1000, 5500));
        Assert.True(ReaderPaginationPolicy.CanTurn(4500, -1, 1000, 5500));
    }

    [Fact]
    public void InvalidViewportMetricsFailClosed()
    {
        Assert.Equal(0, ReaderPaginationPolicy.SnapScrollLeft(20, 0, 1000));
        Assert.False(ReaderPaginationPolicy.CanTurn(0, 1, double.NaN, 1000));
        Assert.Equal(0, ReaderPaginationPolicy.GetTurnTarget(0, 1, double.PositiveInfinity, 1000));
    }
}
