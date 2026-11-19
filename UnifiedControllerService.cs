using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;

namespace GH3_Sync;

public static class XInputNative
{
    [StructLayout(LayoutKind.Sequential)]
    public struct XInputGamepad
    {
        public ushort wButtons;
        public byte bLeftTrigger;
        public byte bRightTrigger;
        public short sThumbLX;
        public short sThumbLY;
        public short sThumbRX;
        public short sThumbRY;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct XInputState
    {
        public uint dwPacketNumber;
        public XInputGamepad Gamepad;
    }

    [DllImport("xinput1_4.dll", EntryPoint = "XInputGetState")]
    private static extern uint XInputGetState14(uint dwUserIndex, out XInputState pState);

    [DllImport("xinput1_3.dll", EntryPoint = "XInputGetState")]
    private static extern uint XInputGetState13(uint dwUserIndex, out XInputState pState);

    [DllImport("xinput9_1_0.dll", EntryPoint = "XInputGetState")]
    private static extern uint XInputGetState910(uint dwUserIndex, out XInputState pState);

    public static bool TryGetFirstConnectedState(out uint slot, out XInputState state)
    {
        for (uint i = 0; i < 4; i++)
        {
            try { if (XInputGetState14(i, out state) == 0) { slot = i; return true; } }
            catch
            {
                try { if (XInputGetState13(i, out state) == 0) { slot = i; return true; } }
                catch
                {
                    try { if (XInputGetState910(i, out state) == 0) { slot = i; return true; } }
                    catch { break; }
                }
            }
        }
        slot = 0;
        state = default;
        return false;
    }

    public const ushort DPAD_UP = 0x0001;
    public const ushort DPAD_DOWN = 0x0002;
    public const ushort START = 0x0010;
    public const ushort BACK = 0x0020;
    public const ushort LEFT_SHOULDER = 0x0100;
    public const ushort RIGHT_SHOULDER = 0x0200;
    public const ushort A = 0x1000;
    public const ushort B = 0x2000;
    public const ushort X = 0x4000;
    public const ushort Y = 0x8000;
}

public enum ControllerInputMode
{
    Auto,
    GamepadGh3,
    GuitarGh3
}

public class UnifiedControllerSnapshot
{
    public bool Connected { get; set; }
    public string DeviceName { get; set; } = "No Controller Detected";
    public string Backend { get; set; } = ""; // "XInput" | "PlayStation HID" | "DirectInput HID"
    public bool IsPlayStation { get; set; }
    public bool IsGuitarDevice { get; set; }
    public bool EffectiveGamepadMode { get; set; } // true = Official GH3 L2/L1/R1/R2/Cross auto-strum

    public bool[] Frets { get; set; } = new bool[5]; // G, R, Y, B, O
    public bool StrumUp { get; set; }
    public bool StrumDown { get; set; }
    public bool StarPowerPressed { get; set; }
    public bool StartPressed { get; set; }
    public bool BackPressed { get; set; }
    public double WhammyNormalized { get; set; } // 0..1
    public double TiltNormalized { get; set; }   // 0..1
    public double LeftTrigger { get; set; }      // 0..1 (L2/LT)
    public double RightTrigger { get; set; }     // 0..1 (R2/RT)
    public uint RawButtonsMask { get; set; }
    public int FirstPressedRawButtonIndex { get; set; } = -1;
}

/// <summary>
/// Unified Zero-Driver Controller Engine for GH3-Sync v1.1.0:
/// Supports Xbox 360 / One / Series & XInput Guitars (XInput) AND
/// PlayStation DualSense Edge, DualSense, DualShock 4, Switch Pro & USB HID Guitars (winmm joyGetPosEx)
/// with exact Official Guitar Hero III mappings out of the box!
/// </summary>
public static class UnifiedControllerService
{
    // ---- WinMM Joystick P/Invoke (works natively for DualSense Edge, DualSense, DS4, HID Guitars!) ----
    private const uint JOY_RETURNALL = 0x000000FFu;
    private const uint JOYERR_NOERROR = 0;

