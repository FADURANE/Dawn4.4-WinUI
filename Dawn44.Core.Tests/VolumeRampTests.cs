using Dawn44.Core;
using Xunit;

namespace Dawn44.Core.Tests;

public class VolumeRampTests
{
    [Theory]
    [InlineData(0, 0, 2, 0)]      // already there
    [InlineData(10, 12, 2, 12)]   // exactly one step away
    [InlineData(10, 11, 2, 11)]   // less than one step away
    [InlineData(10, 60, 2, 12)]   // long drag upward, capped
    [InlineData(60, 0, 2, 58)]    // long drag downward, capped
    [InlineData(10, 8, 2, 8)]
    public void MoveToward_AdvancesAtMostOneStep(int current, int target, int maximumStep, int expected)
    {
        Assert.Equal(expected, DawnProtocol.MoveToward(current, target, maximumStep));
    }

    [Fact]
    public void MoveToward_ReachesTheTargetInBoundedSteps()
    {
        var current = 0;
        var steps = 0;
        while (current != DawnProtocol.MaxVolume)
        {
            current = DawnProtocol.MoveToward(current, DawnProtocol.MaxVolume, 2);
            steps++;
            Assert.True(steps <= DawnProtocol.MaxVolume, "the ramp is not converging");
        }

        Assert.Equal(30, steps);
    }

    [Theory]
    [InlineData(-5, 0, 60, 0)]
    [InlineData(75, 0, 60, 60)]
    [InlineData(30, 0, 60, 30)]
    public void Clamp_KeepsValuesInRange(int value, int minimum, int maximum, int expected)
    {
        Assert.Equal(expected, DawnProtocol.Clamp(value, minimum, maximum));
    }

    /// <summary>
    /// These six codes mean the dongle was unplugged rather than that the request was malformed, so
    /// they must degrade to "not connected" instead of surfacing as an error.
    /// </summary>
    [Theory]
    [InlineData(2)]     // FILE_NOT_FOUND
    [InlineData(3)]     // PATH_NOT_FOUND
    [InlineData(6)]     // INVALID_HANDLE
    [InlineData(21)]    // NOT_READY
    [InlineData(31)]    // GEN_FAILURE
    [InlineData(1167)]  // DEVICE_NOT_CONNECTED
    public void IsDisconnectedWin32Error_RecognizesUnplugCodes(int errorCode)
    {
        Assert.True(DawnProtocol.IsDisconnectedWin32Error(errorCode));
    }

    [Theory]
    [InlineData(0)]     // SUCCESS
    [InlineData(5)]     // ACCESS_DENIED
    [InlineData(87)]    // INVALID_PARAMETER
    [InlineData(1223)]  // CANCELLED
    public void IsDisconnectedWin32Error_LeavesRealFailuresAlone(int errorCode)
    {
        Assert.False(DawnProtocol.IsDisconnectedWin32Error(errorCode));
    }
}
