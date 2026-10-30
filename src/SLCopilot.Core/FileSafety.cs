using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace SLCopilot.Core;

internal static class FileSafety
{
    public static void RejectHardLinks(SafeFileHandle handle)
    {
        if (!Compat.IsWindows) throw new WorkspaceException("Workspace file access currently requires Windows.");
        if (!GetFileInformationByHandle(handle, out var info)) throw new WorkspaceException("Cannot verify file identity.");
        if (info.NumberOfLinks != 1) throw new WorkspaceException("Hard-linked files are not supported.");
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetFileInformationByHandle(SafeFileHandle handle, out FileInformation information);

    [StructLayout(LayoutKind.Sequential)]
    private struct FileInformation
    {
        public uint Attributes, CreationLow, CreationHigh, AccessLow, AccessHigh, WriteLow, WriteHigh;
        public uint VolumeSerial, SizeHigh, SizeLow, NumberOfLinks, IndexHigh, IndexLow;
    }
}
