using System.Runtime.InteropServices;
using HaloPixelToolBox.Models;

namespace HaloPixelToolBox.Services.Audio;

public sealed record AudioOutputChangeResult(bool Success, string Message, IReadOnlyList<string> FailedRoles);

/// <summary>Enumerates playback endpoints; changing the Windows default requires an explicit call.</summary>
public sealed class WindowsAudioEndpointService
{
    private const uint DeviceStateActive = 1;
    private const uint PropertyStoreRead = 0;
    private static readonly Guid EnumeratorClassId = new("BCDE0395-E52F-467C-8E3D-C4579291692E");
    private static readonly Guid PolicyConfigClassId = new("294935CE-F637-4E7C-A41B-AB255460B862");
    private static readonly PropertyKey FriendlyNameKey = new(new Guid("A45C254E-DF1C-4EFD-8020-67D146A850E0"), 14);
    private static readonly SemaphoreSlim OutputChangeGate = new(1, 1);

    public Task<IReadOnlyList<AudioEndpointInfo>> GetEndpointsAsync(CancellationToken cancellationToken = default)
        => Task.Run(() => EnumerateEndpoints(cancellationToken), cancellationToken);

    public async Task<AudioOutputChangeResult> SetDefaultEndpointAsync(string endpointId, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(endpointId))
            return new(false, "请先选择可用的播放设备。", []);

