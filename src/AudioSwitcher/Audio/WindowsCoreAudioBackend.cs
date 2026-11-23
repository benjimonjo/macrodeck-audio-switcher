using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using AudioSwitcher.Audio.Interop;

namespace AudioSwitcher.Audio;

/// <summary>
/// Windows backend using the Core Audio API (MMDevice) to list devices and watch for changes, and the
/// undocumented-but-stable IPolicyConfig interface (used by every Windows audio switcher since Windows 7)
/// to set the default endpoint. Windows moves apps that follow the default by itself.
/// </summary>
[SupportedOSPlatform("windows")]
internal sealed class WindowsCoreAudioBackend : IAudioBackend
{
    private static readonly Guid MmDeviceEnumeratorClsid = new("BCDE0395-E52F-467C-8E3D-C4579291692E");
    private static readonly Guid PolicyConfigClsid = new("870AF99C-171D-4F9E-AF0D-E63DF40C2BC9");
    internal static readonly PropertyKey FriendlyNameKey = new(new Guid("A45C254E-DF1C-4EFD-8020-67D146A850E0"), 14);

    private const uint DeviceStateActive = 0x1;
    private const ushort VtLpwstr = 31;
    private const int ElementNotFound = unchecked((int)0x80070490);

    public TimeSpan PollInterval => TimeSpan.FromSeconds(30);

    public Task<AudioSnapshot> QueryAsync(CancellationToken cancellationToken) => Task.Run(() =>
    {
        var enumerator = CreateEnumerator();
        try
        {
            return new AudioSnapshot(
                ListDevices(enumerator, DataFlow.Render),
                ListDevices(enumerator, DataFlow.Capture),
                DefaultId(enumerator, DataFlow.Render),
                DefaultId(enumerator, DataFlow.Capture),
                null);
        }
        finally
        {
            Marshal.ReleaseComObject(enumerator);
        }
    }, cancellationToken);

    public Task SetDefaultAsync(AudioDirection direction, string deviceId, bool moveStreams, CancellationToken cancellationToken)
        => Task.Run(() =>
        {
            var type = Type.GetTypeFromCLSID(PolicyConfigClsid)
                ?? throw new AudioBackendException("This version of Windows has no audio policy service.");
            var policy = (IPolicyConfig)(Activator.CreateInstance(type)
                ?? throw new AudioBackendException("Could not reach the Windows audio policy service."));
            try
            {
                // Set all three roles, as the Sound control panel does, so every kind of app follows.
                foreach (var role in new[] { Role.Console, Role.Multimedia, Role.Communications })
                {
                    var hr = policy.SetDefaultEndpoint(deviceId, role);
                    if (hr < 0)
                    {
                        throw new AudioBackendException(
                            $"Windows refused to switch the default device (0x{hr:X8}).", Marshal.GetExceptionForHR(hr));
                    }
                }
            }
            finally
            {
                Marshal.ReleaseComObject(policy);
            }
        }, cancellationToken);

    public async Task WatchAsync(Action changed, CancellationToken cancellationToken)
    {
        var enumerator = CreateEnumerator();
        var client = new NotificationClient(changed);
        try
        {
            Check(enumerator.RegisterEndpointNotificationCallback(client), "register for device changes");
            try
            {
                await Task.Delay(Timeout.Infinite, cancellationToken).ConfigureAwait(false);
            }
            finally
            {
                enumerator.UnregisterEndpointNotificationCallback(client);
            }
        }
        finally
        {
            Marshal.ReleaseComObject(enumerator);
        }
    }

    /// <summary>
    /// Polls the endpoint's own peak meter. Windows only measures a capture device while some app is
    /// recording from it, so an idle microphone stays at zero.
    /// </summary>
    public IDisposable? StartMeter(AudioDirection direction, AudioDevice device, Action<float> peak)
    {
        var stop = new CancellationTokenSource();
        var thread = new Thread(() => MeterLoop(device.Id, peak, stop.Token))
        {
            IsBackground = true,
            Name = "Audio level meter",
        };
        thread.SetApartmentState(ApartmentState.MTA);
        thread.Start();
        return new MeterHandle(stop);
    }

