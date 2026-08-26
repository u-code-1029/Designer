using System;
using System.Runtime.InteropServices;

namespace DrillFlow.Desktop.Services;

/// <summary>
/// Uses the Windows Shell file-open dialog in folder-picking mode. Unlike the
/// legacy WinForms folder browser, this provides the Explorer-style picker on
/// Windows 7 while keeping the application on .NET Framework 4.8.
/// </summary>
internal static class ShellFolderPicker
{
    private const int CancelledHResult = unchecked((int)0x800704C7);

    private static readonly Guid FileOpenDialogClassId =
        new("DC1C5A9C-E88A-4DDE-A5A1-60F82A20AEF7");

    private static readonly Guid ShellItemInterfaceId =
        new("43826D1E-E718-42EE-BC55-A1E261C37BFE");

    public static string? Show(IntPtr ownerHandle, string title, string initialFolder)
    {
        IFileDialog? dialog = null;
        IShellItem? initialShellItem = null;
        IShellItem? selectedShellItem = null;

        try
        {
            var dialogType = Type.GetTypeFromCLSID(FileOpenDialogClassId, throwOnError: true);
            dialog = (IFileDialog)Activator.CreateInstance(dialogType!)!;

            dialog.GetOptions(out var existingOptions);
            dialog.SetOptions(
                existingOptions |
                FileOpenOptions.PickFolders |
                FileOpenOptions.ForceFileSystem |
                FileOpenOptions.PathMustExist |
                FileOpenOptions.NoChangeDirectory);
            dialog.SetTitle(title);

            if (TryCreateShellItem(initialFolder, out initialShellItem))
            {
                try
                {
                    dialog.SetFolder(initialShellItem!);
                }
                catch (COMException)
                {
                    // A saved local/UNC location may have disappeared since the last run.
                    // The picker itself is still useful, so fall back to the Shell default.
                    ReleaseComObject(initialShellItem);
                    initialShellItem = null;
                }
            }

            var showResult = dialog.Show(ownerHandle);
            if (showResult == CancelledHResult)
            {
                return null;
            }

            Marshal.ThrowExceptionForHR(showResult);

            dialog.GetResult(out selectedShellItem);
            return GetFileSystemPath(selectedShellItem);
        }
        finally
        {
            ReleaseComObject(selectedShellItem);
            ReleaseComObject(initialShellItem);
            ReleaseComObject(dialog);
        }
    }

    private static bool TryCreateShellItem(string initialFolder, out IShellItem? shellItem)
    {
        shellItem = null;
        if (string.IsNullOrWhiteSpace(initialFolder))
        {
            return false;
        }

        // Do not probe Directory.Exists here: a disconnected UNC path can block the WPF
        // dispatcher before the dialog becomes visible. Let the Shell parse the location
        // once and fall back to its normal start folder when it cannot be resolved.
        var shellItemId = ShellItemInterfaceId;
        var result = SHCreateItemFromParsingName(
            initialFolder,
            IntPtr.Zero,
            ref shellItemId,
            out shellItem);

        if (result < 0)
        {
            shellItem = null;
            return false;
        }

        return true;
    }

    private static string GetFileSystemPath(IShellItem shellItem)
    {
        IntPtr pathPointer = IntPtr.Zero;
        try
        {
            var result = shellItem.GetDisplayName(DisplayName.FileSystemPath, out pathPointer);
            Marshal.ThrowExceptionForHR(result);
            return Marshal.PtrToStringUni(pathPointer) ?? string.Empty;
        }
        finally
        {
            if (pathPointer != IntPtr.Zero)
            {
                Marshal.FreeCoTaskMem(pathPointer);
            }
        }
    }

    private static void ReleaseComObject(object? value)
    {
        if (value is not null && Marshal.IsComObject(value))
        {
            Marshal.ReleaseComObject(value);
        }
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, PreserveSig = true)]
    private static extern int SHCreateItemFromParsingName(
        [MarshalAs(UnmanagedType.LPWStr)] string path,
        IntPtr bindingContext,
        ref Guid shellItemId,
        [MarshalAs(UnmanagedType.Interface)] out IShellItem? shellItem);

    [Flags]
    private enum FileOpenOptions : uint
    {
        NoChangeDirectory = 0x00000008,
        PickFolders = 0x00000020,
        ForceFileSystem = 0x00000040,
        PathMustExist = 0x00000800
    }

    private enum DisplayName : uint
    {
        FileSystemPath = 0x80058000
    }

    [ComImport]
    [Guid("42F85136-DB7E-439C-85F1-E4075D135FC8")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IFileDialog
    {
        [PreserveSig]
        int Show(IntPtr parent);

        void SetFileTypes(uint fileTypeCount, IntPtr filterSpecifications);

        void SetFileTypeIndex(uint fileTypeIndex);

        void GetFileTypeIndex(out uint fileTypeIndex);

        void Advise(IntPtr events, out uint cookie);

        void Unadvise(uint cookie);

        void SetOptions(FileOpenOptions options);

        void GetOptions(out FileOpenOptions options);

        void SetDefaultFolder(IShellItem shellItem);

        void SetFolder(IShellItem shellItem);

        void GetFolder(out IShellItem shellItem);

        void GetCurrentSelection(out IShellItem shellItem);

        void SetFileName([MarshalAs(UnmanagedType.LPWStr)] string name);

        void GetFileName([MarshalAs(UnmanagedType.LPWStr)] out string name);

        void SetTitle([MarshalAs(UnmanagedType.LPWStr)] string title);

        void SetOkButtonLabel([MarshalAs(UnmanagedType.LPWStr)] string text);

        void SetFileNameLabel([MarshalAs(UnmanagedType.LPWStr)] string label);

        void GetResult(out IShellItem shellItem);

        void AddPlace(IShellItem shellItem, uint fileDialogAddPlaceLocation);

        void SetDefaultExtension([MarshalAs(UnmanagedType.LPWStr)] string defaultExtension);

        void Close(int result);

        void SetClientGuid(ref Guid clientGuid);

        void ClearClientData();

        void SetFilter(IntPtr filter);
    }

    [ComImport]
    [Guid("43826D1E-E718-42EE-BC55-A1E261C37BFE")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IShellItem
    {
        void BindToHandler(
            IntPtr bindingContext,
            ref Guid bindingHandlerId,
            ref Guid interfaceId,
            out IntPtr result);

        void GetParent(out IShellItem parent);

        [PreserveSig]
        int GetDisplayName(DisplayName displayName, out IntPtr name);

        void GetAttributes(uint requestedAttributes, out uint attributes);

        void Compare(IShellItem shellItem, uint hint, out int order);
    }
}
