using Microsoft.Win32.SafeHandles;
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;

namespace Dawn44.Core;

/// <summary>
/// Talks to the Dawn 4.4 control interface. Moved out of the WinUI project unchanged so the
/// headless background mode can share it. The transfer parameters, the 100ms gap between the two
/// read commands, and the 8 x 50ms response retry are all device-verified behavior — do not
/// "optimize" them without re-testing against real hardware.
/// </summary>
public sealed class DawnHidDevice
{
    private const ushort VendorId = DawnProtocol.VendorId;
    private const ushort ProductId = DawnProtocol.ProductId;
    private const byte ReportId = DawnProtocol.ReportId;
    private const int OutputReportLength = DawnProtocol.OutputReportLength;

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

    private readonly object _deviceGate = new();

    /// <summary>
    /// The resolved control interface, kept between commands. Every command used to re-enumerate
    /// every HID device on the machine — opening each one, pulling its preparsed data and marshalling
    /// its caps — which for a single volume keypress is dozens of handle opens and a few kilobytes of
    /// garbage. Held down, the volume shortcut did that twelve times a second, which is what made the
    /// resident's memory climb while the volume moved and made each write slow enough that keypresses
    /// piled up behind it.
    /// </summary>
    /// <remarks>
    /// Any disconnect-shaped failure clears this and the command is retried once against a fresh
    /// enumeration, so a path invalidated by a replug costs one retry rather than a wrong answer.
    /// </remarks>
    private HidDeviceInfo? _device;

