using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;
using SAM.App.Services;

namespace SAM.App.Infrastructure;

/// <summary>
/// Lets the app (a Windows app, so it starts without a console) print to the console it was
/// started from, or to wherever its output is redirected.
/// </summary>
public static class ConsoleOutput
{
    private static bool? _available;

    /// <summary>
    /// True if output reaches somewhere: stdout is redirected (a pipe or file), or the app was
    /// started from a console window and has attached to it. Connects on first use.
    /// </summary>
    public static bool IsAvailable => _available ??= Connect();

    /// <summary>Writes a line to stdout. No-op without a console.</summary>
    public static void WriteLine(string text)
    {
        if (IsAvailable)
        {
            Console.Out.WriteLine(text);
            Console.Out.Flush();
        }
    }

    /// <summary>Writes a line to stderr. No-op without a console.</summary>
    public static void WriteError(string text)
    {
        if (IsAvailable)
        {
            Console.Error.WriteLine(text);
            Console.Error.Flush();
        }
    }

    private static bool Connect()
    {
        try
        {
            if (IsValid(GetStdHandle(StdOutputHandle)))
            {
                // Redirected by the caller (e.g. "SAM.exe ... > result.txt" or a pipe).
                return true;
            }

            if (AttachConsole(AttachParentProcess) == false)
            {
                return false;
            }

            var encoding = GetConsoleEncoding();
            Console.SetOut(OpenConsoleWriter(encoding));
            Console.SetError(IsValid(GetStdHandle(StdErrorHandle)) ? Console.Error : OpenConsoleWriter(encoding));

            // The shell has already printed its prompt (it doesn't wait for Windows apps), so
            // start on a fresh line.
            Console.Out.WriteLine();
            return true;
        }
        catch (Exception e)
        {
            Diagnostics.Log("Couldn't attach to the console", e);
            return false;
        }
    }

    private static bool IsValid(IntPtr handle) =>
        handle != IntPtr.Zero && handle != new IntPtr(-1) && GetFileType(handle) != FileTypeUnknown;

    private static Encoding GetConsoleEncoding()
    {
        try
        {
            var codePage = (int)GetConsoleOutputCP();
            Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
            return codePage == 65001 ? new UTF8Encoding(false) : Encoding.GetEncoding(codePage);
        }
        catch (Exception e) when (e is ArgumentException or NotSupportedException)
        {
            return new UTF8Encoding(false);
        }
    }

    private static TextWriter OpenConsoleWriter(Encoding encoding)
    {
        var handle = new SafeFileHandle(
            CreateFile("CONOUT$", GenericRead | GenericWrite, FileShareRead | FileShareWrite, IntPtr.Zero, OpenExisting, 0, IntPtr.Zero),
            ownsHandle: true);
        if (handle.IsInvalid)
        {
            throw new IOException($"Couldn't open the console (error {Marshal.GetLastWin32Error()}).");
        }

        return new StreamWriter(new FileStream(handle, FileAccess.Write), encoding) { AutoFlush = true };
    }

    private const int AttachParentProcess = -1;
    private const int StdOutputHandle = -11;
    private const int StdErrorHandle = -12;
    private const uint FileTypeUnknown = 0;
    private const uint GenericRead = 0x80000000;
    private const uint GenericWrite = 0x40000000;
    private const uint FileShareRead = 0x1;
    private const uint FileShareWrite = 0x2;
    private const uint OpenExisting = 3;

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool AttachConsole(int processId);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr GetStdHandle(int handle);

    [DllImport("kernel32.dll")]
    private static extern uint GetFileType(IntPtr handle);

    [DllImport("kernel32.dll")]
    private static extern uint GetConsoleOutputCP();

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true, EntryPoint = "CreateFileW")]
    private static extern IntPtr CreateFile(
        string fileName, uint access, uint share, IntPtr security, uint creation, uint flags, IntPtr template);
}
