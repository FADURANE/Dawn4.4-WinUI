using Dawn44.Core;
using System;
using System.Threading;
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

    /// <summary>
    /// Nothing is bound here, so the loop polls two dead bindings and no callback can fire. What is
    /// under test is the thread lifecycle: a second <c>Start</c> must replace the first loop rather
    /// than leave two running, and <c>Stop</c> must be safe when nothing is running.
    /// </summary>
    [Fact]
    public void StartAndStop_ToleratesBeingCalledRepeatedly()
    {
        var watcher = NeverPressed();

        watcher.Stop();
        watcher.Start();
        watcher.Start();
        watcher.Stop();
        watcher.Stop();
    }

    /// <summary>
    /// The poll loop runs on a dedicated thread, so an exception escaping it would take the whole
    /// process down instead of quietly killing one task — and for the headless resident that means
    /// losing the only feature it has. It must report and keep polling, hence waiting for a second
    /// fault rather than just the first.
    /// </summary>
    [Fact]
    public void PollLoop_SurvivesABindingThatThrows()
    {
        var faults = 0;
        using var secondFault = new ManualResetEventSlim(false);
        var watcher = new HotkeyWatcher(
            () => throw new InvalidOperationException("binding is unreadable"),
            () => new HotkeySetting(0, 0),
            () => { },
            () => { })
        {
            CallbackFaulted = _ =>
            {
                if (Interlocked.Increment(ref faults) >= 2)
                {
                    secondFault.Set();
                }
            },
        };

        watcher.Start();
        try
        {
            // Two 15ms ticks is all this needs; the margin is for a loaded CI runner.
            Assert.True(secondFault.Wait(TimeSpan.FromSeconds(5)));
        }
        finally
        {
            watcher.Stop();
        }
    }

    private static HotkeyWatcher NeverPressed()
    {
        return new HotkeyWatcher(
            () => new HotkeySetting(0, 0),
            () => new HotkeySetting(0, 0),
            () => { },
            () => { });
    }
}
