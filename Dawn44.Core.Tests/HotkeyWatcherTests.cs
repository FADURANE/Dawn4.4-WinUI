using Dawn44.Core;
using Xunit;

namespace Dawn44.Core.Tests;

public class HotkeyWatcherTests
{
    [Fact]
    public void IsComboActive_IsFalseWhenNoKeyIsBound()
    {
        Assert.False(HotkeyWatcher.IsComboActive(new HotkeySetting(HotkeyModifiers.AltControl, 0)));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(195)]
    [InlineData(390)]
    public void ShouldRepeat_StaysSilentForTheFirst400ms(int heldMs)
    {
        Assert.False(HotkeyWatcher.ShouldRepeat(heldMs));
    }

    [Fact]
    public void ShouldRepeat_FiresOnceTheHoldDelayHasPassed()
    {
        Assert.True(HotkeyWatcher.ShouldRepeat(400));
        Assert.True(HotkeyWatcher.ShouldRepeat(405));
        Assert.False(HotkeyWatcher.ShouldRepeat(420));
    }

    /// <summary>
    /// The accumulator only ever takes 15ms steps, so what matters is that exactly one step lands in
    /// each 80ms repeat window — no dropped repeats and no double-fires.
    /// </summary>
    [Fact]
    public void ShouldRepeat_FiresExactlyOncePerRepeatWindow()
    {
        var repeats = 0;
        for (var heldMs = 0; heldMs <= 2000; heldMs += 15)
        {
            if (HotkeyWatcher.ShouldRepeat(heldMs))
            {
                repeats++;
            }
        }

        // Windows start at 400ms and recur every 80ms: 400, 480, ... 1920 inclusive.
        Assert.Equal(20, repeats);
    }
}
