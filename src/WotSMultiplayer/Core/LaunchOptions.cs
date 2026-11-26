using System;
using System.Runtime.InteropServices;
using System.Text;
using UnityEngine;

namespace WotSMultiplayer.Core
{
    /// <summary>
    /// Command-line options for running two copies on one PC (testing; see tools/Start-TwoInstances.ps1):
    ///   -wotsmp-window x,y,w,h   borderless window at that screen position/size (re-applied if the game changes it)
    ///   -wotsmp-name NAME        player name for this copy (not saved to the shared config)
    ///   -wotsmp-host             host automatically at the main menu (this PC only, no firewall prompt)
    ///   -wotsmp-join ADDR        join ADDR automatically at the main menu
    /// Without these flags nothing changes.
    /// </summary>
    internal static class LaunchOptions
    {
        internal static RectInt? Window;
        internal static string Name, Join;
        internal static bool Host;
        private static bool autoNetDone;
        private static float nextWindowCheck;

        internal struct RectInt { public int X, Y, W, H; }

        internal static void Parse()
        {
            string[] a = System.Environment.GetCommandLineArgs(); // the game has its own global "Environment" class
            for (int i = 0; i < a.Length; i++)
            {
                string k = a[i].ToLowerInvariant();
                string v = i + 1 < a.Length ? a[i + 1] : null;
                if (k == "-wotsmp-window" && v != null)
                {
                    string[] p = v.Split(',');
                    if (p.Length == 4) Window = new RectInt { X = int.Parse(p[0]), Y = int.Parse(p[1]), W = int.Parse(p[2]), H = int.Parse(p[3]) };
                }
                else if (k == "-wotsmp-name" && v != null) Name = v;
                else if (k == "-wotsmp-join" && v != null) Join = v;
                else if (k == "-wotsmp-host") Host = true;
            }
            if (Window.HasValue || Name != null || Host || Join != null)
                Plugin.Log.LogInfo($"Launch options: window={(Window.HasValue ? Window.Value.X + "," + Window.Value.Y + "," + Window.Value.W + "x" + Window.Value.H : "-")} name={Name} host={Host} join={Join}");
        }

        internal static void Update()
        {
            if (Window.HasValue && Time.realtimeSinceStartup >= nextWindowCheck)
            {
                nextWindowCheck = Time.realtimeSinceStartup + 1f;
                RectInt r = Window.Value;
                if (Screen.fullScreen || Screen.width != r.W || Screen.height != r.H)
                {
                    Screen.SetResolution(r.W, r.H, false);
                    nextWindowCheck = Time.realtimeSinceStartup + 0.3f; // move it right after the resize lands
                }
                else PlaceWindow(r);
            }

            if (!autoNetDone && (Host || Join != null) && Features.MpCampaign.MpCampaign.AtMainMenu() && Time.realtimeSinceStartup > 5f)
            {
                autoNetDone = true;
                Multiplayer.Session.PlayerName = Multiplayer.EffectiveName;
                Multiplayer.Session.MaxPlayers = Plugin.MaxPlayers.Value; // additional players (test copies use the shared config)
                if (Host) Multiplayer.Session.StartHost(Plugin.HostPort.Value, loopbackOnly: true);
                else Multiplayer.Join(Join);
            }
        }

        // ---------- window placement (user32) ----------

        [DllImport("user32.dll")] private static extern bool EnumWindows(EnumProc cb, IntPtr lParam);
        [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetClassName(IntPtr h, StringBuilder sb, int n);
        [DllImport("user32.dll")] private static extern bool GetWindowRect(IntPtr h, out WinRect r);
        [DllImport("user32.dll")] private static extern bool SetWindowPos(IntPtr h, IntPtr after, int x, int y, int w, int hgt, uint flags);
        [DllImport("user32.dll")] private static extern int GetWindowLong(IntPtr h, int index);
        [DllImport("user32.dll")] private static extern int SetWindowLong(IntPtr h, int index, int value);
        [DllImport("kernel32.dll")] private static extern uint GetCurrentProcessId();
        private delegate bool EnumProc(IntPtr h, IntPtr lParam);
        [StructLayout(LayoutKind.Sequential)] private struct WinRect { public int Left, Top, Right, Bottom; }

        private static IntPtr hwnd;

        private static void PlaceWindow(RectInt r)
        {
            if (hwnd == IntPtr.Zero)
            {
                uint me = GetCurrentProcessId();
                EnumWindows((h, l) =>
                {
                    uint pid;
                    GetWindowThreadProcessId(h, out pid);
                    if (pid != me) return true;
                    StringBuilder sb = new StringBuilder(64);
                    GetClassName(h, sb, 64);
                    if (sb.ToString() != "UnityWndClass") return true;
                    hwnd = h;
                    return false;
                }, IntPtr.Zero);
                if (hwnd == IntPtr.Zero) return;
            }
            const int GWL_STYLE = -16;
            const int WS_POPUP = unchecked((int)0x80000000), WS_VISIBLE = 0x10000000;
            int style = GetWindowLong(hwnd, GWL_STYLE);
            if (style != (WS_POPUP | WS_VISIBLE)) SetWindowLong(hwnd, GWL_STYLE, WS_POPUP | WS_VISIBLE); // borderless
            WinRect cur;
            GetWindowRect(hwnd, out cur);
            if (cur.Left != r.X || cur.Top != r.Y || cur.Right - cur.Left != r.W || cur.Bottom - cur.Top != r.H)
                SetWindowPos(hwnd, IntPtr.Zero, r.X, r.Y, r.W, r.H, 0x0004 /*NOZORDER*/ | 0x0020 /*FRAMECHANGED*/);
        }
    }
}