    private static void MeterLoop(string deviceId, Action<float> peak, CancellationToken cancellationToken)
    {
        IMMDeviceEnumerator? enumerator = null;
        IMMDevice? device = null;
        object? meterObject = null;
        try
        {
            enumerator = CreateEnumerator();
            if (enumerator.GetDevice(deviceId, out device) < 0)
            {
                return;
            }

            var iid = typeof(IAudioMeterInformation).GUID;
            if (device.Activate(ref iid, 23 /* CLSCTX_ALL */, IntPtr.Zero, out meterObject) < 0)
            {
                return;
            }

            var meter = (IAudioMeterInformation)meterObject;
            while (!cancellationToken.WaitHandle.WaitOne(50))
            {
                if (meter.GetPeakValue(out var value) >= 0)
                {
                    peak(value);
                }
            }
        }
        catch (Exception ex) when (ex is COMException or InvalidCastException or AudioBackendException)
        {
            // Device unplugged or no meter available: the bar just stays empty.
        }
        finally
        {
            foreach (var comObject in new[] { meterObject, device, enumerator })
            {
                if (comObject is not null && Marshal.IsComObject(comObject))
                {
                    Marshal.ReleaseComObject(comObject);
                }
            }
        }
    }

    private sealed class MeterHandle(CancellationTokenSource stop) : IDisposable
    {
        public void Dispose() => stop.Cancel();
    }

    private static IMMDeviceEnumerator CreateEnumerator()
    {
        var type = Type.GetTypeFromCLSID(MmDeviceEnumeratorClsid)
            ?? throw new AudioBackendException("The Windows audio device service is not available.");
        try
        {
            return (IMMDeviceEnumerator)(Activator.CreateInstance(type)
                ?? throw new AudioBackendException("Could not reach the Windows audio device service."));
        }
        catch (COMException ex)
        {
            throw new AudioBackendException("Could not reach the Windows audio device service: " + ex.Message, ex);
        }
    }

    private static List<AudioDevice> ListDevices(IMMDeviceEnumerator enumerator, DataFlow flow)
    {
        Check(enumerator.EnumAudioEndpoints(flow, DeviceStateActive, out var collection), "list audio devices");
        var devices = new List<AudioDevice>();
        try
        {
            Check(collection.GetCount(out var count), "count audio devices");
            for (uint i = 0; i < count; i++)
            {
                if (collection.Item(i, out var device) < 0)
                {
                    continue;
                }

                try
                {
                    if (device.GetId(out var id) < 0 || string.IsNullOrEmpty(id))
                    {
                        continue;
                    }

                    devices.Add(new AudioDevice(id, FriendlyName(device) ?? id));
                }
                finally
                {
                    Marshal.ReleaseComObject(device);
                }
            }
        }
        finally
        {
            Marshal.ReleaseComObject(collection);
        }

        return devices.OrderBy(d => d.Name, StringComparer.CurrentCultureIgnoreCase).ToList();
    }

    private static string? DefaultId(IMMDeviceEnumerator enumerator, DataFlow flow)
    {
        var hr = enumerator.GetDefaultAudioEndpoint(flow, Role.Multimedia, out var device);
        if (hr == ElementNotFound || hr < 0)
        {
            return null; // no device of this kind is connected
        }

        try
        {
            return device.GetId(out var id) >= 0 ? id : null;
        }
        finally
        {
            Marshal.ReleaseComObject(device);
        }
    }

    private static string? FriendlyName(IMMDevice device)
    {
        if (device.OpenPropertyStore(0 /* STGM_READ */, out var store) < 0)
        {
            return null;
        }

        try
        {
            var key = FriendlyNameKey;
            if (store.GetValue(ref key, out var value) < 0)
            {
                return null;
            }

            try
            {
                return value.Type == VtLpwstr ? Marshal.PtrToStringUni(value.Pointer) : null;
            }
            finally
            {
                PropVariantClear(ref value);
            }
        }
        finally
        {
            Marshal.ReleaseComObject(store);
        }
    }

    private static void Check(int hr, string what)
    {
        if (hr < 0)
        {
            throw new AudioBackendException($"Windows could not {what} (0x{hr:X8}).", Marshal.GetExceptionForHR(hr));
        }
    }

    [DllImport("ole32.dll")]
    private static extern int PropVariantClear(ref PropVariant value);
}
