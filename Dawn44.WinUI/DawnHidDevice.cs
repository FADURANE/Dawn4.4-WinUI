using Microsoft.Win32.SafeHandles;
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;

namespace Dawn44.WinUI;

public sealed class DawnHidDevice
{
    private const ushort VendorId = 0x2FC6;
    private const ushort ProductId = 0xF067;
    private const byte ReportId = 0x00;
    private const int OutputReportLength = 8;

    private const uint GenericRead = 0x80000000;
    private const uint GenericWrite = 0x40000000;
    private const uint FileShareRead = 0x00000001;
    private const uint FileShareWrite = 0x00000002;
    private const uint OpenExisting = 3;
    private const uint FileAttributeNormal = 0x00000080;

    private const uint DigcfPresent = 0x00000002;
    private const uint DigcfDeviceInterface = 0x00000010;
    private const int ErrorNoMoreItems = 259;
    private const int ErrorInsufficientBuffer = 122;
    private const int HidpStatusSuccess = 0x00110000;

    private static readonly int[] VolumeTable =
    [
        255, 200, 180, 170, 160, 150, 140, 130, 122, 116,
        110, 106, 102, 98, 94, 90, 88, 86, 84, 82,
        80, 78, 76, 74, 72, 70, 68, 66, 64, 62,
        60, 58, 56, 54, 52, 50, 48, 46, 44, 42,
        40, 38, 36, 34, 32, 30, 28, 26, 24, 22,
        20, 18, 16, 14, 12, 10, 8, 6, 4, 2,
        0,
    ];

    public Task<DawnDeviceState?> TryReadStateAsync(CancellationToken cancellationToken = default)
    {
        return Task.Run<DawnDeviceState?>(() =>
        {
            try
            {
                cancellationToken.ThrowIfCancellationRequested();
                var device = TryOpenDawn();
                if (device is null)
                {
                    return null;
                }

                var stateResponse = SendCommand(device, 0xA3, 0, readBack: true);
                Thread.Sleep(100);
                var volumeResponse = SendCommand(device, 0xA2, 0, readBack: true);
                return ParseState(device, stateResponse, volumeResponse);
            }
            catch (Win32Exception ex) when (IsDisconnectedWin32Error(ex.NativeErrorCode))
            {
                return null;
            }
        }, cancellationToken);
    }
    public Task<DawnDeviceState> ReadStateAsync(CancellationToken cancellationToken = default)
    {
        return Task.Run(() =>
        {
            cancellationToken.ThrowIfCancellationRequested();

            var device = OpenDawn();
            var stateResponse = SendCommand(device, 0xA3, 0, readBack: true);
            Thread.Sleep(100);
            var volumeResponse = SendCommand(device, 0xA2, 0, readBack: true);

            return ParseState(device, stateResponse, volumeResponse);
        }, cancellationToken);
    }

    private static DawnDeviceState ParseState(HidDeviceInfo device, CommandResult stateResponse, CommandResult volumeResponse)
    {            var hasState = IsResponseFor(stateResponse.Data, 0xA3);
            var hasVolume = IsResponseFor(volumeResponse.Data, 0xA2);

            if (!hasState && !hasVolume)
            {
                return new DawnDeviceState(-1, -1, -1, -1, 0, stateResponse.Device.Path);
            }

            var rawVolume = hasVolume ? volumeResponse.Data[5] : (byte)0;
            var displayVolume = hasVolume ? RawToVolume(rawVolume) ?? 0 : -1;
            return new DawnDeviceState(
                hasState ? stateResponse.Data[4] : -1,
                hasState ? stateResponse.Data[5] : -1,
                hasState ? stateResponse.Data[6] : -1,
                displayVolume,
                rawVolume,
                stateResponse.Device.Path);
    }
    public Task SetFilterAsync(int value, CancellationToken cancellationToken = default)
    {
        return SendWriteAsync(0x01, Clamp(value, 0, 4), cancellationToken);
    }

    public Task<bool> TrySetFilterAsync(int value, CancellationToken cancellationToken = default)
    {
        return TrySendWriteAsync(0x01, Clamp(value, 0, 4), cancellationToken);
    }

    public Task SetGainAsync(int value, CancellationToken cancellationToken = default)
    {
        return SendWriteAsync(0x02, Clamp(value, 0, 1), cancellationToken);
    }

