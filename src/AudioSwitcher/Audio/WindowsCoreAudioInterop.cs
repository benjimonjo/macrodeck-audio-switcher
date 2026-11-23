using System.Runtime.InteropServices;

namespace AudioSwitcher.Audio.Interop;

// COM definitions from mmdeviceapi.h and propsys.h, plus the IPolicyConfig interface Windows 7+
// exposes for setting the default endpoint. Public so Windows can call back into NotificationClient.
#pragma warning disable CA1707, CA1711, CA1712, CA1716, CA1720


public enum DataFlow
{
    Render = 0,
    Capture = 1,
}

public enum Role
{
    Console = 0,
    Multimedia = 1,
    Communications = 2,
}

[StructLayout(LayoutKind.Sequential)]
public readonly struct PropertyKey(Guid formatId, int propertyId)
{
    public readonly Guid FormatId = formatId;
    public readonly int PropertyId = propertyId;
}

[StructLayout(LayoutKind.Sequential)]
public struct PropVariant
{
    public ushort Type;
    public ushort Reserved1;
    public ushort Reserved2;
    public ushort Reserved3;
    public IntPtr Pointer;
    public IntPtr Extra;
}

[ComImport]
[ComVisible(true)]
[Guid("A95664D2-9614-4F35-A746-DE8DB63617E6")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
public interface IMMDeviceEnumerator
{
    [PreserveSig]
    int EnumAudioEndpoints(DataFlow dataFlow, uint stateMask, out IMMDeviceCollection devices);

    [PreserveSig]
    int GetDefaultAudioEndpoint(DataFlow dataFlow, Role role, out IMMDevice endpoint);

    [PreserveSig]
    int GetDevice([MarshalAs(UnmanagedType.LPWStr)] string id, out IMMDevice device);

    [PreserveSig]
    int RegisterEndpointNotificationCallback(IMMNotificationClient client);

    [PreserveSig]
    int UnregisterEndpointNotificationCallback(IMMNotificationClient client);
}

[ComImport]
[ComVisible(true)]
[Guid("0BD7A1BE-7A1A-44DB-8397-CC5392387B5E")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
public interface IMMDeviceCollection
{
    [PreserveSig]
    int GetCount(out uint count);

    [PreserveSig]
    int Item(uint index, out IMMDevice device);
}

[ComImport]
[ComVisible(true)]
[Guid("D666063F-1587-4E43-81F1-B948E807363F")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
public interface IMMDevice
{
    [PreserveSig]
    int Activate(ref Guid iid, uint clsCtx, IntPtr activationParams, [MarshalAs(UnmanagedType.IUnknown)] out object instance);

    [PreserveSig]
    int OpenPropertyStore(uint access, out IPropertyStore properties);

    [PreserveSig]
    int GetId([MarshalAs(UnmanagedType.LPWStr)] out string id);

    [PreserveSig]
    int GetState(out uint state);
}

[ComImport]
[ComVisible(true)]
[Guid("886D8EEB-8CF2-4446-8D02-CDBA1DBDCF99")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
public interface IPropertyStore
{
    [PreserveSig]
    int GetCount(out uint count);

    [PreserveSig]
    int GetAt(uint index, out PropertyKey key);

    [PreserveSig]
    int GetValue(ref PropertyKey key, out PropVariant value);
}

[ComImport]
[ComVisible(true)]
[Guid("7991EEC9-7E89-4D85-8390-6C703CEC60C0")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
public interface IMMNotificationClient
{
    void OnDeviceStateChanged([MarshalAs(UnmanagedType.LPWStr)] string deviceId, uint newState);

    void OnDeviceAdded([MarshalAs(UnmanagedType.LPWStr)] string deviceId);

    void OnDeviceRemoved([MarshalAs(UnmanagedType.LPWStr)] string deviceId);

    void OnDefaultDeviceChanged(DataFlow flow, Role role, [MarshalAs(UnmanagedType.LPWStr)] string? defaultDeviceId);

    void OnPropertyValueChanged([MarshalAs(UnmanagedType.LPWStr)] string deviceId, PropertyKey key);
}

[ComImport]
[ComVisible(true)]
[Guid("F8679F50-850A-41CF-9C72-430F290290C8")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
public interface IPolicyConfig
{
    // Only SetDefaultEndpoint is called; the slots before it keep the vtable layout right.
    [PreserveSig] int GetMixFormat();
    [PreserveSig] int GetDeviceFormat();
    [PreserveSig] int ResetDeviceFormat();
    [PreserveSig] int SetDeviceFormat();
    [PreserveSig] int GetProcessingPeriod();
    [PreserveSig] int SetProcessingPeriod();
    [PreserveSig] int GetShareMode();
    [PreserveSig] int SetShareMode();
    [PreserveSig] int GetPropertyValue();
    [PreserveSig] int SetPropertyValue();

    [PreserveSig]
    int SetDefaultEndpoint([MarshalAs(UnmanagedType.LPWStr)] string deviceId, Role role);
}

[ComVisible(true)]
public sealed class NotificationClient(Action changed) : IMMNotificationClient
{
    public void OnDeviceStateChanged(string deviceId, uint newState) => changed();

    public void OnDeviceAdded(string deviceId) => changed();

    public void OnDeviceRemoved(string deviceId) => changed();

    public void OnDefaultDeviceChanged(DataFlow flow, Role role, string? defaultDeviceId) => changed();

    public void OnPropertyValueChanged(string deviceId, PropertyKey key)
    {
        // Fires very often (volume, levels); only a name change matters to the widget.
        if (key.FormatId == WindowsCoreAudioBackend.FriendlyNameKey.FormatId && key.PropertyId == WindowsCoreAudioBackend.FriendlyNameKey.PropertyId)
        {
            changed();
        }
    }
}

[ComImport]
[ComVisible(true)]
[Guid("C02216F6-8C67-4B5B-9D00-D008E73E0064")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
public interface IAudioMeterInformation
{
    // Only the first vtable slot is used; the rest of the interface is never called.
    [PreserveSig]
    int GetPeakValue(out float peak);
}
