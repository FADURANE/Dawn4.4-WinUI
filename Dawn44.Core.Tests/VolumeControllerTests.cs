using Dawn44.Core;
using Xunit;

namespace Dawn44.Core.Tests;

/// <summary>
/// Covers the two decisions that keep a bad reading from reaching the DAC. The rest of
/// <see cref="VolumeController"/> needs a device on the other end, so it is verified on hardware.
/// </summary>
public class VolumeControllerTests
{
    [Theory]
    [InlineData(30, 30)]   // unchanged
    [InlineData(30, 28)]   // moved by exactly the tolerance
    [InlineData(30, 32)]
    [InlineData(30, 31)]
    [InlineData(58, 60)]   // a real step near the top of the range is not a jump
    public void ReadingNeedsCorroboration_TrustsAReadingNearTheLastKnownValue(int lastKnown, int reading)
    {
        Assert.False(VolumeController.ReadingNeedsCorroboration(lastKnown, reading));
    }

    [Theory]
    // The one that mattered: raw 0 decodes to display 60, so a zeroed payload byte reads back as
    // maximum volume. From any normal listening level that is a jump, and must not be believed once.
    [InlineData(30, 60)]
    [InlineData(30, 0)]
    [InlineData(30, 27)]
    [InlineData(30, 33)]
    [InlineData(50, 60)]
    public void ReadingNeedsCorroboration_DistrustsAJump(int lastKnown, int reading)
    {
        Assert.True(VolumeController.ReadingNeedsCorroboration(lastKnown, reading));
    }

    [Fact]
    public void ReadingNeedsCorroboration_DistrustsEverythingWithNothingToCompareAgainst()
    {
        Assert.True(VolumeController.ReadingNeedsCorroboration(null, 30));
        Assert.True(VolumeController.ReadingNeedsCorroboration(null, 60));
        Assert.True(VolumeController.ReadingNeedsCorroboration(null, 0));
    }

    [Theory]
    [InlineData(0, 0)]
    [InlineData(1, 1)]
    [InlineData(-1, -1)]
    [InlineData(2, 2)]
    [InlineData(-2, -2)]
    // A backlog is ramped rather than applied in one write, so a wrong base is audible before it is
    // extreme.
    [InlineData(25, 2)]
    [InlineData(-25, -2)]
    public void StepToApply_CapsTheMagnitudeAndKeepsTheDirection(int delta, int expected)
    {
        Assert.Equal(expected, VolumeController.StepToApply(delta));
    }
}
