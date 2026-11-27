// Each native Windows tray part can be visible, revealed on demand, or hidden.
static class RightAreaModes
{
    public const int Always = 0;
    public const int Peek = 1;
    public const int Off = 2;
    public static readonly int[] Bits = [1, 2, 4, 8];

    public static int[] Default() => [Always, Always, Always, Always];

    public static int[] FromLegacy(int hidden, bool trayOnDemand)
    {
        if ((hidden & 16) != 0) return [Off, Off, Off, Off];
        var modes = Default();
        for (int i = 0; i < Bits.Length; i++)
            if ((hidden & Bits[i]) != 0)
                modes[i] = trayOnDemand && i >= 2 ? Peek : Off;
        return modes;
    }

    public static int[] Normalize(int[]? value, int legacyHidden = 0, bool legacyOnDemand = false)
    {
        if (value is not { Length: 4 }) return FromLegacy(legacyHidden, legacyOnDemand);
        return value.Select(mode => Math.Clamp(mode, Always, Off)).ToArray();
    }

    public static bool HasPeek(int[] modes) => modes.Contains(Peek);

    public static int HiddenMask(int[] modes, bool revealed)
    {
        int mask = 0;
        for (int i = 0; i < Bits.Length; i++)
            if (modes[i] == Off || (modes[i] == Peek && !revealed)) mask |= Bits[i];
        if ((mask & 15) == 15) mask |= 16; // collapse the otherwise empty system tray frame
        return mask;
    }

    public static bool ClockVisible(int[] modes, bool revealed) => modes[0] == Always || (modes[0] == Peek && revealed);
}
