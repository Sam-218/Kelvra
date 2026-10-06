using System.Runtime.InteropServices;
using System.Security;
using System.Windows.Media.Imaging;
using Microsoft.Win32;

namespace Kelvra;

/// <summary>
/// Windows 10/11 toast notifications (Action Center, focus assist, "Kelvra" as the sender) without a NuGet package
/// or a Windows-specific target framework: the few WinRT calls needed are made directly through combase.dll.
/// An unpackaged app identifies itself with an AppUserModelID registered under HKCU. If anything here fails,
/// <see cref="TryShow"/> returns false and the caller falls back to the tray balloon.
/// </summary>
public static class Toasts
{
    public const string AppId = "Kelvra.HardwareMonitor";

    private static bool _registered;
    private static bool _unavailable;

    /// <summary>Shows a toast. False if toasts don't work here (the caller then uses the tray balloon).</summary>
    public static bool TryShow(string title, string message)
    {
        if (_unavailable) return false;
        try
        {
            if (!_registered)
            {
                Register();
                _registered = true;
            }
            Show(BuildXml(title, message));
            return true;
        }
        catch (Exception ex) when (ex is COMException or InvalidCastException or EntryPointNotFoundException or DllNotFoundException
                                       or UnauthorizedAccessException or IOException or SecurityException or ArgumentException)
        {
            _unavailable = true; // don't retry on every alert
            Log.Warn("Toast notifications unavailable, using tray balloons instead: " + ex.Message);
            return false;
        }
    }

    internal static string BuildXml(string title, string message) =>
        "<toast><visual><binding template=\"ToastGeneric\">" +
        $"<text>{SecurityElement.Escape(title)}</text><text>{SecurityElement.Escape(message)}</text>" +
        "</binding></visual><audio silent=\"true\" /></toast>"; // Kelvra plays its own alert sound (Settings)

    /// <summary>Name and icon Windows shows on the toast (HKCU, so no admin rights are involved).</summary>
    private static void Register()
    {
        string icon = Path.Combine(AppSettings.Dir, "kelvra-toast.png");
        if (!File.Exists(icon))
        {
            Directory.CreateDirectory(AppSettings.Dir);
            var decoder = BitmapDecoder.Create(new Uri("pack://application:,,,/Assets/app.ico"), BitmapCreateOptions.None, BitmapCacheOption.OnLoad);
            var frame = decoder.Frames.OrderByDescending(f => f.PixelWidth).First();
            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(frame));
            using var file = File.Create(icon);
            encoder.Save(file);
        }
        using var key = Registry.CurrentUser.CreateSubKey(@"Software\Classes\AppUserModelId\" + AppId);
        key.SetValue("DisplayName", "Kelvra");
        key.SetValue("IconUri", icon);
    }

    // ---------- WinRT by hand ----------

    private static readonly Guid IidXmlDocument = new("F7F3A506-1E87-42D6-BCFB-B8C809FA5494");
    private static readonly Guid IidXmlDocumentIO = new("6CD0E74E-EE65-4489-9EBF-CA43E87BA637");
    private static readonly Guid IidToastFactory = new("04124B20-82C6-4229-B109-FD9ED4662B53");
    private static readonly Guid IidToastManagerStatics = new("50AC103F-D235-4598-BBEF-98FE4D1A3AD4");

    // Interface methods start after IUnknown (3) and IInspectable (3) at vtable slot 6
    private const int XmlDocumentIO_LoadXml = 6;
    private const int ToastFactory_CreateToastNotification = 6;
    private const int ToastManager_CreateToastNotifierWithId = 7;
    private const int ToastNotifier_Show = 6;

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int InPtr(IntPtr self, IntPtr arg);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int InPtrOutPtr(IntPtr self, IntPtr arg, out IntPtr result);

    private static void Show(string xml)
    {
        IntPtr doc = 0, io = 0, xmlDoc = 0, factory = 0, toast = 0, manager = 0, notifier = 0;
        try
        {
            doc = Activate("Windows.Data.Xml.Dom.XmlDocument");
            Check(Marshal.QueryInterface(doc, in IidXmlDocumentIO, out io));
            using (var text = new HString(xml))
                Check(Method<InPtr>(io, XmlDocumentIO_LoadXml)(io, text.Handle));
            Check(Marshal.QueryInterface(doc, in IidXmlDocument, out xmlDoc));

            factory = Factory("Windows.UI.Notifications.ToastNotification", IidToastFactory);
            Check(Method<InPtrOutPtr>(factory, ToastFactory_CreateToastNotification)(factory, xmlDoc, out toast));

            manager = Factory("Windows.UI.Notifications.ToastNotificationManager", IidToastManagerStatics);
            using (var id = new HString(AppId))
                Check(Method<InPtrOutPtr>(manager, ToastManager_CreateToastNotifierWithId)(manager, id.Handle, out notifier));
            Check(Method<InPtr>(notifier, ToastNotifier_Show)(notifier, toast));
        }
        finally
        {
            foreach (var p in new[] { notifier, manager, toast, factory, xmlDoc, io, doc })
                if (p != 0) Marshal.Release(p);
        }
    }

    private static T Method<T>(IntPtr obj, int slot) where T : Delegate =>
        Marshal.GetDelegateForFunctionPointer<T>(Marshal.ReadIntPtr(Marshal.ReadIntPtr(obj), slot * IntPtr.Size));

    private static IntPtr Activate(string runtimeClass)
    {
        using var name = new HString(runtimeClass);
        Check(RoActivateInstance(name.Handle, out var instance));
        return instance;
    }

    private static IntPtr Factory(string runtimeClass, Guid iid)
    {
        using var name = new HString(runtimeClass);
        Check(RoGetActivationFactory(name.Handle, in iid, out var factory));
        return factory;
    }

    private static void Check(int hr)
    {
        if (hr < 0) Marshal.ThrowExceptionForHR(hr);
    }

    private sealed class HString : IDisposable
    {
        public HString(string value) => Check(WindowsCreateString(value, value.Length, out _handle));
        private readonly IntPtr _handle;
        public IntPtr Handle => _handle;
        public void Dispose() => WindowsDeleteString(_handle);
    }

    [DllImport("combase.dll", CharSet = CharSet.Unicode)]
    private static extern int WindowsCreateString(string source, int length, out IntPtr hstring);

    [DllImport("combase.dll")]
    private static extern int WindowsDeleteString(IntPtr hstring);

    [DllImport("combase.dll")]
    private static extern int RoActivateInstance(IntPtr classId, out IntPtr instance);

    [DllImport("combase.dll")]
    private static extern int RoGetActivationFactory(IntPtr classId, in Guid iid, out IntPtr factory);
}
