using System;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using ArcGISProMCP.Contracts;

namespace ArcGISProMCP.AddIn.Services;

internal static class ProWindowCapture
{
    [StructLayout(LayoutKind.Sequential)] private struct Rect { public int Left, Top, Right, Bottom; }
    [DllImport("user32.dll")] private static extern bool GetWindowRect(IntPtr window, out Rect rect);
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr window, out uint process);
    [DllImport("user32.dll")] private static extern IntPtr GetDC(IntPtr window);
    [DllImport("user32.dll")] private static extern int ReleaseDC(IntPtr window, IntPtr dc);
    [DllImport("user32.dll")] private static extern bool PrintWindow(IntPtr window, IntPtr dc, uint flags);
    [DllImport("gdi32.dll")] private static extern IntPtr CreateCompatibleDC(IntPtr dc);
    [DllImport("gdi32.dll")] private static extern IntPtr CreateCompatibleBitmap(IntPtr dc, int width, int height);
    [DllImport("gdi32.dll")] private static extern IntPtr SelectObject(IntPtr dc, IntPtr obj);
    [DllImport("gdi32.dll")] private static extern bool DeleteObject(IntPtr obj);
    [DllImport("gdi32.dll")] private static extern bool DeleteDC(IntPtr dc);
    internal static BitmapSource Capture(Window window)
    {
        var handle = new WindowInteropHelper(window).Handle; GetWindowThreadProcessId(handle, out uint owner);
        if (owner != Environment.ProcessId || !GetWindowRect(handle, out var rect) || window.WindowState == WindowState.Minimized)
            throw new BridgeException("ui_capture_unavailable", "只能截图当前Pro主窗口，且窗口不能最小化。");
        int width = rect.Right - rect.Left, height = rect.Bottom - rect.Top;
        if (width <= 0 || height <= 0 || (long)width * height > 8388608) throw new BridgeException("ui_capture_limit", "窗口截图最多8百万像素，请缩小Pro窗口。");
        IntPtr dc = GetDC(handle), memory = IntPtr.Zero, bitmap = IntPtr.Zero, old = IntPtr.Zero;
        try
        {
            memory = CreateCompatibleDC(dc); bitmap = CreateCompatibleBitmap(dc, width, height);
            if (dc == IntPtr.Zero || memory == IntPtr.Zero || bitmap == IntPtr.Zero) throw new BridgeException("ui_capture_unavailable", "无法分配窗口截图资源。");
            old = SelectObject(memory, bitmap);
            if (!PrintWindow(handle, memory, 2)) throw new BridgeException("ui_capture_unavailable", "Pro未提供窗口图像，未用地图导出冒充界面截图。");
            var image = Imaging.CreateBitmapSourceFromHBitmap(bitmap, IntPtr.Zero, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions()); image.Freeze();
            return image;
        }
        finally
        {
            if (old != IntPtr.Zero) SelectObject(memory, old);
            if (bitmap != IntPtr.Zero) DeleteObject(bitmap);
            if (memory != IntPtr.Zero) DeleteDC(memory);
            if (dc != IntPtr.Zero) ReleaseDC(handle, dc);
        }
    }
}
