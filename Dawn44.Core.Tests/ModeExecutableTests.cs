using Dawn44.Core;
using Xunit;

namespace Dawn44.Core.Tests;

/// <summary>
/// The mapping from a mode to the executable that serves it. <see cref="ModeExecutable.Resolve"/>
/// and <see cref="ModeExecutable.TryStart"/> touch the filesystem and start processes, so they are
/// verified on a real install instead.
/// </summary>
public class ModeExecutableTests
{
    [Fact]
    public void FileNameFor_PicksTheExecutableThatServesTheMode()
    {
        Assert.Equal(ModeExecutable.GuiFileName, ModeExecutable.FileNameFor(AppMode.Gui));
        Assert.Equal(ModeExecutable.BackgroundFileName, ModeExecutable.FileNameFor(AppMode.Background));
    }

    /// <summary>
    /// The resident has no window to hide, and passing it <c>--tray</c> in the logon entry is the
    /// kind of leftover that survives for versions because nothing complains about it.
    /// </summary>
    [Fact]
    public void ArgumentsFor_OnlyGivesTheTraySwitchToTheWindow()
    {
        Assert.Equal(ModeExecutable.TraySwitch, ModeExecutable.ArgumentsFor(AppMode.Gui));
        Assert.Equal(string.Empty, ModeExecutable.ArgumentsFor(AppMode.Background));
    }
}
