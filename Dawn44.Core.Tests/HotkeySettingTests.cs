using Dawn44.Core;
using Xunit;

namespace Dawn44.Core.Tests;

public class HotkeySettingTests
{
    [Fact]
    public void ResolveHotkey_UsesTheDefaultWhenNothingIsStored()
    {
        var fallback = new HotkeySetting(HotkeyModifiers.AltControl, HotkeyVirtualKeys.Up);

        Assert.Equal(fallback, SettingsStore.ResolveHotkey(null, null, fallback));
    }

    [Fact]
    public void ResolveHotkey_ReadsAStoredBinding()
    {
        var fallback = new HotkeySetting(HotkeyModifiers.AltControl, HotkeyVirtualKeys.Up);

        var resolved = SettingsStore.ResolveHotkey("4", "112", fallback);

        Assert.Equal(new HotkeySetting(HotkeyModifiers.Shift, 112), resolved);
    }

    /// <summary>
    /// A recorded zero means "no modifier" or "no key", which is not a usable binding, so the whole
    /// default is restored rather than a half-applied combo.
    /// </summary>
    [Theory]
    [InlineData("0", "112")]   // no modifier recorded
    [InlineData("4", "0")]     // no key recorded
    [InlineData("0", "0")]
    public void ResolveHotkey_FallsBackWholeWhenEitherHalfIsZero(string modifiers, string vk)
    {
        var fallback = new HotkeySetting(HotkeyModifiers.AltControl, HotkeyVirtualKeys.Down);

        Assert.Equal(fallback, SettingsStore.ResolveHotkey(modifiers, vk, fallback));
    }

    /// <summary>
    /// An unparseable half is a different case from a zero: it takes the default for that field only
    /// and the other stored half is kept. This is the shipped behavior, not an accident of the port.
    /// </summary>
    [Fact]
    public void ResolveHotkey_TakesThePerFieldDefaultForAnUnparseableHalf()
    {
        var fallback = new HotkeySetting(HotkeyModifiers.AltControl, HotkeyVirtualKeys.Down);

        Assert.Equal(
            new HotkeySetting(HotkeyModifiers.AltControl, 112),
            SettingsStore.ResolveHotkey("", "112", fallback));

        Assert.Equal(
            new HotkeySetting(HotkeyModifiers.Shift, HotkeyVirtualKeys.Down),
            SettingsStore.ResolveHotkey("4", "abc", fallback));
    }

    [Fact]
    public void ConvertSettingToUInt32_FallsBackOnGarbage()
    {
        Assert.Equal(7u, SettingsStore.ConvertSettingToUInt32("not a number", 7u));
        Assert.Equal(7u, SettingsStore.ConvertSettingToUInt32("-1", 7u));
        Assert.Equal(38u, SettingsStore.ConvertSettingToUInt32("38", 7u));
    }

    [Fact]
    public void Modifiers_MatchTheWin32Values()
    {
        Assert.Equal(0x0001u, HotkeyModifiers.Alt);
        Assert.Equal(0x0002u, HotkeyModifiers.Control);
        Assert.Equal(0x0004u, HotkeyModifiers.Shift);
        Assert.Equal(0x0008u, HotkeyModifiers.Win);
        Assert.Equal(0x0003u, HotkeyModifiers.AltControl);
    }

    [Fact]
    public void DefaultBindings_AreCtrlAltArrowKeys()
    {
        Assert.Equal(0x26u, HotkeyVirtualKeys.Up);
        Assert.Equal(0x28u, HotkeyVirtualKeys.Down);
    }
}
