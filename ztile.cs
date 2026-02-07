#!/usr/bin/dotnet run

#:package TerraFX.Interop.Xlib@6.4.0.2
#:package System.CommandLine@2.0.2
#:package Tomlyn.Signed@0.20.0

#:property Version=0.0.1
#:property AllowUnsafeBlocks=true

/* Software to make my desktop less fucked
 * 
 * things i want to add probably:
 *  - zone spanning: when a key is held or when a window is resized, it should
 *    slot into adjacent zones as well, so that it can span across several
 *  - window class filter config: some windows should not be tracked
 *  - keybind thing: maybe i want windows to float unless i hold a key when dropping one into a zone
 *  - draw zones: when moving a window, we should probably draw the zones as transparent rectangles onto the root window
 *    or using some kind of overlay window (manually redirect input... import cairo... wahhh)
 * 
*/

using System.CommandLine;
using System.Diagnostics;
using System.Runtime.InteropServices.Marshalling;
using System.Text;
using TerraFX.Interop.Xlib;
using Tomlyn;
using Tomlyn.Model;

const long ReleaseThresholdMs = 50;

bool shouldRun = true;

var cmd = new RootCommand("Utility for X11 that allows windows to slot into pre-configured zones")
{
};

cmd.SetAction(c =>
{
    RunLoop();
});

Console.CancelKeyPress += (o, e) =>
{
    shouldRun = false;
};

return cmd.Parse(args).Invoke();