    public Task<bool> TrySetGainAsync(int value, CancellationToken cancellationToken = default)
    {
        return TrySendWriteAsync(0x02, Clamp(value, 0, 1), cancellationToken);
    }

    public Task SetLedAsync(int value, CancellationToken cancellationToken = default)
    {
        return SendWriteAsync(0x06, Clamp(value, 0, 2), cancellationToken);
    }

    public Task<bool> TrySetLedAsync(int value, CancellationToken cancellationToken = default)
    {
        return TrySendWriteAsync(0x06, Clamp(value, 0, 2), cancellationToken);
    }

    public Task SetVolumeAsync(int displayVolume, CancellationToken cancellationToken = default)
    {
        var index = Clamp(displayVolume, 0, VolumeTable.Length - 1);
        return SendWriteAsync(0x04, VolumeTable[index], cancellationToken);
    }

    public Task<bool> TrySetVolumeAsync(int displayVolume, CancellationToken cancellationToken = default)
    {
        var index = Clamp(displayVolume, 0, VolumeTable.Length - 1);
        return TrySendWriteAsync(0x04, VolumeTable[index], cancellationToken);
    }

    private static Task SendWriteAsync(byte command, int value, CancellationToken cancellationToken)
    {
        return Task.Run(() =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            var device = OpenDawn();
            SendCommand(device, command, (byte)value, readBack: false);
        }, cancellationToken);
    }

    private static Task<bool> TrySendWriteAsync(byte command, int value, CancellationToken cancellationToken)
    {
        return Task.Run(() =>
        {
            try
            {
                cancellationToken.ThrowIfCancellationRequested();
                var device = TryOpenDawn();
                if (device is null)
                {
                    return false;
                }

                SendCommand(device, command, (byte)value, readBack: false);
                return true;
            }
            catch (Win32Exception ex) when (IsDisconnectedWin32Error(ex.NativeErrorCode))
            {
                return false;
            }
        }, cancellationToken);
    }

    private static CommandResult SendCommand(HidDeviceInfo device, byte command, byte value, bool readBack)
    {
        using var handle = OpenHandle(device.Path, readWrite: true);
        var report = CreateReport(command, value);

        WriteReport(handle, report);

        if (!readBack)
        {
            return new CommandResult(device, Array.Empty<byte>());
        }

        var responseLength = Math.Max(device.InputReportLength, OutputReportLength);
        var response = ReadResponse(handle, command, responseLength);
        return new CommandResult(device, response);
    }

    private static HidDeviceInfo? TryOpenDawn()
    {
        var devices = EnumerateHidDevices()
            .Where(device => device.VendorId == VendorId && device.ProductId == ProductId)
            .ToList();

        if (devices.Count == 0)
        {
            return null;
        }

        return devices.FirstOrDefault(device => device.Path.Contains("mi_02", StringComparison.OrdinalIgnoreCase))
            ?? devices[0];
    }
    private static HidDeviceInfo OpenDawn()
    {
        return TryOpenDawn() ?? throw new FileNotFoundException("Dawn 4.4 HID interface was not found.");
    }

    private static byte[] CreateReport(byte command, byte value)
    {
        return [ReportId, 0xC0, 0xA5, command, value, 0x00, 0x00, 0x00];
    }

    private static byte[] ReadResponse(SafeFileHandle handle, byte command, int length)
    {
        byte[] lastData = [];
        for (var attempt = 0; attempt < 8; attempt++)
        {
            Thread.Sleep(50);
            lastData = GetInputReport(handle, ReportId, length);
            if (IsResponseFor(lastData, command))
            {
                return lastData;
            }
        }

        return lastData;
    }

    private static bool IsResponseFor(byte[] data, byte command)
    {
        return data.Length >= 4 && data[1] == 0xA0 && data[2] == 0xA5 && data[3] == command;
    }

    private static string ToHex(byte[] data)
    {
        return data.Length == 0 ? "empty" : string.Join(" ", data.Select(value => value.ToString("X2")));
    }
    private static int? RawToVolume(byte raw)
    {
        var index = Array.IndexOf(VolumeTable, raw);
        return index >= 0 ? index : null;
    }

    private static int Clamp(int value, int minimum, int maximum)
    {
        return Math.Min(Math.Max(value, minimum), maximum);
    }

    private static bool IsDisconnectedWin32Error(int errorCode)
    {
        return errorCode is 2 or 3 or 6 or 21 or 31 or 1167;
    }

    private static void WriteReport(SafeFileHandle handle, byte[] report)
    {
        if (!WriteFile(handle, report, report.Length, out var written, IntPtr.Zero) || written != report.Length)
        {
            throw new Win32Exception(Marshal.GetLastWin32Error());
        }
    }

    private static byte[] GetInputReport(SafeFileHandle handle, byte reportId, int length)
    {
        var buffer = new byte[length];
        buffer[0] = reportId;
        if (!HidD_GetInputReport(handle, buffer, buffer.Length))
        {
            throw new Win32Exception(Marshal.GetLastWin32Error());
        }

        return buffer;
    }

    private static SafeFileHandle OpenHandle(string path, bool readWrite)
    {
        var access = readWrite ? GenericRead | GenericWrite : 0;
        var handle = CreateFileW(
            path,
            access,
            FileShareRead | FileShareWrite,
            IntPtr.Zero,
            OpenExisting,
            FileAttributeNormal,
            IntPtr.Zero);

        if (handle.IsInvalid)
        {
            throw new Win32Exception(Marshal.GetLastWin32Error());
        }

        return handle;
    }

    private static List<HidDeviceInfo> EnumerateHidDevices()
    {
        HidD_GetHidGuid(out var hidGuid);
        var infoSet = SetupDiGetClassDevsW(ref hidGuid, null, IntPtr.Zero, DigcfPresent | DigcfDeviceInterface);
        if (infoSet == IntPtr.Zero || infoSet == new IntPtr(-1))
        {
            throw new Win32Exception(Marshal.GetLastWin32Error());
        }

        var devices = new List<HidDeviceInfo>();

        try
        {
            for (uint index = 0; ; index++)
            {
                var interfaceData = new SpDeviceInterfaceData
                {
                    CbSize = Marshal.SizeOf<SpDeviceInterfaceData>(),
                };

                if (!SetupDiEnumDeviceInterfaces(infoSet, IntPtr.Zero, ref hidGuid, index, ref interfaceData))
                {
                    var error = Marshal.GetLastWin32Error();
                    if (error == ErrorNoMoreItems)
                    {
                        break;
                    }

                    throw new Win32Exception(error);
                }

                SetupDiGetDeviceInterfaceDetailW(infoSet, ref interfaceData, IntPtr.Zero, 0, out var requiredSize, IntPtr.Zero);
                if (Marshal.GetLastWin32Error() != ErrorInsufficientBuffer)
                {
                    throw new Win32Exception(Marshal.GetLastWin32Error());
                }

                var detailDataPointer = Marshal.AllocHGlobal((int)requiredSize);
                try
                {
                    Marshal.WriteInt32(detailDataPointer, IntPtr.Size == 8 ? 8 : 6);
                    if (!SetupDiGetDeviceInterfaceDetailW(infoSet, ref interfaceData, detailDataPointer, requiredSize, out _, IntPtr.Zero))
                    {
                        throw new Win32Exception(Marshal.GetLastWin32Error());
                    }

                    var pathOffset = 4;
                    var path = Marshal.PtrToStringUni(IntPtr.Add(detailDataPointer, pathOffset));
                    if (string.IsNullOrWhiteSpace(path))
                    {
                        continue;
                    }

                    TryAddDevice(devices, path);
                }
                finally
                {
                    Marshal.FreeHGlobal(detailDataPointer);
                }
            }
        }
        finally
        {
            SetupDiDestroyDeviceInfoList(infoSet);
        }

        return devices;
    }

    private static void TryAddDevice(List<HidDeviceInfo> devices, string path)
    {
        try
        {
            using var handle = OpenHandle(path, readWrite: false);
            var attributes = new HiddAttributes
            {
                Size = Marshal.SizeOf<HiddAttributes>(),
            };

            if (!HidD_GetAttributes(handle, ref attributes))
            {
                return;
            }

            var inputLength = OutputReportLength;
            var outputLength = OutputReportLength;
            var featureLength = OutputReportLength;

            if (HidD_GetPreparsedData(handle, out var preparsedData))
            {
                try
                {
                    if (HidP_GetCaps(preparsedData, out var caps) == HidpStatusSuccess)
                    {
                        inputLength = caps.InputReportByteLength;
                        outputLength = caps.OutputReportByteLength;
                        featureLength = caps.FeatureReportByteLength;
                    }
                }
                finally
                {
                    HidD_FreePreparsedData(preparsedData);
                }
            }

            devices.Add(new HidDeviceInfo(
                path,
                attributes.VendorID,
                attributes.ProductID,
                inputLength,
                outputLength,
                featureLength));
        }
        catch
        {
            // Some HID devices reject metadata opens; skip them and keep enumerating.
        }
    }

    private sealed record HidDeviceInfo(
        string Path,
        ushort VendorId,
        ushort ProductId,
        int InputReportLength,
        int OutputReportLength,
        int FeatureReportLength);

    private sealed record CommandResult(HidDeviceInfo Device, byte[] Data);

    [StructLayout(LayoutKind.Sequential)]
    private struct SpDeviceInterfaceData
    {
        public int CbSize;
        public Guid InterfaceClassGuid;
        public int Flags;
        public IntPtr Reserved;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct HiddAttributes
    {
        public int Size;
        public ushort VendorID;
        public ushort ProductID;
        public ushort VersionNumber;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct HidpCaps
    {
        public ushort Usage;
        public ushort UsagePage;
        public ushort InputReportByteLength;
        public ushort OutputReportByteLength;
        public ushort FeatureReportByteLength;

        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 17)]
        public ushort[] Reserved;

        public ushort NumberLinkCollectionNodes;
        public ushort NumberInputButtonCaps;
        public ushort NumberInputValueCaps;
        public ushort NumberInputDataIndices;
        public ushort NumberOutputButtonCaps;
        public ushort NumberOutputValueCaps;
        public ushort NumberOutputDataIndices;
        public ushort NumberFeatureButtonCaps;
        public ushort NumberFeatureValueCaps;
        public ushort NumberFeatureDataIndices;
    }

    [DllImport("hid.dll")]
    private static extern void HidD_GetHidGuid(out Guid hidGuid);

    [DllImport("hid.dll", SetLastError = true)]
    private static extern bool HidD_GetAttributes(SafeFileHandle hidDeviceObject, ref HiddAttributes attributes);

    [DllImport("hid.dll", SetLastError = true)]
    private static extern bool HidD_GetPreparsedData(SafeFileHandle hidDeviceObject, out IntPtr preparsedData);

    [DllImport("hid.dll", SetLastError = true)]
    private static extern bool HidD_FreePreparsedData(IntPtr preparsedData);

    [DllImport("hid.dll", SetLastError = true)]
    private static extern bool HidD_GetInputReport(SafeFileHandle hidDeviceObject, byte[] reportBuffer, int reportBufferLength);

    [DllImport("hid.dll")]
    private static extern int HidP_GetCaps(IntPtr preparsedData, out HidpCaps capabilities);

    [DllImport("setupapi.dll", SetLastError = true)]
    private static extern IntPtr SetupDiGetClassDevsW(
        ref Guid classGuid,
        string? enumerator,
        IntPtr hwndParent,
        uint flags);

    [DllImport("setupapi.dll", SetLastError = true)]
    private static extern bool SetupDiEnumDeviceInterfaces(
        IntPtr deviceInfoSet,
        IntPtr deviceInfoData,
        ref Guid interfaceClassGuid,
        uint memberIndex,
        ref SpDeviceInterfaceData deviceInterfaceData);

    [DllImport("setupapi.dll", SetLastError = true)]
    private static extern bool SetupDiGetDeviceInterfaceDetailW(
        IntPtr deviceInfoSet,
        ref SpDeviceInterfaceData deviceInterfaceData,
        IntPtr deviceInterfaceDetailData,
        uint deviceInterfaceDetailDataSize,
        out uint requiredSize,
        IntPtr deviceInfoData);

    [DllImport("setupapi.dll", SetLastError = true)]
    private static extern bool SetupDiDestroyDeviceInfoList(IntPtr deviceInfoSet);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFileHandle CreateFileW(
        string fileName,
        uint desiredAccess,
        uint shareMode,
        IntPtr securityAttributes,
        uint creationDisposition,
        uint flagsAndAttributes,
        IntPtr templateFile);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool WriteFile(
        SafeFileHandle file,
        byte[] buffer,
        int numberOfBytesToWrite,
        out int numberOfBytesWritten,
        IntPtr overlapped);
}







