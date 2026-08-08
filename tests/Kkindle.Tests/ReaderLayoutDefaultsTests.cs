using Kkindle.Core;

namespace Kkindle.Tests;

public sealed class ReaderLayoutDefaultsTests
{
    [Fact]
    public void DefaultsAreHorizontalAndReadable()
    {
        var defaults = new ReaderLayoutSettings();
        Assert.Equal(1.0, defaults.FontScale);
        Assert.Equal(1.88, defaults.LineHeight);
        Assert.Equal(800, defaults.MaxWidth);
        Assert.Equal(68, defaults.BodyPadding);
        Assert.Equal(ReaderFontDefaults.DefaultFamily, defaults.FontFamily);
        Assert.Equal(0, defaults.FlowMode);
        Assert.False(defaults.VerticalWriting);
        Assert.False(defaults.TwoPageMode);
        Assert.False(defaults.VerticalWriting && defaults.FlowMode == 1);
    }

    [Fact]
    public void NormalizeClampsOutOfRangeValuesBackToSupportedRanges()
    {
        var corrupt = new ReaderLayoutSettings(
            FontScale: 9.0,
            LineHeight: 0.2,
            MaxWidth: 40,
            BodyPadding: 500,
            FlowMode: 7,
            VerticalWriting: true,
            TwoPageMode: true);

        var normalized = ReaderLayoutDefaults.Normalize(corrupt);

        Assert.Equal(ReaderLayoutDefaults.MaxFontScale, normalized.FontScale);
        Assert.Equal(ReaderLayoutDefaults.MinLineHeight, normalized.LineHeight);
        Assert.Equal(ReaderLayoutDefaults.MinMaxWidth, normalized.MaxWidth);
        Assert.Equal(ReaderLayoutDefaults.MaxBodyPadding, normalized.BodyPadding);
        Assert.Equal(0, normalized.FlowMode); // only 0 or 1 are valid flow modes; 7 falls back to scroll
        Assert.True(normalized.VerticalWriting); // user choice is preserved
        Assert.True(normalized.TwoPageMode); // page layout preference is preserved across normalization
    }

    [Fact]
    public void NormalizeTreatsNonFiniteValuesAsSafeDefaults()
    {
        var bad = new ReaderLayoutSettings(
            FontScale: double.NaN,
            LineHeight: double.PositiveInfinity,
            MaxWidth: double.NaN,
            BodyPadding: double.NaN,
            FlowMode: 0,
            VerticalWriting: false);

        var normalized = ReaderLayoutDefaults.Normalize(bad);

        Assert.Equal(ReaderLayoutDefaults.DefaultFontScale, normalized.FontScale);
        Assert.Equal(ReaderLayoutDefaults.DefaultLineHeight, normalized.LineHeight);
        Assert.Equal(ReaderLayoutDefaults.DefaultMaxWidth, normalized.MaxWidth);
        Assert.Equal(ReaderLayoutDefaults.DefaultBodyPadding, normalized.BodyPadding);
    }

    [Fact]
    public void NormalizeForcesInvalidFlowModeToScrollMode()
    {
        var invalid = new ReaderLayoutSettings(FlowMode: 3, VerticalWriting: true);
        var normalized = ReaderLayoutDefaults.Normalize(invalid);
        Assert.Equal(0, normalized.FlowMode);

        var paged = new ReaderLayoutSettings(FlowMode: 1, VerticalWriting: true);
        Assert.Equal(1, ReaderLayoutDefaults.Normalize(paged).FlowMode);
    }

    [Fact]
    public void NormalizeKeepsValidSettingsUntouched()
    {
        var valid = new ReaderLayoutSettings(
            FontScale: 1.2,
            LineHeight: 2.1,
            MaxWidth: 960,
            BodyPadding: 96,
            FontFamily: "SimSun",
            FlowMode: 1,
            VerticalWriting: false,
            TwoPageMode: true);

        var normalized = ReaderLayoutDefaults.Normalize(valid);
        Assert.Equal(valid, normalized);
    }

    [Fact]
    public void NormalizeMigratesLegacyEmptyFontToJinghuaLaosongti()
    {
        var normalized = ReaderLayoutDefaults.Normalize(new ReaderLayoutSettings(FontFamily: string.Empty));

        Assert.Equal(ReaderFontDefaults.DefaultFamily, normalized.FontFamily);
    }
}
