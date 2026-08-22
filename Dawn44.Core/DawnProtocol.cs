using System;

namespace Dawn44.Core;

/// <summary>
/// The pure, side-effect-free half of the Dawn 4.4 HID protocol: report layout, response
/// validation, and the volume table. Everything here is verified against the real device, so
/// treat the constants and the table as fixed data rather than something to tidy up. It lives
/// apart from <see cref="DawnHidDevice"/> only so it can be covered by unit tests without a
/// device attached.
/// </summary>
public static class DawnProtocol
{
    public const ushort VendorId = 0x2FC6;
    public const ushort ProductId = 0xF067;

    /// <summary>The device uses a single unnumbered report, so the leading byte is always zero.</summary>
    public const byte ReportId = 0x00;
    public const int OutputReportLength = 8;

    public const byte CommandFilter = 0x01;
    public const byte CommandGain = 0x02;
    public const byte CommandVolume = 0x04;
    public const byte CommandLed = 0x06;

    /// <summary>Reads filter, gain, and LED into data[4], data[5], and data[6] of the response.</summary>
    public const byte CommandReadState = 0xA3;

    /// <summary>Reads the raw volume into data[5] of the response.</summary>
    public const byte CommandReadVolume = 0xA2;

    public const int MaxFilter = 4;
    public const int MaxGain = 1;
    public const int MaxLed = 2;
    public const int MaxVolume = 60;

    /// <summary>Display volume 0-60 mapped to the raw attenuation byte the device expects.</summary>
    private static readonly int[] VolumeTableValues =
    [
        255, 200, 180, 170, 160, 150, 140, 130, 122, 116,
        110, 106, 102, 98, 94, 90, 88, 86, 84, 82,
        80, 78, 76, 74, 72, 70, 68, 66, 64, 62,
        60, 58, 56, 54, 52, 50, 48, 46, 44, 42,
        40, 38, 36, 34, 32, 30, 28, 26, 24, 22,
        20, 18, 16, 14, 12, 10, 8, 6, 4, 2,
        0,
    ];

    public static ReadOnlySpan<int> VolumeTable => VolumeTableValues;

    public static int VolumeStepCount => VolumeTableValues.Length;

    public static byte[] CreateReport(byte command, byte value)
    {
        return [ReportId, 0xC0, 0xA5, command, value, 0x00, 0x00, 0x00];
    }

    public static bool IsResponseFor(byte[] data, byte command)
    {
        return data.Length >= 4 && data[1] == 0xA0 && data[2] == 0xA5 && data[3] == command;
    }

    public static int VolumeToRaw(int displayVolume)
    {
        return VolumeTableValues[Clamp(displayVolume, 0, VolumeTableValues.Length - 1)];
    }

    /// <summary>Returns null when the device reports a raw value that is not in the table.</summary>
    public static int? RawToVolume(byte raw)
    {
        var index = Array.IndexOf(VolumeTableValues, (int)raw);
        return index >= 0 ? index : null;
    }

    public static int Clamp(int value, int minimum, int maximum)
    {
        return Math.Min(Math.Max(value, minimum), maximum);
    }

    /// <summary>
    /// Steps <paramref name="current"/> at most <paramref name="maximumStep"/> toward
    /// <paramref name="target"/>. Fast slider drags are written as a ramp rather than one jump.
    /// </summary>
    public static int MoveToward(int current, int target, int maximumStep)
    {
        if (current == target)
        {
            return target;
        }

        var delta = target - current;
        if (Math.Abs(delta) <= maximumStep)
        {
            return target;
        }

        return current + Math.Sign(delta) * maximumStep;
    }

    /// <summary>
    /// Win32 error codes that mean the dongle was unplugged rather than that the request was bad:
    /// FILE_NOT_FOUND, PATH_NOT_FOUND, INVALID_HANDLE, NOT_READY, GEN_FAILURE, DEVICE_NOT_CONNECTED.
    /// </summary>
    public static bool IsDisconnectedWin32Error(int errorCode)
    {
        return errorCode is 2 or 3 or 6 or 21 or 31 or 1167;
    }
}
