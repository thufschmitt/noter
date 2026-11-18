using System;
using System.Runtime.InteropServices;

namespace Longhand.PenDemo;

/// <summary>Win32's pointer input (Windows 8 and later): the raw stylus API, with every sample the digitizer took.</summary>
static class Native
{
    public const int WM_POINTERUPDATE = 0x0245, WM_POINTERDOWN = 0x0246, WM_POINTERUP = 0x0247, WM_POINTERCAPTURECHANGED = 0x024C;

    public const uint POINTER_FLAG_INCONTACT = 0x4, POINTER_FLAG_FIRSTBUTTON = 0x10, POINTER_FLAG_SECONDBUTTON = 0x20, POINTER_FLAG_CANCELED = 0x8000;
    public const uint PEN_FLAG_BARREL = 0x1, PEN_FLAG_INVERTED = 0x2, PEN_FLAG_ERASER = 0x4;
    public const uint PEN_MASK_PRESSURE = 0x1;

    public enum PointerType : uint { Pointer = 1, Touch = 2, Pen = 3, Mouse = 4, Touchpad = 5 }

    [StructLayout(LayoutKind.Sequential)]
    public struct POINT { public int X, Y; }

    [StructLayout(LayoutKind.Sequential)]
    public struct RECT { public int Left, Top, Right, Bottom; }

    [StructLayout(LayoutKind.Sequential)]
    public struct POINTER_INFO
    {
        public PointerType pointerType;
        public uint pointerId;
        public uint frameId;
        public uint pointerFlags;
        public IntPtr sourceDevice;
        public IntPtr hwndTarget;
        public POINT ptPixelLocation;
        public POINT ptHimetricLocation;
        public POINT ptPixelLocationRaw;
        public POINT ptHimetricLocationRaw;
        public uint dwTime;
        public uint historyCount;
        public int InputData;
        public uint dwKeyStates;
        public ulong PerformanceCount;
        public int ButtonChangeType;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct POINTER_PEN_INFO
    {
        public POINTER_INFO pointerInfo;
        public uint penFlags;
        public uint penMask;
        public uint pressure; // 0 to 1024
        public uint rotation;
        public int tiltX;
        public int tiltY;
    }

    public static uint PointerId(IntPtr wParam) => (uint)((long)wParam & 0xFFFF);

    [DllImport("user32.dll", SetLastError = true)]
    public static extern bool EnableMouseInPointer(bool enable);

    [DllImport("user32.dll", SetLastError = true)]
    public static extern bool GetPointerType(uint pointerId, out PointerType type);

    [DllImport("user32.dll", SetLastError = true)]
    public static extern bool GetPointerInfo(uint pointerId, out POINTER_INFO info);

    [DllImport("user32.dll", SetLastError = true)]
    public static extern bool GetPointerPenInfo(uint pointerId, out POINTER_PEN_INFO info);

    [DllImport("user32.dll", SetLastError = true)]
    public static extern bool GetPointerInfoHistory(uint pointerId, ref uint entriesCount, [Out] POINTER_INFO[] info);

    [DllImport("user32.dll", SetLastError = true)]
    public static extern bool GetPointerPenInfoHistory(uint pointerId, ref uint entriesCount, [Out] POINTER_PEN_INFO[] info);

    [DllImport("user32.dll", SetLastError = true)]
    public static extern bool GetPointerDeviceRects(IntPtr device, out RECT pointerDeviceRect, out RECT displayRect);
}
