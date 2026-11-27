using System.Buffers.Binary;
using System.Runtime.InteropServices;

// The Explorer helper accepts a complete snapshot, keyed by actual taskbar HWNDs.
// A zero-entry snapshot removes all per-taskbar overrides.
internal readonly record struct MonitorTapEntry(
    IntPtr TaskbarHwnd, ulong StackRanges, uint TrayMask, uint Flags, uint KeepPinned);

internal static class MonitorTapProtocol
{
    private const uint WmCopyData = 0x004A;
    private const uint Magic = 0x57424D36; // WBM6
    private const uint SmtoBlock = 0x0001;
    private const uint SmtoAbortIfHung = 0x0002;
    private static readonly object Gate = new();
    private static IntPtr lastTapWindow;
    private static byte[]? lastPayload;

    [StructLayout(LayoutKind.Sequential)]
    private struct CopyDataStruct
    {
        public IntPtr DwData;
        public int CbData;
        public IntPtr LpData;
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr SendMessageTimeout(IntPtr hWnd, uint message, IntPtr wParam,
        ref CopyDataStruct data, uint flags, uint timeoutMilliseconds, out IntPtr result);

    // Returns true when the helper accepted the snapshot or already has this exact snapshot.
    internal static bool Send(IntPtr tapWindow, IReadOnlyList<MonitorTapEntry> entries)
    {
        ArgumentNullException.ThrowIfNull(entries);
        if (tapWindow == IntPtr.Zero || entries.Count > 8) return false;

        var bytes = new byte[8 + 32 * entries.Count];
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(0, 4), 1);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(4, 4), (uint)entries.Count);
        for (int i = 0; i < entries.Count; i++)
        {
            var entry = entries[i];
            if (entry.TaskbarHwnd == IntPtr.Zero || entry.TrayMask > 31 || entry.Flags > 31 || entry.KeepPinned > 10)
                return false;
            var span = bytes.AsSpan(8 + 32 * i, 32);
            BinaryPrimitives.WriteUInt64LittleEndian(span[..8], unchecked((ulong)entry.TaskbarHwnd.ToInt64()));
            BinaryPrimitives.WriteUInt64LittleEndian(span.Slice(8, 8), entry.StackRanges);
            BinaryPrimitives.WriteUInt32LittleEndian(span.Slice(16, 4), entry.TrayMask);
            BinaryPrimitives.WriteUInt32LittleEndian(span.Slice(20, 4), entry.Flags);
            BinaryPrimitives.WriteUInt32LittleEndian(span.Slice(24, 4), entry.KeepPinned);
            // Bytes 28..31 are reserved and remain zero.
        }

        lock (Gate)
        {
            if (lastTapWindow == tapWindow && lastPayload != null && lastPayload.AsSpan().SequenceEqual(bytes))
                return true;

            IntPtr nativeBytes = IntPtr.Zero;
            try
            {
                nativeBytes = Marshal.AllocHGlobal(bytes.Length);
                Marshal.Copy(bytes, 0, nativeBytes, bytes.Length);
                var data = new CopyDataStruct { DwData = (IntPtr)Magic, CbData = bytes.Length, LpData = nativeBytes };
                bool sent = SendMessageTimeout(tapWindow, WmCopyData, IntPtr.Zero, ref data,
                    SmtoBlock | SmtoAbortIfHung, 500, out var result) != IntPtr.Zero && result != IntPtr.Zero;
                if (sent)
                {
                    lastTapWindow = tapWindow;
                    lastPayload = bytes;
                }
                return sent;
            }
            finally
            {
                if (nativeBytes != IntPtr.Zero) Marshal.FreeHGlobal(nativeBytes);
            }
        }
    }

    // Call after the Explorer helper is replaced, even if its HWND is reused.
    internal static void Invalidate()
    {
        lock (Gate)
        {
            lastTapWindow = IntPtr.Zero;
            lastPayload = null;
        }
    }
}
