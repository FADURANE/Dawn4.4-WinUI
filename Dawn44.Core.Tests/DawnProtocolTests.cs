using Dawn44.Core;
using Xunit;

namespace Dawn44.Core.Tests;

/// <summary>
/// Locks the wire format down. Every value here was verified against real hardware, so a failure
/// means the code changed, not that the expectation is stale.
/// </summary>
public class DawnProtocolTests
{
    [Fact]
    public void VolumeTable_HasOneEntryPerDisplayStep()
    {
        Assert.Equal(61, DawnProtocol.VolumeStepCount);
        Assert.Equal(DawnProtocol.MaxVolume + 1, DawnProtocol.VolumeStepCount);
    }

    [Fact]
    public void VolumeTable_RunsFromSilenceToFullOutput()
    {
        Assert.Equal(255, DawnProtocol.VolumeTable[0]);
        Assert.Equal(0, DawnProtocol.VolumeTable[DawnProtocol.MaxVolume]);
    }

    [Fact]
    public void VolumeTable_IsStrictlyDecreasing()
    {
        for (var index = 1; index < DawnProtocol.VolumeStepCount; index++)
        {
            Assert.True(
                DawnProtocol.VolumeTable[index] < DawnProtocol.VolumeTable[index - 1],
                $"entry {index} ({DawnProtocol.VolumeTable[index]}) is not below entry {index - 1}");
        }
    }

    [Fact]
    public void VolumeMapping_RoundTripsForEveryStep()
    {
        for (var display = 0; display <= DawnProtocol.MaxVolume; display++)
        {
            var raw = DawnProtocol.VolumeToRaw(display);
            Assert.InRange(raw, 0, 255);
            Assert.Equal(display, DawnProtocol.RawToVolume((byte)raw));
        }
    }

    [Theory]
    [InlineData(-1, 255)]
    [InlineData(61, 0)]
    [InlineData(1000, 0)]
    public void VolumeToRaw_ClampsOutOfRangeInput(int display, int expectedRaw)
    {
        Assert.Equal(expectedRaw, DawnProtocol.VolumeToRaw(display));
    }

    [Theory]
    [InlineData(1)]
    [InlineData(3)]
    [InlineData(199)]
    [InlineData(254)]
    public void RawToVolume_ReturnsNullForValuesOutsideTheTable(byte raw)
    {
        Assert.Null(DawnProtocol.RawToVolume(raw));
    }
}
