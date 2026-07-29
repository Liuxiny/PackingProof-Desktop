using System;
using System.IO;
using System.Runtime.InteropServices;

namespace ExpressPackingMonitoring.Config
{
    /// <summary>
    /// Provides capacity information for local volumes and UNC network shares.
    /// DriveInfo rejects UNC roots, while GetDiskFreeSpaceEx supports both forms.
    /// </summary>
    internal readonly record struct StorageVolumeInfo(
        string RootPath,
        long TotalSize,
        long AvailableFreeSpace)
    {
        public static bool TryGet(string path, out StorageVolumeInfo volume)
        {
            volume = default;
            if (string.IsNullOrWhiteSpace(path)) return false;

            try
            {
                string fullPath = Path.GetFullPath(path);
                string? root = Path.GetPathRoot(fullPath);
                if (string.IsNullOrWhiteSpace(root)) return false;

                string queryPath = Path.EndsInDirectorySeparator(fullPath)
                    ? fullPath
                    : fullPath + Path.DirectorySeparatorChar;
                if (!GetDiskFreeSpaceEx(
                        queryPath,
                        out ulong availableBytes,
                        out ulong totalBytes,
                        out _))
                {
                    return false;
                }

                volume = new StorageVolumeInfo(
                    root,
                    ClampToInt64(totalBytes),
                    ClampToInt64(availableBytes));
                return true;
            }
            catch
            {
                return false;
            }
        }

        private static long ClampToInt64(ulong value) =>
            value > long.MaxValue ? long.MaxValue : (long)value;

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool GetDiskFreeSpaceEx(
            string directoryName,
            out ulong freeBytesAvailableToCaller,
            out ulong totalNumberOfBytes,
            out ulong totalNumberOfFreeBytes);
    }
}
