using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;

namespace Sharm;

internal static class NativeMemory
{
    [StructLayout(LayoutKind.Sequential)]
    private struct Counters
    {
        public uint Size, PageFaultCount;
        public nuint PeakWorkingSet, WorkingSet, PeakPagedPool, PagedPool, PeakNonPagedPool, NonPagedPool;
        public nuint Pagefile, PeakPagefile, PrivateUsage, PrivateWorkingSet;
        public ulong SharedCommit;
    }

    [DllImport("psapi.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetProcessMemoryInfo(IntPtr process, ref Counters counters, uint size);

    internal static long PrivateResidentBytes(int processId)
    {
        using var process = Process.GetProcessById(processId);
        var data = new Counters { Size = (uint)Marshal.SizeOf<Counters>() };
        return GetProcessMemoryInfo(process.Handle, ref data, data.Size)
            ? (long)data.PrivateWorkingSet : process.WorkingSet64;
    }
}

public partial class MainWindow
{
    [DllImport("user32.dll")] private static extern bool ReleaseCapture();
    [DllImport("user32.dll")] private static extern IntPtr SendMessage(IntPtr hwnd, uint message, IntPtr wParam, IntPtr lParam);

    internal static bool IsDraggableTitleSource(DependencyObject? source)
    {
        while (source is not null)
        {
            if (source is ButtonBase or ListBoxItem or ScrollBar or TextBoxBase) return false;
            if (source is FrameworkElement { Name: "TitleBar" }) return true;
            source = source is Visual or System.Windows.Media.Media3D.Visual3D
                ? VisualTreeHelper.GetParent(source) : LogicalTreeHelper.GetParent(source);
        }
        return false;
    }

    private void TitleBar_MouseDown(object sender, MouseButtonEventArgs e)
    {
        if (!IsDraggableTitleSource(e.OriginalSource as DependencyObject)) return;
        e.Handled = true;
        if (e.ClickCount == 2) { Maximize_Click(this, new RoutedEventArgs()); return; }
        ReleaseCapture();
        // Let Windows own the move loop, restore-drag, monitor DPI, and snapping.
        var cursor = PointToScreen(e.GetPosition(this));
        var coordinates = ((int)cursor.X & 0xffff) | (((int)cursor.Y & 0xffff) << 16);
        SendMessage(new WindowInteropHelper(this).Handle, 0x00A1, new IntPtr(2), new IntPtr(coordinates));
    }

    private void TitleBar_RightClick(object sender, MouseButtonEventArgs e)
    {
        if (!IsDraggableTitleSource(e.OriginalSource as DependencyObject)) return;
        e.Handled = true;
        SystemCommands.ShowSystemMenu(this, PointToScreen(e.GetPosition(this)));
    }
}