    [StructLayout(LayoutKind.Sequential)]
    private struct JOYINFOEX
    {
        public uint dwSize;
        public uint dwFlags;
        public uint dwXpos;
        public uint dwYpos;
        public uint dwZpos;
        public uint dwRpos;
        public uint dwUpos;
        public uint dwVpos;
        public uint dwButtons;
        public uint dwButtonNumber;
        public uint dwPOV;
        public uint dwReserved1;
        public uint dwReserved2;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct JOYCAPSW
    {
        public ushort wMid;
        public ushort wPid;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)]
        public string szPname;
        public uint wXmin, wXmax, wYmin, wYmax, wZmin, wZmax;
        public uint wNumButtons;
        public uint wPeriodMin, wPeriodMax;
        public uint wRmin, wRmax, wUmin, wUmax, wVmin, wVmax;
        public uint wCaps;
        public uint wMaxAxes, wNumAxes, wMaxButtons;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)]
        public string szRegKey;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)]
        public string szOEMVxD;
    }

    [DllImport("winmm.dll")]
    private static extern uint joyGetNumDevs();

    [DllImport("winmm.dll")]
    private static extern uint joyGetPosEx(uint uJoyID, ref JOYINFOEX pji);

    [DllImport("winmm.dll", CharSet = CharSet.Unicode)]
    private static extern uint joyGetDevCapsW(uint uJoyID, out JOYCAPSW pjc, uint cbjc);

    // ---- XInput Capabilities (detects Xbox Gamepad vs XInput Guitar SubType 0x06/0x07) ----
    [StructLayout(LayoutKind.Sequential)]
    private struct XINPUT_CAPABILITIES
    {
        public byte Type;
        public byte SubType;
        public ushort Flags;
        public XInputNative.XInputGamepad Gamepad;
        public ushort LeftMotorSpeed;
        public ushort RightMotorSpeed;
    }

    [DllImport("xinput1_4.dll", EntryPoint = "XInputGetCapabilities")]
    private static extern uint XInputGetCapabilities14(uint dwUserIndex, uint dwFlags, out XINPUT_CAPABILITIES pCapabilities);

    private static string ResolveFriendlyHidName(ushort vid, ushort pid, string fallback)
    {
        if (vid == 0x054C) // Sony Interactive Entertainment
        {
            return pid switch
            {
                0x0DF2 => "DualSense Edge (PS5)",
                0x0CE6 => "DualSense Wireless Controller (PS5)",
                0x05C4 or 0x09CC => "DualShock 4 (PS4)",
                0x0268 => "DualShock 3 (PS3)",
                _ => $"PlayStation Controller (0x{pid:X4})"
            };
        }
        if (vid == 0x12BA) return "PlayStation Guitar Hero Controller";
        if (vid == 0x1430) return "RedOctane Guitar Hero Controller";
        if (vid == 0x057E) return "Nintendo Switch Pro Controller";
        if (!string.IsNullOrWhiteSpace(fallback) && !fallback.Equals("Microsoft PC-joystick driver", StringComparison.OrdinalIgnoreCase))
            return fallback.Trim();
        return $"HID Controller (VID_{vid:X4}&PID_{pid:X4})";
    }

    public static ControllerInputMode ParseConfiguredMode()
    {
        return (AppConfig.Current.CustomControllerMode ?? "Auto").ToLowerInvariant() switch
        {
            "gamepad" => ControllerInputMode.GamepadGh3,
            "guitar" => ControllerInputMode.GuitarGh3,
            _ => ControllerInputMode.Auto
        };
    }

    /// <summary>
    /// Polls all connected XInput and Native Windows HID (DualSense Edge / DualSense / DS4 / Guitar)
    /// devices and returns a unified GH3-mapped snapshot.
    /// </summary>
    public static UnifiedControllerSnapshot Poll(ControllerInputMode? modeOverride = null)
    {
        var mode = modeOverride ?? ParseConfiguredMode();

        // 1. Check XInput first (Xbox 360 / One / Series / XInput Guitars)
        if (XInputNative.TryGetFirstConnectedState(out uint xIdx, out var xState))
        {
            bool isGuitar = false;
            try
            {
                if (XInputGetCapabilities14(xIdx, 0, out var caps) == 0)
                    isGuitar = caps.SubType == 0x06 || caps.SubType == 0x07 || caps.SubType == 0x0B;
            }
            catch { }

            bool useGamepadLayout = mode == ControllerInputMode.GamepadGh3 || (mode == ControllerInputMode.Auto && !isGuitar);
            var snap = new UnifiedControllerSnapshot
            {
                Connected = true,
                DeviceName = isGuitar ? $"XInput Guitar Controller (Port {xIdx + 1})" : $"Xbox Controller (Port {xIdx + 1})",
                Backend = "XInput",
                IsPlayStation = false,
                IsGuitarDevice = isGuitar,
                EffectiveGamepadMode = useGamepadLayout
            };

            ushort b = xState.Gamepad.wButtons;
            snap.RawButtonsMask = b;
            snap.LeftTrigger = xState.Gamepad.bLeftTrigger / 255.0;
            snap.RightTrigger = xState.Gamepad.bRightTrigger / 255.0;
            snap.StrumUp = (b & XInputNative.DPAD_UP) != 0;
            snap.StrumDown = (b & XInputNative.DPAD_DOWN) != 0;
            snap.StartPressed = (b & XInputNative.START) != 0;
            snap.BackPressed = (b & XInputNative.BACK) != 0;

            if (useGamepadLayout)
            {
                // Official GH3 Xbox Standard Gamepad Layout: LT, LB, RB, RT, A
                snap.Frets[0] = xState.Gamepad.bLeftTrigger > 35;
                snap.Frets[1] = (b & XInputNative.LEFT_SHOULDER) != 0;
                snap.Frets[2] = (b & XInputNative.RIGHT_SHOULDER) != 0;
                snap.Frets[3] = xState.Gamepad.bRightTrigger > 35;
                snap.Frets[4] = (b & XInputNative.A) != 0;
                snap.WhammyNormalized = Math.Clamp(Math.Max(Math.Abs(xState.Gamepad.sThumbLX), Math.Abs(xState.Gamepad.sThumbLY)) / 32768.0, 0.0, 1.0);
                snap.StarPowerPressed = snap.BackPressed;
            }
            else
            {
                // Official GH3 Guitar Controller Layout: A=G, B=R, Y=Y, X=B, LB=O + D-Pad Strum
                snap.Frets[0] = (b & XInputNative.A) != 0;
                snap.Frets[1] = (b & XInputNative.B) != 0;
                snap.Frets[2] = (b & XInputNative.Y) != 0;
                snap.Frets[3] = (b & XInputNative.X) != 0;
                snap.Frets[4] = (b & XInputNative.LEFT_SHOULDER) != 0;
                snap.WhammyNormalized = Math.Clamp((xState.Gamepad.sThumbRX + 32768.0) / 65535.0, 0.0, 1.0);
                snap.TiltNormalized = Math.Clamp((xState.Gamepad.sThumbRY + 32768.0) / 65535.0, 0.0, 1.0);
                snap.StarPowerPressed = snap.BackPressed || snap.TiltNormalized > 0.78;
            }

            ApplyCustomRawButtonOverrides(snap);
            ApplyDebounceFilter(snap);
            return snap;
        }

        // 2. Check Windows Native Multimedia Joystick / HID (DualSense Edge, DualSense, DS4, HID Guitars!)
        try
        {
            uint maxDevs = Math.Min(16u, joyGetNumDevs());
            for (uint id = 0; id < maxDevs; id++)
            {
                var ji = new JOYINFOEX
                {
                    dwSize = (uint)Marshal.SizeOf<JOYINFOEX>(),
                    dwFlags = JOY_RETURNALL
                };
                if (joyGetPosEx(id, ref ji) != JOYERR_NOERROR) continue;

                joyGetDevCapsW(id, out var caps, (uint)Marshal.SizeOf<JOYCAPSW>());
                bool isSony = caps.wMid == 0x054C;
                bool isGuitar = caps.wMid == 0x12BA || caps.wMid == 0x1430 ||
                                (caps.szPname?.Contains("Guitar", StringComparison.OrdinalIgnoreCase) == true);
                bool useGamepadLayout = mode == ControllerInputMode.GamepadGh3 || (mode == ControllerInputMode.Auto && !isGuitar);

                var snap = new UnifiedControllerSnapshot
                {
                    Connected = true,
                    DeviceName = ResolveFriendlyHidName(caps.wMid, caps.wPid, caps.szPname ?? ""),
                    Backend = isSony ? "PlayStation HID" : "DirectInput HID",
                    IsPlayStation = isSony,
                    IsGuitarDevice = isGuitar,
                    EffectiveGamepadMode = useGamepadLayout,
                    RawButtonsMask = ji.dwButtons
                };

                // Record first pressed raw button index (useful for Custom Remap / DualSense Edge back paddles)
                for (int bit = 0; bit < 32; bit++)
                {
                    if ((ji.dwButtons & (1u << bit)) != 0)
                    {
                        snap.FirstPressedRawButtonIndex = bit;
                        break;
                    }
                }

                // POV Hat -> D-Pad Up / Down
                if (ji.dwPOV != 0xFFFF && ji.dwPOV < 36000)
                {
                    snap.StrumUp = ji.dwPOV <= 4500 || ji.dwPOV >= 31500;
                    snap.StrumDown = ji.dwPOV >= 13500 && ji.dwPOV <= 22500;
                }

                // DualSense / DualSense Edge / DS4 Windows HID Button Mapping:
                // 0=Square, 1=Cross, 2=Circle, 3=Triangle, 4=L1, 5=R1, 6=L2, 7=R2, 8=Create/Share, 9=Options
                bool btnSquare   = (ji.dwButtons & (1u << 0)) != 0;
                bool btnCross    = (ji.dwButtons & (1u << 1)) != 0;
                bool btnCircle   = (ji.dwButtons & (1u << 2)) != 0;
                bool btnTriangle = (ji.dwButtons & (1u << 3)) != 0;
                bool btnL1       = (ji.dwButtons & (1u << 4)) != 0;
                bool btnR1       = (ji.dwButtons & (1u << 5)) != 0;
                bool btnL2Bit    = (ji.dwButtons & (1u << 6)) != 0;
                bool btnR2Bit    = (ji.dwButtons & (1u << 7)) != 0;
                bool btnShare    = (ji.dwButtons & (1u << 8)) != 0;
                bool btnOptions  = (ji.dwButtons & (1u << 9)) != 0;

                // DualSense / DS4 analog trigger axes on Windows winmm (U=L2, V=R2; on some drivers R=R2)
                double l2Axis = NormalizeAxis01(ji.dwUpos, caps.wUmin, caps.wUmax);
                double r2Axis = NormalizeAxis01(ji.dwVpos, caps.wVmin, caps.wVmax);
                snap.LeftTrigger = btnL2Bit ? Math.Max(0.85, l2Axis) : l2Axis;
                snap.RightTrigger = btnR2Bit ? Math.Max(0.85, r2Axis) : r2Axis;

                bool l2Pressed = btnL2Bit || (isSony && l2Axis > 0.22);
                bool r2Pressed = btnR2Bit || (isSony && r2Axis > 0.22);

                // Left Stick deflection for Whammy Bar
                double lx = Math.Abs(NormalizeAxisSigned(ji.dwXpos, caps.wXmin, caps.wXmax));
                double ly = Math.Abs(NormalizeAxisSigned(ji.dwYpos, caps.wYmin, caps.wYmax));
                snap.WhammyNormalized = Math.Clamp(Math.Max(lx, ly), 0.0, 1.0);

                snap.StartPressed = btnOptions;
                snap.BackPressed = btnShare;
                snap.StarPowerPressed = btnShare;

                if (useGamepadLayout)
                {
                    // Official GH3 PlayStation Standard Gamepad Layout:
                    // Green = L2, Red = L1, Yellow = R1, Blue = R2, Orange = Cross (X)
                    snap.Frets[0] = l2Pressed;
                    snap.Frets[1] = btnL1;
                    snap.Frets[2] = btnR1;
                    snap.Frets[3] = r2Pressed;
                    snap.Frets[4] = btnCross;
                }
                else
                {
                    // Official GH3 Guitar Layout on PS/HID Guitars:
                    // Green = Cross(1), Red = Circle(2), Yellow = Triangle(3), Blue = Square(0), Orange = L1(4)
                    snap.Frets[0] = btnCross;
                    snap.Frets[1] = btnCircle;
                    snap.Frets[2] = btnTriangle;
                    snap.Frets[3] = btnSquare;
                    snap.Frets[4] = btnL1;
                }

                ApplyCustomRawButtonOverrides(snap);
                ApplyDebounceFilter(snap);
                return snap;
            }
        }
        catch { }

        return new UnifiedControllerSnapshot { Connected = false };
    }

    private static void ApplyCustomRawButtonOverrides(UnifiedControllerSnapshot snap)
    {
        var custom = AppConfig.Current.CustomPadButtonIndices;
        if (custom == null || custom.Length < 5) return;
        for (int i = 0; i < 5; i++)
        {
            int rawIdx = custom[i];
            if (rawIdx >= 0 && rawIdx < 32)
            {
                if ((snap.RawButtonsMask & (1u << rawIdx)) != 0)
                    snap.Frets[i] = true;
            }
        }
    }

    private static long _lastStrumTimestamp = 0;
    private static bool _lastStrumWasUp = false;

    private static void ApplyDebounceFilter(UnifiedControllerSnapshot snap)
    {
        int debounceMs = AppConfig.Current.ControllerDebounceMs;
        if (debounceMs <= 0) return;

        long now = Environment.TickCount64;
        bool hasStrum = snap.StrumUp || snap.StrumDown;

        if (hasStrum)
        {
            bool isOppositeStrum = (snap.StrumUp && !_lastStrumWasUp) || (snap.StrumDown && _lastStrumWasUp);
            if (isOppositeStrum && (now - _lastStrumTimestamp) < debounceMs)
            {
                // Rebound bounce detected: suppress false overstrum
                snap.StrumUp = false;
                snap.StrumDown = false;
                return;
            }

            _lastStrumTimestamp = now;
            _lastStrumWasUp = snap.StrumUp;
        }
    }

    private static double NormalizeAxis01(uint val, uint min, uint max)
    {
        if (max <= min) return 0.0;
        return Math.Clamp((val - (double)min) / (max - (double)min), 0.0, 1.0);
    }

    private static double NormalizeAxisSigned(uint val, uint min, uint max)
    {
        if (max <= min) return 0.0;
        double norm = ((val - (double)min) / (max - (double)min)) * 2.0 - 1.0;
        return Math.Abs(norm) < 0.14 ? 0.0 : Math.Clamp(norm, -1.0, 1.0);
    }
}