unsafe void RunLoop()
{
    Xlib.XInitThreads();

    Stopwatch clock = new();
    clock.Start();

    var configPath = $"{Environment.GetEnvironmentVariable("HOME")}/.config/ztile.toml";
    Console.WriteLine("Reading config from {0}", configPath);

    var model = Toml.ToModel(File.ReadAllText(configPath));
    var config = new Config()
    {
        Zones = [.. (model["zones"] as TomlArray ?? []).Select(d => Zone.Parse(d!.ToString()!))],
        Padding = (int)(model["padding"] == null ? 0 : (long)model["padding"]),
    };

    var display = Xlib.XOpenDisplay(null);
    var root = Xlib.DefaultRootWindow(display);
    Console.WriteLine("Running on display {0}", UmHelp.AsString(display->display_name));

    Window pointerRootWindow = default, pointerChildWindow = default;

    Xlib.XSelectInput(display, root, Xlib.SubstructureNotifyMask);

    using SemaphoreSlim trackerLock = new(1);
    Dictionary<nint, TrackedWindow> trackedWindows = [];
    Queue<nint> released = [];

    ThreadPool.QueueUserWorkItem(_ => { EventLoop(); }, null);

    // TODO THIS LOOP MIGHT CUT OUT. HANDLE EXCEPTIONS PL0X
    while (shouldRun)
    {
        int mouseX, mouseY;
        int mouseWinX, mouseWinY;
        uint pointerMask;

        Xlib.XQueryPointer(
            display, root,
            &pointerRootWindow,
            &pointerChildWindow,
            &mouseX,
            &mouseY,
            &mouseWinX,
            &mouseWinY,
            &pointerMask);

        // no mouse buttons held?
        if ((pointerMask & (Xlib.Button1Mask | Xlib.Button2Mask | Xlib.Button3Mask)) == 0)
            try
            {
                trackerLock.Wait();

                foreach (var tracked in trackedWindows)
                    if (clock.ElapsedMilliseconds - tracked.Value.LastUpdateTime > ReleaseThresholdMs)
                        released.Enqueue(tracked.Key);

                while (released.TryDequeue(out var n))
                {
                    if (!trackedWindows.Remove(n, out var tracked))
                        continue;

                    Console.WriteLine("Dropped {0}", n);

                    Window myRoot;
                    int myX, myY;
                    uint myW, myH, myDepth, myBorder;
                    Xlib.XGetGeometry(display, new Drawable(tracked.Window), &myRoot, &myX, &myY, &myW, &myH, &myBorder, &myDepth);

                    // is the mouse even still in this window?
                    if (mouseX > myX && mouseX < myX + myW)
                        if (mouseY > myY && mouseY < myY + myH)
                            foreach (var zone in config.Zones)
                            {
                                if (zone.IsPointInside(mouseX, mouseY))
                                {
                                    Console.WriteLine("Dropped in zone at {0}, {1}", zone.X, zone.Y);


                                    MapWindowTo(tracked.Window, zone);

                                    // var offset = GetDecorationalOffset(tracked.Window);
                                    // Xlib.XMoveWindow(display, tracked.Window, zone.X + config.Padding + offset.x, zone.Y + config.Padding + offset.y);
                                    // Xlib.XResizeWindow(display, tracked.Window, (uint)(zone.W + offset.w - config.Padding * 2), (uint)(zone.H + offset.h - config.Padding * 2));

                                    break;
                                }
                            }
                }
            }
            finally
            {
                trackerLock.Release();
            }
    }

    void EventLoop()
    {
        // TODO THIS LOOP MIGHT CUT OUT TOO. HANDLE EXCEPTIONS PLEEEEEEEEASE
        while (shouldRun)
        {
            XEvent ev = default;
            Xlib.XNextEvent(display, &ev);
            trackerLock.Wait();
            try
            {
                if (ev.type == Xlib.ConfigureNotify)
                {
                    var n = ev.xconfigure;

                    if (IsWindowTrackable(n.window))
                        if (trackedWindows.TryGetValue(n.window, out var tracked))
                            tracked.LastUpdateTime = clock.ElapsedMilliseconds;
                        else
                        {
                            Console.WriteLine("Picked up {0}", n.window);
                            trackedWindows.Add(n.window, new TrackedWindow
                            {
                                LastUpdateTime = clock.ElapsedMilliseconds,
                                Window = n.window
                            });
                        }
                }
            }
            finally
            {
                trackerLock.Release();
            }
        }
    }

    Xlib.XCloseDisplay(display);

    bool IsWindowTrackable(Window window)
    {
        // TODO this is definitely inadequate

        if (window.Value == null)
            return false;

        try
        {
            XWindowAttributes attributes = default;
            if (Xlib.XGetWindowAttributes(display, window, &attributes) == 1)
            {
                if (attributes.override_redirect == 1)
                    return false;
                if (attributes.map_state != Xlib.IsViewable)
                    return false;

                return true;
            }
        }
        catch
        {
            return false;
        }

        return false;
    }

    unsafe void MapWindowTo(Window window, Zone zone)
    {
        if (window.Value == null)
            return;

        if (zone.W * zone.H < 1)
            return;

        Atom atomExtents = default, actualType = default;
        int actualFormat = 0;
        nuint numItems = 0, bytesAfter = 0;
        long* data = null;
        fixed (byte* atomName = Encoding.ASCII.GetBytes("_NET_FRAME_EXTENTS\0"))
            atomExtents = Xlib.XInternAtom(display, (sbyte*)atomName, 0);

        int left = 2, right = 2, top = 21, bottom = 2; // <- set to default values of MY personal fucking shit

        Xlib.XSync(display, 0); // <- does nothing

        // ALWAYS RETURNS FAIL, DAtA NOT POPULATED. FUCK MY LIFE
        // WAAAAAAAAAAAAWD)U* HAW*O&HAWJd 09AP*Jdada
        var status = Xlib.XGetWindowProperty(display, window, atomExtents,
            0, 4, 0, (Atom)Xlib.AnyPropertyType,
            &actualType, &actualFormat, &numItems, &bytesAfter, (byte**)&data);

        if (numItems == 4 && data != null)
        {
            left = (int)data[0];
            right = (int)data[1];
            top = (int)data[2];
            bottom = (int)data[3];
            _ = Xlib.XFree(data);
        }

        XEvent ev;
        var mask = Xlib.SubstructureNotifyMask | Xlib.SubstructureRedirectMask;

        ev.xclient.type = Xlib.ClientMessage;
        ev.xclient.serial = 0;
        ev.xclient.send_event = 1;
        ev.xclient.display = display;
        ev.xclient.window = window;
        fixed (byte* atomName = Encoding.ASCII.GetBytes("_NET_MOVERESIZE_WINDOW\0"))
            ev.xclient.message_type = Xlib.XInternAtom(display, (sbyte*)atomName, 0);
        ev.xclient.format = 32; // <- no idea what this means :3

        ev.xclient.data.l = new()
        {
            // https://github.com/jbenden/i3-gaps-rounded/blob/17fd233b7846b80ae805ddceb254a7190edc25d6/src/handlers.c#L651
            // got the flags from here because the docs are profoundly confusing to me
            e0 = 10 | (1 << 8) | (1 << 9) | (1 << 10) | (1 << 11),
            e1 = zone.X + config.Padding + left,
            e2 = zone.Y + config.Padding + top,
            e3 = (int)zone.W - 2 * config.Padding - left - right,
            e4 = (int)zone.H - 2 * config.Padding - top - bottom
        };
        Xlib.XSendEvent(display, root, 0, mask, &ev);
    }

}

public class TrackedWindow
{
    public Window Window;
    public long LastUpdateTime;
}

public class Config
{
    public Zone[] Zones = [];
    public int Padding = 0;
    // TODO should probably add some other stuff here
}

public struct Zone
{
    public int X, Y;
    public uint W, H;

    public readonly bool IsPointInside(int x, int y)
    {
        return x >= X && x <= X + W && y >= Y && y < y + H;
    }

    public unsafe static Zone Parse(string t)
    {
        var data = (sbyte*)AnsiStringMarshaller.ConvertToUnmanaged(t);
        try
        {
            var zone = new Zone();
            Xlib.XParseGeometry(data, &zone.X, &zone.Y, &zone.W, &zone.H);
            return zone;
        }
        finally
        {
            Xlib.XFree(data);
        }
        throw new Exception($"Invalid format: {t}");
    }
}

public unsafe static class UmHelp
{
    public static string? AsString(sbyte* unmanagedString)
    {
        return AnsiStringMarshaller.ConvertToManaged((byte*)unmanagedString);
    }
}