        await OutputChangeGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            // Once the first role is changed, finish all three roles and report the actual result.
            // Cancellation at that point could otherwise hide a partially changed system output.
            return await Task.Run(() => ChangeDefaultEndpoint(endpointId, cancellationToken), CancellationToken.None).ConfigureAwait(false);
        }
        finally
        {
            OutputChangeGate.Release();
        }
    }

    private static IReadOnlyList<AudioEndpointInfo> EnumerateEndpoints(CancellationToken cancellationToken)
    {
        if (!OperatingSystem.IsWindows())
            throw new PlatformNotSupportedException("播放设备管理仅支持 Windows。");

        IMMDeviceEnumerator? enumerator = null;
        IMMDeviceCollection? collection = null;
        try
        {
            enumerator = CreateComObject<IMMDeviceEnumerator>(EnumeratorClassId);
            var defaultId = GetDefaultId(enumerator, AudioRole.Multimedia);
            var communicationsId = GetDefaultId(enumerator, AudioRole.Communications);
            Marshal.ThrowExceptionForHR(enumerator.EnumAudioEndpoints(AudioDataFlow.Render, DeviceStateActive, out collection));
            Marshal.ThrowExceptionForHR(collection.GetCount(out var count));
            var endpoints = new List<AudioEndpointInfo>();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            for (uint index = 0; index < count; index++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                IMMDevice? device = null;
                try
                {
                    // Devices can disappear while the enumeration is in progress.
                    if (collection.Item(index, out device) < 0 || device.GetState(out var state) < 0 || (state & DeviceStateActive) == 0)
                        continue;
                    if (device.GetId(out var id) < 0 || string.IsNullOrWhiteSpace(id) || !seen.Add(id))
                        continue;
                    endpoints.Add(new(id, GetFriendlyName(device, id),
                        string.Equals(id, defaultId, StringComparison.OrdinalIgnoreCase),
                        string.Equals(id, communicationsId, StringComparison.OrdinalIgnoreCase)));
                }
                finally
                {
                    ReleaseComObject(device);
                }
            }

            return endpoints.OrderByDescending(endpoint => endpoint.IsDefault)
                .ThenBy(endpoint => endpoint.Name, StringComparer.CurrentCultureIgnoreCase)
                .ThenBy(endpoint => endpoint.Id, StringComparer.OrdinalIgnoreCase).ToArray();
        }
        finally
        {
            ReleaseComObject(collection);
            ReleaseComObject(enumerator);
        }
    }

    private static AudioOutputChangeResult ChangeDefaultEndpoint(string endpointId, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!OperatingSystem.IsWindows())
            return new(false, "播放设备管理仅支持 Windows。", []);

        IMMDeviceEnumerator? enumerator = null;
        IMMDevice? device = null;
        IPolicyConfigVista? policy = null;
        try
        {
            enumerator = CreateComObject<IMMDeviceEnumerator>(EnumeratorClassId);
            var endpoint = EnumerateEndpoints(cancellationToken)
                .FirstOrDefault(item => string.Equals(item.Id, endpointId, StringComparison.OrdinalIgnoreCase));
            if (endpoint is null || enumerator.GetDevice(endpointId, out device) < 0 || device.GetState(out var state) < 0 || (state & DeviceStateActive) == 0)
                return new(false, "所选播放设备已断开或不可用，请刷新设备列表。", []);

            policy = CreateComObject<IPolicyConfigVista>(PolicyConfigClassId);
            cancellationToken.ThrowIfCancellationRequested();
            var failed = new List<string>();
            foreach (var role in new[] { AudioRole.Console, AudioRole.Multimedia, AudioRole.Communications })
            {
                var status = policy.SetDefaultEndpoint(endpoint.Id, role);
                var actualId = GetDefaultId(enumerator, role);
                if (status < 0 || !string.Equals(actualId, endpoint.Id, StringComparison.OrdinalIgnoreCase))
                    failed.Add(RoleName(role));
            }

            return failed.Count == 0
                ? new(true, $"已将“{endpoint.Name}”设为 Windows 默认播放和通信设备。", [])
                : new(false, $"输出切换未完全完成（{string.Join("、", failed)}）。请刷新查看当前输出，或在 Windows 声音设置中完成切换。", failed);
        }
        catch (Exception exception) when (exception is COMException or InvalidCastException or UnauthorizedAccessException)
        {
            return new(false, $"Windows 未能切换默认输出：{exception.Message}。可在 Windows 声音设置中手动切换。", []);
        }
        finally
        {
            ReleaseComObject(policy);
            ReleaseComObject(device);
            ReleaseComObject(enumerator);
        }
    }

    private static string? GetDefaultId(IMMDeviceEnumerator enumerator, AudioRole role)
    {
        IMMDevice? device = null;
        try
        {
            return enumerator.GetDefaultAudioEndpoint(AudioDataFlow.Render, role, out device) >= 0
                && device.GetId(out var id) >= 0 ? id : null;
        }
        finally
        {
            ReleaseComObject(device);
        }
    }

    private static string GetFriendlyName(IMMDevice device, string fallback)
    {
        IPropertyStore? store = null;
        var value = new PropVariant();
        try
        {
            var key = FriendlyNameKey;
            if (device.OpenPropertyStore(PropertyStoreRead, out store) < 0 || store.GetValue(ref key, out value) < 0)
                return fallback;
            var name = value.VariantType switch
            {
                31 => Marshal.PtrToStringUni(value.PointerValue), // VT_LPWSTR
                8 => Marshal.PtrToStringBSTR(value.PointerValue), // VT_BSTR
                _ => null
            };
            return string.IsNullOrWhiteSpace(name) ? fallback : name;
        }
        finally
        {
            PropVariantClear(ref value);
            ReleaseComObject(store);
        }
    }

    private static T CreateComObject<T>(Guid classId) where T : class
    {
        var type = Type.GetTypeFromCLSID(classId, throwOnError: true)!;
        var instance = Activator.CreateInstance(type)!;
        try
        {
            return (T)instance;
        }
        catch
        {
            ReleaseComObject(instance);
            throw;
        }
    }

    private static void ReleaseComObject(object? instance)
    {
        if (instance is not null && Marshal.IsComObject(instance))
            Marshal.ReleaseComObject(instance);
    }

    private static string RoleName(AudioRole role) => role switch
    {
        AudioRole.Console => "系统声音",
        AudioRole.Multimedia => "媒体播放",
        _ => "语音通信"
    };

    private enum AudioDataFlow { Render, Capture, All }
    private enum AudioRole { Console, Multimedia, Communications }

    [StructLayout(LayoutKind.Sequential)]
    private struct PropertyKey(Guid formatId, uint propertyId)
    {
        public Guid FormatId = formatId;
        public uint PropertyId = propertyId;
    }

    [StructLayout(LayoutKind.Explicit, Size = 24)]
    private struct PropVariant
    {
        [FieldOffset(0)] public ushort VariantType;
        [FieldOffset(8)] public IntPtr PointerValue;
    }

    [DllImport("ole32.dll")]
    private static extern int PropVariantClear(ref PropVariant value);

    [ComImport, Guid("A95664D2-9614-4F35-A746-DE8DB63617E6"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IMMDeviceEnumerator
    {
        [PreserveSig] int EnumAudioEndpoints(AudioDataFlow flow, uint stateMask, out IMMDeviceCollection devices);
        [PreserveSig] int GetDefaultAudioEndpoint(AudioDataFlow flow, AudioRole role, out IMMDevice device);
        [PreserveSig] int GetDevice([MarshalAs(UnmanagedType.LPWStr)] string id, out IMMDevice device);
        [PreserveSig] int RegisterEndpointNotificationCallback(IntPtr callback);
        [PreserveSig] int UnregisterEndpointNotificationCallback(IntPtr callback);
    }

    [ComImport, Guid("0BD7A1BE-7A1A-44DB-8397-CC5392387B5E"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IMMDeviceCollection
    {
        [PreserveSig] int GetCount(out uint count);
        [PreserveSig] int Item(uint index, out IMMDevice device);
    }

    [ComImport, Guid("D666063F-1587-4E43-81F1-B948E807363F"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IMMDevice
    {
        [PreserveSig] int Activate(ref Guid interfaceId, uint context, IntPtr activationParameters, [MarshalAs(UnmanagedType.IUnknown)] out object instance);
        [PreserveSig] int OpenPropertyStore(uint access, out IPropertyStore properties);
        [PreserveSig] int GetId([MarshalAs(UnmanagedType.LPWStr)] out string id);
        [PreserveSig] int GetState(out uint state);
    }

    [ComImport, Guid("886D8EEB-8CF2-4446-8D02-CDBA1DBDCF99"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IPropertyStore
    {
        [PreserveSig] int GetCount(out uint count);
        [PreserveSig] int GetAt(uint index, out PropertyKey key);
        [PreserveSig] int GetValue(ref PropertyKey key, out PropVariant value);
        [PreserveSig] int SetValue(ref PropertyKey key, ref PropVariant value);
        [PreserveSig] int Commit();
    }

    // The complete Vista interface preserves the native vtable order. The default-device
    // setter is private Windows functionality, so unsupported systems receive a clear result.
    [ComImport, Guid("568B9108-44BF-40B4-9006-86AFE5B5A620"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IPolicyConfigVista
    {
        [PreserveSig] int GetMixFormat([MarshalAs(UnmanagedType.LPWStr)] string id, out IntPtr format);
        [PreserveSig] int GetDeviceFormat([MarshalAs(UnmanagedType.LPWStr)] string id, int useDefault, out IntPtr format);
        [PreserveSig] int SetDeviceFormat([MarshalAs(UnmanagedType.LPWStr)] string id, IntPtr endpointFormat, IntPtr mixFormat);
        [PreserveSig] int GetProcessingPeriod([MarshalAs(UnmanagedType.LPWStr)] string id, int useDefault, out long defaultPeriod, out long minimumPeriod);
        [PreserveSig] int SetProcessingPeriod([MarshalAs(UnmanagedType.LPWStr)] string id, ref long period);
        [PreserveSig] int GetShareMode([MarshalAs(UnmanagedType.LPWStr)] string id, IntPtr mode);
        [PreserveSig] int SetShareMode([MarshalAs(UnmanagedType.LPWStr)] string id, IntPtr mode);
        [PreserveSig] int GetPropertyValue([MarshalAs(UnmanagedType.LPWStr)] string id, ref PropertyKey key, out PropVariant value);
        [PreserveSig] int SetPropertyValue([MarshalAs(UnmanagedType.LPWStr)] string id, ref PropertyKey key, ref PropVariant value);
        [PreserveSig] int SetDefaultEndpoint([MarshalAs(UnmanagedType.LPWStr)] string id, AudioRole role);
        [PreserveSig] int SetEndpointVisibility([MarshalAs(UnmanagedType.LPWStr)] string id, int visible);
    }
}
