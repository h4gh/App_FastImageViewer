using System.IO;
using System.Runtime.InteropServices;

namespace FastImageViewer.Services;

public static class FileOperationsService
{
    private const int FO_DELETE = 0x0003;
    private const ushort FOF_SILENT = 0x0004;
    private const ushort FOF_NOCONFIRMATION = 0x0010;
    private const ushort FOF_ALLOWUNDO = 0x0040;
    private const ushort FOF_NOERRORUI = 0x0400;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct SHFILEOPSTRUCT
    {
        public nint hwnd;
        public uint wFunc;
        [MarshalAs(UnmanagedType.LPWStr)]
        public string pFrom;
        [MarshalAs(UnmanagedType.LPWStr)]
        public string? pTo;
        public ushort fFlags;
        [MarshalAs(UnmanagedType.Bool)]
        public bool fAnyOperationsAborted;
        public nint hNameMappings;
        [MarshalAs(UnmanagedType.LPWStr)]
        public string? lpszProgressTitle;
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern int SHFileOperation(ref SHFILEOPSTRUCT FileOp);

    [DllImport("shell32.dll")]
    private static extern void SHChangeNotify(uint wEventId, uint uFlags, nint dwItem1, nint dwItem2);

    /// <summary>
    /// Notifies Windows Explorer to refresh file association icons in its shell icon cache.
    /// </summary>
    public static void RefreshShellIconCache()
    {
        try
        {
            const uint SHCNE_ASSOCCHANGED = 0x08000000;
            const uint SHCNF_IDLIST = 0x0000;
            SHChangeNotify(SHCNE_ASSOCCHANGED, SHCNF_IDLIST, 0, 0);
        }
        catch
        {
            // Ignore shell notification errors
        }
    }

    /// <summary>
    /// Silently moves the specified file to the Windows Recycle Bin without confirmation dialogs.
    /// </summary>
    public static bool MoveToRecycleBin(string filePath)
    {
        if (string.IsNullOrWhiteSpace(filePath) || !File.Exists(filePath))
            return false;

        try
        {
            var shf = new SHFILEOPSTRUCT
            {
                wFunc = FO_DELETE,
                // pFrom must be double-null terminated
                pFrom = Path.GetFullPath(filePath) + "\0\0",
                fFlags = FOF_ALLOWUNDO | FOF_NOCONFIRMATION | FOF_SILENT | FOF_NOERRORUI
            };

            int result = SHFileOperation(ref shf);
            return result == 0 && !shf.fAnyOperationsAborted;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Copies the source file into the target directory. Creates the directory if it does not exist.
    /// </summary>
    public static string CopyFileToFolder(string sourceFilePath, string targetFolder, bool overwrite = true)
    {
        if (!File.Exists(sourceFilePath))
            throw new FileNotFoundException("Zdrojový soubor neexistuje.", sourceFilePath);

        string fullTargetFolder = Path.GetFullPath(targetFolder);
        if (!Directory.Exists(fullTargetFolder))
        {
            Directory.CreateDirectory(fullTargetFolder);
        }

        string fileName = Path.GetFileName(sourceFilePath);
        string destFilePath = Path.Combine(fullTargetFolder, fileName);

        if (string.Equals(Path.GetFullPath(sourceFilePath), destFilePath, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("Zdrojová a cílová cesta jsou shodné.");
        }

        File.Copy(sourceFilePath, destFilePath, overwrite);
        return destFilePath;
    }
}
