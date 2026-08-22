using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using System.Text.Json;

namespace Dawn44.Core;

/// <summary>
/// The single reader/writer for <c>%LOCALAPPDATA%\Dawn4.4 Control\settings.json</c>.
/// </summary>
/// <remarks>
/// <para>
/// Three deliberate differences from the original in-window implementation:
/// </para>
/// <para>
/// 1. The cache is guarded by a lock. The hotkey poll thread re-reads the bindings every 15ms
///    while the UI thread can write through <see cref="SaveSetting"/>, which is an unsynchronized
///    <see cref="Dictionary{TKey, TValue}"/> read/write race in the original.
/// </para>
/// <para>
/// 2. JSON is handled with <see cref="JsonDocument"/> and <see cref="Utf8JsonWriter"/> rather than
///    <c>JsonSerializer.Deserialize&lt;Dictionary&lt;string, string&gt;&gt;</c>, because the
///    background executable is published with NativeAOT and the reflection-based serializer is not
///    trim-safe. The output is byte-compatible: two-space indentation and the same default escaping.
///    Reading is also more forgiving — a stray non-string value now skips that one entry instead of
///    discarding the whole file (which the original would then overwrite on the next save).
/// </para>
/// <para>
/// 3. <see cref="GetDoubleSetting"/> parses with the invariant culture, matching what
///    <see cref="SaveDoubleSetting"/> has always written.
/// </para>
/// </remarks>
public static class SettingsStore
{
    public const string CloseBehaviorKey = "CloseBehavior";
    public const string BackgroundImageTokenKey = "BackgroundImageToken";
    public const string BackgroundImageNameKey = "BackgroundImageName";
    public const string BackgroundZoomKey = "BackgroundZoom";
    public const string BackgroundOffsetXKey = "BackgroundOffsetX";
    public const string BackgroundOffsetYKey = "BackgroundOffsetY";
    public const string ResizeLockedKey = "ResizeLocked";
    public const string LanguageKey = "Language";
    public const string HotkeyVolumeUpModifiersKey = "HotkeyVolumeUpModifiers";
    public const string HotkeyVolumeUpVkKey = "HotkeyVolumeUpVk";
    public const string HotkeyVolumeDownModifiersKey = "HotkeyVolumeDownModifiers";
    public const string HotkeyVolumeDownVkKey = "HotkeyVolumeDownVk";
    public const string StartupEnabledKey = "StartupEnabled";
    public const string HotkeyOsdEnabledKey = "HotkeyOsdEnabled";
    public const string RunAsAdminKey = "RunAsAdmin";

