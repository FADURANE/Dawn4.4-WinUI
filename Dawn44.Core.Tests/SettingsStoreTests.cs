using System.Collections.Generic;
using Dawn44.Core;
using Xunit;

namespace Dawn44.Core.Tests;

/// <summary>
/// Only the pure helpers are exercised — these tests never read or write the real
/// <c>%LOCALAPPDATA%\Dawn4.4 Control\settings.json</c>, so running them cannot disturb an install.
/// </summary>
public class SettingsStoreTests
{
    [Fact]
    public void ParseJson_ReadsFlatStringPairs()
    {
        var settings = SettingsStore.ParseJson("""
            {
              "Language": "zh",
              "HotkeyVolumeUpVk": "38"
            }
            """);

        Assert.Equal("zh", settings["Language"]);
        Assert.Equal("38", settings["HotkeyVolumeUpVk"]);
    }

    [Fact]
    public void ParseJson_KeepsValidEntriesWhenOneValueIsNotAString()
    {
        // The old reflection-based deserializer threw here and lost the whole file, which the next
        // save would then overwrite. Keeping the readable entries is the point of the rewrite.
        var settings = SettingsStore.ParseJson("""{"Language":"en","BackgroundZoom":1.5}""");

        Assert.Equal("en", settings["Language"]);
        Assert.False(settings.ContainsKey("BackgroundZoom"));
    }

    [Fact]
    public void ParseJson_TreatsANonObjectRootAsEmpty()
    {
        Assert.Empty(SettingsStore.ParseJson("[]"));
        Assert.Empty(SettingsStore.ParseJson("{}"));
    }

    [Fact]
    public void ParseJson_IsCaseSensitive()
    {
        var settings = SettingsStore.ParseJson("""{"Language":"zh"}""");

        Assert.False(settings.ContainsKey("language"));
    }

    [Fact]
    public void SerializeJson_WritesTwoSpaceIndentedPairs()
    {
        var json = SettingsStore.SerializeJson(new Dictionary<string, string>
        {
            ["Language"] = "zh",
            ["RunAsAdmin"] = "true",
        });

        var expected = string.Join(
            System.Environment.NewLine,
            "{",
            "  \"Language\": \"zh\",",
            "  \"RunAsAdmin\": \"true\"",
            "}");

        Assert.Equal(expected, json);
    }

    [Fact]
    public void SerializeJson_RoundTripsThroughParseJson()
    {
        var original = new Dictionary<string, string>
        {
            ["Language"] = "zh",
            ["BackgroundImageName"] = "壁纸 with spaces.png",
            ["BackgroundZoom"] = "1.35",
        };

        var restored = SettingsStore.ParseJson(SettingsStore.SerializeJson(original));

        Assert.Equal(original.Count, restored.Count);
        foreach (var pair in original)
        {
            Assert.Equal(pair.Value, restored[pair.Key]);
        }
    }
}
