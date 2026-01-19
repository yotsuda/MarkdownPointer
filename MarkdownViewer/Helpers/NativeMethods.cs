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
    }
}
