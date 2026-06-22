namespace Dawn44.WinUI;

public sealed record DawnDeviceState(
    int Filter,
    int Gain,
    int Led,
    int Volume,
    int VolumeRaw,
    string DevicePath);
