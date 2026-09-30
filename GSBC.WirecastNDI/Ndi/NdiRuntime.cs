using System.Runtime.InteropServices;
using System.Runtime.Loader;
using NewTek;

namespace GSBC.WirecastNDI.Ndi;

/// <summary>
/// Locates and initialises the native NDI library.
/// The NDILibDotNetCoreBase wrapper only probes for the library by file name, which misses the
/// NDI Runtime install folder on Windows (C:\Program Files\NDI\NDI 6 Runtime\v6) and the SDK
/// folder on macOS. We hook the last-chance unmanaged resolver and point it at the real file.
/// </summary>
public static class NdiRuntime
{
    private static readonly Lock Gate = new();
    private static bool _initialised;
    private static string? _loadedFrom;

    public static string? LoadedFrom => _loadedFrom;

    public static void Initialise(string? configuredPath)
    {
        lock (Gate)
        {
            if (_initialised)
                return;

            AssemblyLoadContext.Default.ResolvingUnmanagedDll += (assembly, name) =>
            {
                if (assembly != typeof(NDIlib).Assembly)
                    return IntPtr.Zero;

                foreach (string candidate in Candidates(configuredPath))
                {
                    if (File.Exists(candidate) && NativeLibrary.TryLoad(candidate, out IntPtr handle))
                    {
                        _loadedFrom = candidate;
                        return handle;
                    }
                }

                return IntPtr.Zero;
            };

            bool ok;
            try
            {
                ok = NDIlib.initialize();
            }
            catch (DllNotFoundException ex)
            {
                throw new InvalidOperationException(
                    "NDI runtime not found. Install the NDI Runtime (or NDI Tools) on this machine, or set " +
                    "WirecastNdi:Ndi:LibraryPath. Searched: " + string.Join("; ", Candidates(configuredPath)), ex);
            }

            if (!ok)
                throw new InvalidOperationException("NDIlib_initialize failed - this CPU is not supported by NDI.");

            _loadedFrom ??= "(default search path)";
            _initialised = true;
        }
    }

    public static string Version => Marshal.PtrToStringUTF8(NDIlib.version()) ?? "unknown";

    private static IEnumerable<string> Candidates(string? configuredPath)
    {
        if (!string.IsNullOrWhiteSpace(configuredPath))
            yield return configuredPath;

        string fileName = OperatingSystem.IsWindows() ? "Processing.NDI.Lib.x64.dll"
            : OperatingSystem.IsMacOS() ? "libndi.dylib"
            : "libndi.so.6";

        yield return Path.Combine(AppContext.BaseDirectory, fileName);

        // Set by the NDI Runtime / NDI Tools / SDK installers.
        foreach (string env in new[] { "NDI_RUNTIME_DIR_V6", "NDI_RUNTIME_DIR_V5" })
        {
            string? dir = Environment.GetEnvironmentVariable(env);
            if (!string.IsNullOrWhiteSpace(dir))
                yield return Path.Combine(dir, fileName);
        }

        if (OperatingSystem.IsWindows())
        {
            string pf = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
            yield return Path.Combine(pf, "NDI", "NDI 6 Runtime", "v6", fileName);
            yield return Path.Combine(pf, "NDI", "NDI 6 Tools", "Runtime", fileName);
            yield return Path.Combine(pf, "NDI", "NDI 5 Runtime", "v5", fileName);
        }
        else if (OperatingSystem.IsMacOS())
        {
            yield return "/Library/NDI SDK for Apple/lib/macOS/libndi.dylib";
            yield return "/usr/local/lib/libndi.dylib";
            yield return "/opt/homebrew/lib/libndi.dylib";
        }
        else
        {
            yield return "/usr/lib/libndi.so.6";
            yield return "/usr/local/lib/libndi.so.6";
        }
    }
}
