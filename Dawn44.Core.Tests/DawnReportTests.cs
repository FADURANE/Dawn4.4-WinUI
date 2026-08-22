using Dawn44.Core;
using Xunit;

namespace Dawn44.Core.Tests;

public class DawnReportTests
{
    [Fact]
    public void CreateReport_UsesTheEightByteWriteLayout()
    {
        var report = DawnProtocol.CreateReport(DawnProtocol.CommandVolume, 42);

        Assert.Equal(DawnProtocol.OutputReportLength, report.Length);
        Assert.Equal(new byte[] { 0x00, 0xC0, 0xA5, 0x04, 42, 0x00, 0x00, 0x00 }, report);
    }

    [Fact]
    public void CommandBytes_MatchTheDeviceFirmware()
    {
        Assert.Equal(0x01, DawnProtocol.CommandFilter);
        Assert.Equal(0x02, DawnProtocol.CommandGain);
        Assert.Equal(0x04, DawnProtocol.CommandVolume);
        Assert.Equal(0x06, DawnProtocol.CommandLed);
        Assert.Equal(0xA2, DawnProtocol.CommandReadVolume);
        Assert.Equal(0xA3, DawnProtocol.CommandReadState);
    }

    [Fact]
    public void DeviceIdentity_IsTheDawn44ControlInterface()
    {
        Assert.Equal(0x2FC6, DawnProtocol.VendorId);
        Assert.Equal(0xF067, DawnProtocol.ProductId);
        Assert.Equal(0x00, DawnProtocol.ReportId);
    }

    [Fact]
    public void FeatureRanges_MatchTheUiChoices()
    {
        Assert.Equal(4, DawnProtocol.MaxFilter);
        Assert.Equal(1, DawnProtocol.MaxGain);
        Assert.Equal(2, DawnProtocol.MaxLed);
        Assert.Equal(60, DawnProtocol.MaxVolume);
    }

    [Fact]
    public void IsResponseFor_AcceptsAReadReply()
    {
        byte[] data = [0x00, 0xA0, 0xA5, 0xA3, 2, 1, 0, 0];

        Assert.True(DawnProtocol.IsResponseFor(data, DawnProtocol.CommandReadState));
    }

    [Theory]
    [InlineData(new byte[] { 0x00, 0xC0, 0xA5, 0xA3 })] // write prefix, not a reply
    [InlineData(new byte[] { 0x00, 0xA0, 0xFF, 0xA3 })] // wrong second magic byte
    [InlineData(new byte[] { 0x00, 0xA0, 0xA5, 0xA2 })] // reply to the other read command
    [InlineData(new byte[] { 0x00, 0xA0, 0xA5 })]       // truncated
    [InlineData(new byte[] { })]                        // nothing came back
    public void IsResponseFor_RejectsAnythingElse(byte[] data)
    {
        Assert.False(DawnProtocol.IsResponseFor(data, DawnProtocol.CommandReadState));
    }
}
