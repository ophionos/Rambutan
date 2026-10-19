#if WINDOWS
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.Marshalling;
using Microsoft.Win32;
using Rambutan.Core.Diagnostics;

namespace Rambutan.Integrations.Taskbar;

/// <summary>
/// The buttons under the main window's taskbar preview and the progress bar on
/// its taskbar button, through <c>ITaskbarList3</c>. The App forwards the
/// window's messages to <see cref="HandleMessage"/>.
/// </summary>
/// <remarks>
/// Windows takes a window's buttons once per taskbar button and never lets the
/// app add or remove them: only icon, tip and flags change after. A taskbar
/// button is made each time the window is shown after being hidden (to the
/// tray) and when Explorer restarts, and each time the shell sends
/// <c>TaskbarButtonCreated</c>, so the buttons are added again then.
/// Every member must be called on the window's UI thread (COM, STA).
/// </remarks>
public sealed partial class TaskbarThumbnailToolbar : IDisposable
{
    private const uint WM_COMMAND = 0x0111;
    private const int THBN_CLICKED = 0x1800;
    private const uint THB_ICON = 0x2, THB_TOOLTIP = 0x4, THB_FLAGS = 0x8;
    private const uint THBF_DISABLED = 0x1, THBF_HIDDEN = 0x8;
    private const int TBPF_NOPROGRESS = 0;
    private const int SM_CXSMICON = 49;
    private const int TipLength = 260;

    private static readonly Guid ClsidTaskbarList = new("56FDF344-FD6D-11d0-958A-006097C9A090");
    private static readonly Guid IidTaskbarList3 = new("ea1afb91-9e28-4b86-90e9-9e9f8a5eefaf");

    private readonly nint _hwnd;
    private readonly Func<string, string> _text;
    private readonly uint _buttonCreated;
    private readonly Dictionary<TaskbarGlyph, nint> _icons = [];
    private ITaskbarList3? _taskbar;
    private TaskbarButtonView[]? _buttons;
    private TaskbarProgress _progress;
    private bool _added;

    public event Action<TaskbarButton>? ButtonClicked;

    /// <param name="text">Gives the tip for a string key.</param>
    public TaskbarThumbnailToolbar(nint hwnd, Func<string, string> text)
    {
        _hwnd = hwnd;
        _text = text;
        _buttonCreated = RegisterWindowMessageW("TaskbarButtonCreated");
    }

    /// <summary>The side of a small icon, in pixels, at the system's scale:
    /// the size the toolbar draws its icons at.</summary>
    public static int IconSize => Math.Max(16, GetSystemMetrics(SM_CXSMICON));

