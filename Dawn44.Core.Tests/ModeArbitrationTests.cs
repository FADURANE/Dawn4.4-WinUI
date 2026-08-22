using Dawn44.Core;
using System;
using Xunit;

namespace Dawn44.Core.Tests;

/// <summary>
/// Covers the arbitration decision table and the <c>running.json</c> round trip. Nothing here goes
/// near <c>%LOCALAPPDATA%</c>: the four cases are decided by a pure function precisely so they can be
/// tested without spawning a second process or touching the user's live state.
/// </summary>
public class ModeArbitrationTests
{
    private static readonly Func<ModeArbitration.RunningState, bool> Alive = _ => true;
    private static readonly Func<ModeArbitration.RunningState, bool> Dead = _ => false;

    [Fact]
    public void Decide_RunsWhenNobodyOwnsTheDevice()
    {
        Assert.Equal(TakeOverDecision.Run, ModeArbitration.Decide(null, AppMode.Background, Alive));
    }

    [Fact]
    public void Decide_RunsWhenTheRecordedOwnerIsGone()
    {
        var stale = new ModeArbitration.RunningState(4242, AppMode.Gui, DateTimeOffset.UnixEpoch);

        Assert.Equal(TakeOverDecision.Run, ModeArbitration.Decide(stale, AppMode.Background, Dead));
    }

    [Fact]
    public void Decide_YieldsToALiveOwnerInTheSameMode()
    {
        var owner = new ModeArbitration.RunningState(4242, AppMode.Background, null);

        Assert.Equal(
            TakeOverDecision.YieldToSameMode,
            ModeArbitration.Decide(owner, AppMode.Background, Alive));
    }

    [Fact]
    public void Decide_AsksTheOtherModeToStepAside()
    {
        var owner = new ModeArbitration.RunningState(4242, AppMode.Gui, null);

        Assert.Equal(
            TakeOverDecision.WaitForOtherMode,
            ModeArbitration.Decide(owner, AppMode.Background, Alive));
    }

    [Fact]
    public void Decide_TreatsOurOwnRecordAsFreeToOverwrite()
    {
        var self = new ModeArbitration.RunningState(Environment.ProcessId, AppMode.Gui, null);

        Assert.Equal(TakeOverDecision.Run, ModeArbitration.Decide(self, AppMode.Background, Alive));
    }

    [Fact]
    public void RunningState_SurvivesTheRoundTrip()
    {
        var startedAt = new DateTimeOffset(2026, 8, 22, 13, 45, 6, TimeSpan.FromHours(8));
        var json = ModeArbitration.SerializeRunningState(
            new ModeArbitration.RunningState(1234, AppMode.Background, startedAt));

        Assert.True(ModeArbitration.TryParseRunningState(json, out var parsed));
        Assert.Equal(1234, parsed.Pid);
        Assert.Equal(AppMode.Background, parsed.Mode);
        Assert.Equal(startedAt, parsed.StartedAt);
    }

    [Fact]
    public void TryParseRunningState_KeepsThePidWhenTheRestIsMissing()
    {
        Assert.True(ModeArbitration.TryParseRunningState("{\"Pid\":7}", out var parsed));
        Assert.Equal(7, parsed.Pid);
        Assert.Equal(AppMode.Gui, parsed.Mode);
        Assert.Null(parsed.StartedAt);
    }

    [Theory]
    [InlineData("")]                          // empty file, a half-finished write
    [InlineData("not json at all")]
    [InlineData("[1,2,3]")]                   // right syntax, wrong shape
    [InlineData("{\"Mode\":\"Gui\"}")]        // no pid means nothing to check liveness against
    [InlineData("{\"Pid\":\"1234\"}")]        // pid must be a number
    public void TryParseRunningState_RejectsAnythingUnusable(string json)
    {
        Assert.False(ModeArbitration.TryParseRunningState(json, out _));
    }

    [Fact]
    public void IsAlive_SeesThisVeryProcess()
    {
        using var current = System.Diagnostics.Process.GetCurrentProcess();
        var self = new ModeArbitration.RunningState(
            Environment.ProcessId,
            AppMode.Gui,
            new DateTimeOffset(current.StartTime));

        Assert.True(ModeArbitration.IsAlive(self));
    }

    [Fact]
    public void IsAlive_RejectsAPidThatCouldNeverExist()
    {
        Assert.False(ModeArbitration.IsAlive(new ModeArbitration.RunningState(0, AppMode.Gui, null)));
        Assert.False(ModeArbitration.IsAlive(new ModeArbitration.RunningState(-1, AppMode.Gui, null)));
    }

    /// <summary>
    /// The pid-reuse guard: an unrelated process that inherited the pid must not be mistaken for the
    /// owner, or a stale record would block mode switching until reboot.
    /// </summary>
    [Fact]
    public void IsAlive_RejectsARecycledPid()
    {
        var recycled = new ModeArbitration.RunningState(
            Environment.ProcessId,
            AppMode.Gui,
            DateTimeOffset.UnixEpoch);

        Assert.False(ModeArbitration.IsAlive(recycled));
    }

    [Theory]
    [InlineData("Background", AppMode.Background)]
    [InlineData("background", AppMode.Background)]
    [InlineData("Gui", AppMode.Gui)]
    [InlineData("nonsense", AppMode.Gui)]
    [InlineData(null, AppMode.Gui)]
    public void ParseMode_FallsBackToTheModeThatCanFixItself(string? value, AppMode expected)
    {
        Assert.Equal(expected, SettingsStore.ParseMode(value));
    }
}
