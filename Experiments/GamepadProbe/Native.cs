using System.Runtime.InteropServices;

namespace ZZZ.GamepadProbe;

internal static class Native
{
    [DllImport("user32.dll")] internal static extern nint GetForegroundWindow();
    [DllImport("user32.dll")] internal static extern uint GetWindowThreadProcessId(nint window, out uint processId);
    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool RegisterHotKey(nint window, int id, uint modifiers, uint key);
    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool UnregisterHotKey(nint window, int id);
    [DllImport("xinput1_4.dll")] internal static extern uint XInputGetState(uint index, out XInputState state);

    internal static uint ForegroundProcessId()
    {
        GetWindowThreadProcessId(GetForegroundWindow(), out var pid);
        return pid;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct XInputState
    {
        public uint Packet;
        public ushort Buttons;
        public byte LeftTrigger, RightTrigger;
        public short LeftX, LeftY, RightX, RightY;
        public readonly bool IsNeutral => Buttons == 0 && LeftTrigger == 0 && RightTrigger == 0
            && LeftX == 0 && LeftY == 0 && RightX == 0 && RightY == 0;
    }
}