    public static readonly string SettingsDirectory = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "Dawn4.4 Control");

    public static readonly string SettingsFilePath = Path.Combine(SettingsDirectory, "settings.json");

    private static readonly object Gate = new();
    private static Dictionary<string, string>? _cache;

    public static string? GetCloseBehavior()
    {
        return GetStringSetting(CloseBehaviorKey, "Ask");
    }

    public static void SaveCloseBehavior(string behavior)
    {
        SaveSetting(CloseBehaviorKey, behavior);
    }

    public static bool GetStartupEnabled()
    {
        return GetBoolSetting(StartupEnabledKey, false);
    }

    public static void SaveStartupEnabled(bool enabled)
    {
        SaveSetting(StartupEnabledKey, enabled ? "true" : "false");
    }

    public static bool GetHotkeyOsdEnabled()
    {
        return GetBoolSetting(HotkeyOsdEnabledKey, true);
    }

    public static void SaveHotkeyOsdEnabled(bool enabled)
    {
        SaveSetting(HotkeyOsdEnabledKey, enabled ? "true" : "false");
    }

    public static bool GetRunAsAdmin()
    {
        return GetBoolSetting(RunAsAdminKey, false);
    }

    public static void SaveRunAsAdmin(bool enabled)
    {
        SaveSetting(RunAsAdminKey, enabled ? "true" : "false");
    }

    public static bool GetResizeLocked()
    {
        return GetBoolSetting(ResizeLockedKey, true);
    }

    public static void SaveResizeLocked(bool locked)
    {
        SaveSetting(ResizeLockedKey, locked ? "true" : "false");
    }

    public static string GetLanguage()
    {
        return NormalizeLanguage(GetStringSetting(LanguageKey, null));
    }

    public static void SaveLanguage(string language)
    {
        SaveSetting(LanguageKey, NormalizeLanguage(language));
    }

    public static string NormalizeLanguage(string? language)
    {
        return string.Equals(language, "zh", StringComparison.OrdinalIgnoreCase) ? "zh" : "en";
    }

    public static string? GetBackgroundImageToken()
    {
        return GetStringSetting(BackgroundImageTokenKey, null);
    }

    public static string? GetBackgroundImageName()
    {
        return GetStringSetting(BackgroundImageNameKey, null);
    }

    public static HotkeySetting GetVolumeUpHotkey()
    {
        return ResolveHotkey(
            GetStringSetting(HotkeyVolumeUpModifiersKey, null),
            GetStringSetting(HotkeyVolumeUpVkKey, null),
            new HotkeySetting(HotkeyModifiers.AltControl, HotkeyVirtualKeys.Up));
    }

    public static HotkeySetting GetVolumeDownHotkey()
    {
        return ResolveHotkey(
            GetStringSetting(HotkeyVolumeDownModifiersKey, null),
            GetStringSetting(HotkeyVolumeDownVkKey, null),
            new HotkeySetting(HotkeyModifiers.AltControl, HotkeyVirtualKeys.Down));
    }

    public static void SaveVolumeUpHotkey(HotkeySetting hotkey)
    {
        SaveHotkey(HotkeyVolumeUpModifiersKey, HotkeyVolumeUpVkKey, hotkey);
    }

    public static void SaveVolumeDownHotkey(HotkeySetting hotkey)
    {
        SaveHotkey(HotkeyVolumeDownModifiersKey, HotkeyVolumeDownVkKey, hotkey);
    }

    private static void SaveHotkey(string modifiersKey, string vkKey, HotkeySetting hotkey)
    {
        SaveSetting(modifiersKey, hotkey.Modifiers.ToString(CultureInfo.InvariantCulture));
        SaveSetting(vkKey, hotkey.Vk.ToString(CultureInfo.InvariantCulture));
    }

    /// <summary>
    /// A zero in either half means the binding was cleared rather than customised, so the whole
    /// default is restored. An unparseable half takes the default for that field only.
    /// </summary>
    internal static HotkeySetting ResolveHotkey(string? modifiersValue, string? vkValue, HotkeySetting defaultValue)
    {
        var modifiers = ConvertSettingToUInt32(modifiersValue, defaultValue.Modifiers);
        var vk = ConvertSettingToUInt32(vkValue, defaultValue.Vk);

        return modifiers == 0 || vk == 0
            ? defaultValue
            : new HotkeySetting(modifiers, vk);
    }

    internal static uint ConvertSettingToUInt32(string? value, uint defaultValue)
    {
        return uint.TryParse(value, out var parsed) ? parsed : defaultValue;
    }

    /// <summary>
    /// Parsed with the invariant culture because <see cref="SaveDoubleSetting"/> always writes with
    /// it. The original read with the current culture, so on a comma-decimal locale a stored
    /// <c>"1.35"</c> came back as 135 and the background zoom jumped; keep both sides invariant.
    /// </summary>
    public static double GetDoubleSetting(string key, double defaultValue)
    {
        var value = GetStringSetting(key, null);
        return double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed)
            ? parsed
            : defaultValue;
    }

    public static void SaveDoubleSetting(string key, double value)
    {
        SaveSetting(key, value.ToString(CultureInfo.InvariantCulture));
    }

    public static bool GetBoolSetting(string key, bool defaultValue)
    {
        var value = GetStringSetting(key, null);
        return bool.TryParse(value, out var parsed) ? parsed : defaultValue;
    }

    public static uint GetUIntSetting(string key, uint defaultValue)
    {
        return ConvertSettingToUInt32(GetStringSetting(key, null), defaultValue);
    }

    public static string? GetStringSetting(string key, string? defaultValue)
    {
        lock (Gate)
        {
            return Load().TryGetValue(key, out var value) ? value : defaultValue;
        }
    }

    public static void SaveSetting(string key, string value)
    {
        lock (Gate)
        {
            var settings = Load();
            settings[key] = value;
            Persist(settings);
        }
    }

    public static void RemoveSetting(string key)
    {
        lock (Gate)
        {
            var settings = Load();
            if (settings.Remove(key))
            {
                Persist(settings);
            }
        }
    }

    /// <summary>
    /// Drops the cache so the next read comes off disk. Needed when the other mode's process may
    /// have rewritten the file while this one was idle.
    /// </summary>
    public static void Invalidate()
    {
        lock (Gate)
        {
            _cache = null;
        }
    }

    private static Dictionary<string, string> Load()
    {
        if (_cache is not null)
        {
            return _cache;
        }

        try
        {
            if (File.Exists(SettingsFilePath))
            {
                _cache = ParseJson(File.ReadAllText(SettingsFilePath));
                return _cache;
            }
        }
        catch
        {
            // Corrupt settings should not prevent the controller from opening.
        }

        _cache = new Dictionary<string, string>(StringComparer.Ordinal);
        return _cache;
    }

    private static void Persist(Dictionary<string, string> settings)
    {
        try
        {
            Directory.CreateDirectory(SettingsDirectory);
            File.WriteAllText(SettingsFilePath, SerializeJson(settings));
        }
        catch
        {
            // Settings persistence is best-effort; device control should continue working.
        }
    }

    internal static Dictionary<string, string> ParseJson(string json)
    {
        var settings = new Dictionary<string, string>(StringComparer.Ordinal);
        using var document = JsonDocument.Parse(json);
        if (document.RootElement.ValueKind != JsonValueKind.Object)
        {
            return settings;
        }

        foreach (var property in document.RootElement.EnumerateObject())
        {
            if (property.Value.ValueKind == JsonValueKind.String)
            {
                settings[property.Name] = property.Value.GetString() ?? string.Empty;
            }
        }

        return settings;
    }

    internal static string SerializeJson(Dictionary<string, string> settings)
    {
        using var buffer = new MemoryStream();
        using (var writer = new Utf8JsonWriter(buffer, new JsonWriterOptions { Indented = true }))
        {
            writer.WriteStartObject();
            foreach (var pair in settings)
            {
                writer.WriteString(pair.Key, pair.Value);
            }

            writer.WriteEndObject();
        }

        return Encoding.UTF8.GetString(buffer.ToArray());
    }
}
