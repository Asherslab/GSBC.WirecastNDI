using System.Runtime.InteropServices;

namespace GSBC.WirecastNDI;

/// <summary>
/// The app is built as WinExe so the background task never shows a window. For the CLI
/// sub-commands we attach to the console we were started from so their output is visible.
/// (Piping, e.g. "GSBC.WirecastNDI.exe list-devices | more", also works and waits for completion.)
/// </summary>
internal static partial class ConsoleHelper
{
    private const int AttachParentProcess = -1;
    private const int StdOutputHandle = -11;

    public static void AttachToParentConsole()
    {
        if (!OperatingSystem.IsWindows())
            return;

        // Output already goes somewhere (pipe, file, or we have a console): leave it alone.
        IntPtr stdout = GetStdHandle(StdOutputHandle);
        if (stdout != IntPtr.Zero && stdout != new IntPtr(-1))
            return;

        if (AttachConsole(AttachParentProcess))
        {
            Console.SetOut(new StreamWriter(Console.OpenStandardOutput()) { AutoFlush = true });
            Console.SetError(new StreamWriter(Console.OpenStandardError()) { AutoFlush = true });
            Console.WriteLine();
        }
    }

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool AttachConsole(int processId);

    [LibraryImport("kernel32.dll")]
    private static partial IntPtr GetStdHandle(int stdHandle);
}