    public Task<DawnDeviceState?> TryReadStateAsync(CancellationToken cancellationToken = default)
    {
        return Task.Run<DawnDeviceState?>(() =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            return WithDevice<DawnDeviceState?>(ReadState, null);
        }, cancellationToken);
    }

    public Task<DawnDeviceState> ReadStateAsync(CancellationToken cancellationToken = default)
    {
        return Task.Run(() =>
        {
            cancellationToken.ThrowIfCancellationRequested();

            return WithDeviceOrThrow(ReadState);
        }, cancellationToken);
    }

    private static DawnDeviceState ReadState(HidDeviceInfo device)
    {
        var stateResponse = SendCommand(device, DawnProtocol.CommandReadState, 0, readBack: true);
        Thread.Sleep(100);
        var volumeResponse = SendCommand(device, DawnProtocol.CommandReadVolume, 0, readBack: true);
        return ParseState(stateResponse, volumeResponse);
    }

    private static DawnDeviceState ParseState(CommandResult stateResponse, CommandResult volumeResponse)
    {
        var hasState = DawnProtocol.IsResponseFor(stateResponse.Data, DawnProtocol.CommandReadState);
        var hasVolume = DawnProtocol.IsResponseFor(volumeResponse.Data, DawnProtocol.CommandReadVolume);

        if (!hasState && !hasVolume)
        {
            return new DawnDeviceState(-1, -1, -1, -1, 0, stateResponse.Device.Path);
        }

        var rawVolume = hasVolume ? volumeResponse.Data[5] : (byte)0;

        // A raw byte outside the table is unknown, not zero. It used to fall back to display 0, which
        // reads as "quietest" but is indistinguishable from a real reading, so callers could not tell
        // a failed decode from a genuine value. -1 is what every other field here already uses for
        // "the device did not tell us", and both the GUI and VolumeController check for it.
        var displayVolume = hasVolume ? DawnProtocol.RawToVolume(rawVolume) ?? -1 : -1;
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
        return SendWriteAsync(DawnProtocol.CommandFilter, DawnProtocol.Clamp(value, 0, DawnProtocol.MaxFilter), cancellationToken);
    }

    public Task<bool> TrySetFilterAsync(int value, CancellationToken cancellationToken = default)
    {
        return TrySendWriteAsync(DawnProtocol.CommandFilter, DawnProtocol.Clamp(value, 0, DawnProtocol.MaxFilter), cancellationToken);
    }

    public Task SetGainAsync(int value, CancellationToken cancellationToken = default)
    {
        return SendWriteAsync(DawnProtocol.CommandGain, DawnProtocol.Clamp(value, 0, DawnProtocol.MaxGain), cancellationToken);
    }

    public Task<bool> TrySetGainAsync(int value, CancellationToken cancellationToken = default)
    {
        return TrySendWriteAsync(DawnProtocol.CommandGain, DawnProtocol.Clamp(value, 0, DawnProtocol.MaxGain), cancellationToken);
    }

    public Task SetLedAsync(int value, CancellationToken cancellationToken = default)
    {
        return SendWriteAsync(DawnProtocol.CommandLed, DawnProtocol.Clamp(value, 0, DawnProtocol.MaxLed), cancellationToken);
    }

    public Task<bool> TrySetLedAsync(int value, CancellationToken cancellationToken = default)
    {
        return TrySendWriteAsync(DawnProtocol.CommandLed, DawnProtocol.Clamp(value, 0, DawnProtocol.MaxLed), cancellationToken);
    }

    public Task SetVolumeAsync(int displayVolume, CancellationToken cancellationToken = default)
    {
        return SendWriteAsync(DawnProtocol.CommandVolume, DawnProtocol.VolumeToRaw(displayVolume), cancellationToken);
    }

    public Task<bool> TrySetVolumeAsync(int displayVolume, CancellationToken cancellationToken = default)
    {
        return TrySendWriteAsync(DawnProtocol.CommandVolume, DawnProtocol.VolumeToRaw(displayVolume), cancellationToken);
    }

    private Task SendWriteAsync(byte command, int value, CancellationToken cancellationToken)
    {
        return Task.Run(() =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            WithDeviceOrThrow(device =>
            {
                SendCommand(device, command, (byte)value, readBack: false);
                return true;
            });
        }, cancellationToken);
    }

    private Task<bool> TrySendWriteAsync(byte command, int value, CancellationToken cancellationToken)
    {
        return Task.Run(() =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            return WithDevice(
                device =>
                {
                    SendCommand(device, command, (byte)value, readBack: false);
                    return true;
                },
                false);
        }, cancellationToken);
    }

    /// <summary>
    /// Runs <paramref name="action"/> against the cached interface, and on a disconnect-shaped error
    /// drops the cache and tries once more against a fresh enumeration. Returns
    /// <paramref name="failure"/> when there is no device to talk to.
    /// </summary>
    /// <remarks>
    /// The retry is what makes caching safe: a cached path that died with the last unplug fails at
    /// <c>CreateFileW</c> with one of the disconnect codes, and the second attempt sees the current
    /// device list. Without it the first keypress after a replug would be swallowed.
    /// </remarks>
    private T WithDevice<T>(Func<HidDeviceInfo, T> action, T failure)
    {
        for (var attempt = 0; attempt < 2; attempt++)
        {
            var device = ResolveDevice();
            if (device is null)
            {
                return failure;
            }

            try
            {
                return action(device);
            }
            catch (Win32Exception ex) when (DawnProtocol.IsDisconnectedWin32Error(ex.NativeErrorCode))
            {
                InvalidateDevice(device);
            }
        }

        return failure;
    }

    /// <summary>
    /// The throwing counterpart of <see cref="WithDevice{T}"/>, for the callers whose failures the GUI
    /// reports to the user. Same one retry after a disconnect-shaped error; if the second attempt fails
    /// too, the exception is left to propagate.
    /// </summary>
    private T WithDeviceOrThrow<T>(Func<HidDeviceInfo, T> action)
    {
        for (var attempt = 0; ; attempt++)
        {
            var device = ResolveDevice()
                ?? throw new FileNotFoundException("Dawn 4.4 HID interface was not found.");

            try
            {
                return action(device);
            }
            catch (Win32Exception ex)
                when (attempt == 0 && DawnProtocol.IsDisconnectedWin32Error(ex.NativeErrorCode))
            {
                InvalidateDevice(device);
            }
        }
    }

    private HidDeviceInfo? ResolveDevice()
    {
        lock (_deviceGate)
        {
            _device ??= FindDawn();
            return _device;
        }
    }

    /// <summary>
    /// Only clears the cache when it still holds the interface that failed, so a concurrent caller
    /// that has already re-resolved does not lose its fresh answer.
    /// </summary>
    private void InvalidateDevice(HidDeviceInfo failed)
    {
        lock (_deviceGate)
        {
            if (ReferenceEquals(_device, failed))
            {
                _device = null;
            }
        }
    }

    private static HidDeviceInfo? FindDawn()
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

    private static CommandResult SendCommand(HidDeviceInfo device, byte command, byte value, bool readBack)
    {
        using var handle = OpenHandle(device.Path, readWrite: true);
        var report = DawnProtocol.CreateReport(command, value);

        WriteReport(handle, report);

        if (!readBack)
        {
            return new CommandResult(device, Array.Empty<byte>());
        }

        var responseLength = Math.Max(device.InputReportLength, OutputReportLength);
        var response = ReadResponse(handle, command, responseLength);
        return new CommandResult(device, response);
    }

    /// <summary>
    /// The device needs a moment before it answers, and it may answer a different command first,
    /// so poll up to eight times at 50ms and keep the last payload if none matched.
    /// </summary>
    private static byte[] ReadResponse(SafeFileHandle handle, byte command, int length)
    {
        byte[] lastData = [];
        for (var attempt = 0; attempt < 8; attempt++)
        {
            Thread.Sleep(50);
            lastData = GetInputReport(handle, ReportId, length);
            if (DawnProtocol.IsResponseFor(lastData, command))
            {
                return lastData;
            }
        }

        return lastData;
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
                    // cbSize of SP_DEVICE_INTERFACE_DETAIL_DATA_W: 8 on 64-bit, 6 on 32-bit.
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
