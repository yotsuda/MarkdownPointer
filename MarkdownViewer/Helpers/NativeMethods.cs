using System.Runtime.InteropServices;

namespace MarkdownViewer.Helpers
{
    /// <summary>
    /// P/Invoke declarations for native Windows API calls.
    /// </summary>
    internal static class NativeMethods
    {
        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool GetCursorPos(out POINT lpPoint);

        [StructLayout(LayoutKind.Sequential)]
        public struct POINT
        {
            public int X;
            public int Y;
        }

        // Foreground window APIs
        [DllImport("user32.dll")]
        public static extern IntPtr GetForegroundWindow();

        [DllImport("user32.dll")]
        public static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool AttachThreadInput(uint idAttach, uint idAttachTo, bool fAttach);

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool SetForegroundWindow(IntPtr hWnd);

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool BringWindowToTop(IntPtr hWnd);

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

        [DllImport("kernel32.dll")]
        public static extern uint GetCurrentThreadId();

        public const int SW_RESTORE = 9;
        public const int SW_SHOW = 5;

        /// <summary>
        /// Force a window to the foreground, even from another process.
        /// </summary>
        public static void ForceForegroundWindow(IntPtr hWnd)
        {
            var foregroundWnd = GetForegroundWindow();
            var currentThreadId = GetCurrentThreadId();
            GetWindowThreadProcessId(foregroundWnd, out _);
            var foregroundThreadId = GetWindowThreadProcessId(foregroundWnd, out _);

            // Attach to the foreground thread to allow SetForegroundWindow
            if (currentThreadId != foregroundThreadId)
            {
                AttachThreadInput(currentThreadId, foregroundThreadId, true);
            }

            try
            {
                BringWindowToTop(hWnd);
                ShowWindow(hWnd, SW_SHOW);
                SetForegroundWindow(hWnd);
            }
            finally
            {
                if (currentThreadId != foregroundThreadId)
                {
                    AttachThreadInput(currentThreadId, foregroundThreadId, false);
                }
            }
        }
    }
}
