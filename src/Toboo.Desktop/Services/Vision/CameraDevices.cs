using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;

namespace Toboo.Desktop.Services;

public sealed record CameraDevice(int Index, string Name)
{
    public override string ToString() => $"{Index} · {Name}";
}

public static class CameraDevices
{
    // DirectShow device order corresponds to OpenCV CAP_DSHOW indexes.
    public static IReadOnlyList<CameraDevice> List()
    {
        if (!OperatingSystem.IsWindows()) return [];
        var devices = new List<CameraDevice>();
        var type = Type.GetTypeFromCLSID(new("62BE5D10-60EB-11D0-BD3B-00A0C911CE86"), throwOnError: true)!;
        var enumerator = (ICreateDevEnum)Activator.CreateInstance(type)!;
        IEnumMoniker? monikers = null;
        try
        {
            var category = new Guid("860BB310-5D01-11D0-BD3B-00A0C911CE86");
            if (enumerator.CreateClassEnumerator(ref category, out monikers, 0) != 0 || monikers is null) return devices;
            var items = new IMoniker[1];
            while (monikers.Next(1, items, IntPtr.Zero) == 0)
            {
                object? storage = null;
                try
                {
                    var bagId = new Guid("55272A00-42CB-11CE-8135-00AA004BB851");
                    items[0].BindToStorage(null!, null!, ref bagId, out storage);
                    var bag = (IPropertyBag)storage;
                    var name = bag.Read("FriendlyName", out var value, IntPtr.Zero) == 0
                        ? value?.ToString() : null;
                    devices.Add(new(devices.Count, name ?? $"Camera {devices.Count}"));
                }
                finally
                {
                    if (storage is not null) Marshal.ReleaseComObject(storage);
                    Marshal.ReleaseComObject(items[0]);
                }
            }
        }
        finally
        {
            if (monikers is not null) Marshal.ReleaseComObject(monikers);
            Marshal.ReleaseComObject(enumerator);
        }
        return devices;
    }

    [ComImport, Guid("29840822-5B84-11D0-BD3B-00A0C911CE86"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface ICreateDevEnum
    {
        [PreserveSig] int CreateClassEnumerator(ref Guid category, out IEnumMoniker enumerator, int flags);
    }

    [ComImport, Guid("55272A00-42CB-11CE-8135-00AA004BB851"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IPropertyBag
    {
        [PreserveSig] int Read([MarshalAs(UnmanagedType.LPWStr)] string name,
            [MarshalAs(UnmanagedType.Struct)] out object value, IntPtr errorLog);
        [PreserveSig] int Write([MarshalAs(UnmanagedType.LPWStr)] string name,
            [MarshalAs(UnmanagedType.Struct)] ref object value);
    }
}