    /// <summary>Whether the taskbar, and with it the preview, is light. The
    /// taskbar follows Windows' mode, not the apps' mode.</summary>
    public static bool UsesLightTheme()
    {
        using var key = Registry.CurrentUser.OpenSubKey(
            @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
        return key?.GetValue("SystemUsesLightTheme") is int light && light != 0;
    }

    /// <summary>Replaces every glyph's icon; the shown buttons take the new ones.</summary>
    public void SetIcons(IReadOnlyDictionary<TaskbarGlyph, IconPixels> icons)
    {
        var old = _icons.Values.ToArray();
        _icons.Clear();
        foreach (var (glyph, pixels) in icons)
        {
            var icon = CreateIcon(pixels);
            if (icon != 0)
                _icons[glyph] = icon;
        }
        Push();
        foreach (var icon in old)
            DestroyIcon(icon);
    }

    public void SetButtons(TaskbarButtonView[] buttons)
    {
        _buttons = buttons;
        Push();
    }

    public void SetProgress(TaskbarProgress progress)
    {
        _progress = progress;
        PushProgress();
    }

    /// <summary>Handles a message to the window. True for a button's click, which
    /// the window's own handling must not see.</summary>
    public bool HandleMessage(uint msg, nint wParam)
    {
        if (msg == _buttonCreated && _buttonCreated != 0)
        {
            // A new taskbar button, possibly in a new Explorer: start over.
            _added = false;
            _taskbar = null;
            Push();
            PushProgress();
            return false;
        }
        if (msg == WM_COMMAND && HighWord(wParam) == THBN_CLICKED)
        {
            var id = LowWord(wParam);
            if (Enum.IsDefined((TaskbarButton)id))
            {
                ButtonClicked?.Invoke((TaskbarButton)id);
                return true;
            }
        }
        return false;
    }

    private void Push()
    {
        if (_buttons is not { Length: > 0 } buttons || Taskbar() is not { } taskbar)
            return;
        var native = new ThumbButton[buttons.Length];
        for (var i = 0; i < buttons.Length; i++)
            native[i] = ToNative(buttons[i]);
        try
        {
            unsafe
            {
                fixed (ThumbButton* first = native)
                {
                    if (_added)
                    {
                        taskbar.ThumbBarUpdateButtons(_hwnd, (uint)native.Length, first);
                    }
                    else
                    {
                        taskbar.ThumbBarAddButtons(_hwnd, (uint)native.Length, first);
                        _added = true;
                    }
                }
            }
        }
        catch (Exception) when (!_added)
        {
            // Before the window's first showing there is no taskbar button yet;
            // TaskbarButtonCreated brings the buttons when there is.
        }
        catch (Exception ex)
        {
            AppLog.Write("taskbar", $"buttons not updated: {ex.Message}");
        }
    }

    private void PushProgress()
    {
        if (!_added || Taskbar() is not { } taskbar)
            return;
        try
        {
            if (_progress.IsNone)
                taskbar.SetProgressState(_hwnd, TBPF_NOPROGRESS);
            else
                taskbar.SetProgressValue(_hwnd, _progress.Completed, _progress.Total);
        }
        catch (Exception ex)
        {
            AppLog.Write("taskbar", $"progress not set: {ex.Message}");
        }
    }

    private ITaskbarList3? Taskbar()
    {
        if (_taskbar is not null)
            return _taskbar;
        try
        {
            var hr = CoCreateInstance(ClsidTaskbarList, 0, 1 /* CLSCTX_INPROC_SERVER */, IidTaskbarList3, out var unknown);
            Marshal.ThrowExceptionForHR(hr);
            try
            {
                var taskbar = (ITaskbarList3)ComWrappers.GetOrCreateObjectForComInstance(unknown, CreateObjectFlags.UniqueInstance);
                taskbar.HrInit();
                _taskbar = taskbar;
            }
            finally
            {
                Marshal.Release(unknown);
            }
        }
        catch (Exception ex)
        {
            AppLog.Write("taskbar", $"taskbar list unavailable: {ex.Message}");
        }
        return _taskbar;
    }

    private static readonly StrategyBasedComWrappers ComWrappers = new();

    private unsafe ThumbButton ToNative(TaskbarButtonView view)
    {
        var button = new ThumbButton
        {
            Mask = THB_ICON | THB_TOOLTIP | THB_FLAGS,
            Id = (uint)view.Button,
            Icon = _icons.GetValueOrDefault(view.Glyph),
            Flags = (view.IsEnabled ? 0 : THBF_DISABLED) | (view.IsHidden ? THBF_HIDDEN : 0),
        };
        var tip = _text(view.TipKey).AsSpan();
        if (tip.Length >= TipLength)
            tip = tip[..(TipLength - 1)];
        var destination = new Span<char>(button.Tip, TipLength);
        tip.CopyTo(destination);
        destination[tip.Length] = '\0';
        return button;
    }

    /// <summary>A 32-bit icon from premultiplied pixels; icons take straight alpha.</summary>
    private static unsafe nint CreateIcon(IconPixels pixels)
    {
        var size = pixels.Size;
        var header = new BitmapInfoHeader
        {
            Size = (uint)sizeof(BitmapInfoHeader),
            Width = size,
            Height = -size, // top-down, as the pixels are
            Planes = 1,
            BitCount = 32,
        };
        var color = CreateDIBSection(0, &header, 0 /* DIB_RGB_COLORS */, out var bits, 0, 0);
        if (color == 0)
            return 0;
        var mask = CreateBitmap(size, size, 1, 1, null);
        try
        {
            var target = new Span<byte>((void*)bits, size * size * 4);
            var source = pixels.Bgra.AsSpan(0, target.Length);
            for (var i = 0; i < source.Length; i += 4)
            {
                var alpha = source[i + 3];
                for (var c = 0; c < 3; c++)
                    target[i + c] = alpha == 0 ? (byte)0 : (byte)Math.Min(255, source[i + c] * 255 / alpha);
                target[i + 3] = alpha;
            }
            var info = new IconInfo { IsIcon = 1, Mask = mask, Color = color };
            return CreateIconIndirect(&info);
        }
        finally
        {
            DeleteObject(color);
            if (mask != 0)
                DeleteObject(mask);
        }
    }

    private static int LowWord(nint value) => (int)((long)value & 0xFFFF);

    private static int HighWord(nint value) => (int)(((long)value >> 16) & 0xFFFF);

    public void Dispose()
    {
        foreach (var icon in _icons.Values)
            DestroyIcon(icon);
        _icons.Clear();
        _taskbar = null;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal unsafe struct ThumbButton
    {
        public uint Mask;
        public uint Id;
        public uint Bitmap;
        public nint Icon;
        public fixed char Tip[TipLength];
        public uint Flags;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct BitmapInfoHeader
    {
        public uint Size;
        public int Width;
        public int Height;
        public ushort Planes;
        public ushort BitCount;
        public uint Compression;
        public uint SizeImage;
        public int XPelsPerMeter;
        public int YPelsPerMeter;
        public uint ClrUsed;
        public uint ClrImportant;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct IconInfo
    {
        public int IsIcon;
        public uint HotspotX;
        public uint HotspotY;
        public nint Mask;
        public nint Color;
    }

    // ITaskbarList, ITaskbarList2 and ITaskbarList3 as one flat vtable, in
    // shobjidl_core.h's order: the unused slots must be there too.
    [GeneratedComInterface]
    [Guid("ea1afb91-9e28-4b86-90e9-9e9f8a5eefaf")]
    internal unsafe partial interface ITaskbarList3
    {
        void HrInit();
        void AddTab(nint hwnd);
        void DeleteTab(nint hwnd);
        void ActivateTab(nint hwnd);
        void SetActiveAlt(nint hwnd);
        void MarkFullscreenWindow(nint hwnd, int fullscreen);
        void SetProgressValue(nint hwnd, ulong completed, ulong total);
        void SetProgressState(nint hwnd, int flags);
        void RegisterTab(nint hwndTab, nint hwndMdi);
        void UnregisterTab(nint hwndTab);
        void SetTabOrder(nint hwndTab, nint hwndInsertBefore);
        void SetTabActive(nint hwndTab, nint hwndMdi, uint reserved);
        void ThumbBarAddButtons(nint hwnd, uint count, ThumbButton* buttons);
        void ThumbBarUpdateButtons(nint hwnd, uint count, ThumbButton* buttons);
        void ThumbBarSetImageList(nint hwnd, nint imageList);
        void SetOverlayIcon(nint hwnd, nint icon, nint description);
        void SetThumbnailTooltip(nint hwnd, nint tip);
        void SetThumbnailClip(nint hwnd, nint clip);
    }

    [LibraryImport("ole32.dll")]
    private static partial int CoCreateInstance(in Guid clsid, nint outer, uint context, in Guid iid, out nint instance);

    [LibraryImport("user32.dll", StringMarshalling = StringMarshalling.Utf16)]
    private static partial uint RegisterWindowMessageW(string name);

    [LibraryImport("user32.dll")]
    private static partial int GetSystemMetrics(int index);

    [LibraryImport("gdi32.dll")]
    private static unsafe partial nint CreateDIBSection(nint dc, BitmapInfoHeader* info, uint usage, out nint bits, nint section, uint offset);

    [LibraryImport("gdi32.dll")]
    private static unsafe partial nint CreateBitmap(int width, int height, uint planes, uint bitCount, void* bits);

    [LibraryImport("gdi32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool DeleteObject(nint handle);

    [LibraryImport("user32.dll")]
    private static unsafe partial nint CreateIconIndirect(IconInfo* info);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool DestroyIcon(nint icon);
}
#endif
