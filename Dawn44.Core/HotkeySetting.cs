using System;

namespace Dawn44.Core;

/// <summary>
/// A global shortcut binding: a mask of <see cref="HotkeyModifiers"/> plus one virtual key.
/// </summary>
public readonly record struct HotkeySetting(uint Modifiers, uint Vk);

/// <summary>
/// The MOD_* values. These are still the <c>RegisterHotKey</c> constants even though the app now
/// polls <c>GetAsyncKeyState</c> instead, because they are what the persisted settings already hold.
/// </summary>
public static class HotkeyModifiers
{
    public const uint Alt = 0x0001;
    public const uint Control = 0x0002;
    public const uint Shift = 0x0004;
    public const uint Win = 0x0008;

    /// <summary>The shipped default for both volume shortcuts.</summary>
    public const uint AltControl = Alt | Control;
}

/// <summary>
/// The virtual-key codes the hotkey code needs. The bound keys are <see cref="uint"/> to match the
/// persisted setting type; the modifier keys are <see cref="int"/> because that is what
/// <c>GetAsyncKeyState</c> takes. The split is deliberate — do not unify the types.
/// </summary>
public static class HotkeyVirtualKeys
{
    public const uint Up = 0x26;
    public const uint Down = 0x28;

    public const int Shift = 0x10;
    public const int Control = 0x11;
    public const int Menu = 0x12;
    public const int LeftWin = 0x5B;
    public const int RightWin = 0x5C;
}
