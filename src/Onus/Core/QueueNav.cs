namespace Onus.Core;

public enum RepeatMode { Off, All, One }

/// <summary>Pure play-queue navigation, so shuffle/repeat rules are testable without a media player.</summary>
public static class QueueNav
{
    /// <summary>Index to play after <paramref name="index"/>, or -1 to stop.
    /// <paramref name="auto"/> = the track ended by itself (Repeat One only loops then, not on a Next click).</summary>
    public static int Next(int index, int count, bool shuffle, RepeatMode repeat, bool auto, Random rng)
    {
        if (count == 0) return -1;
        if (auto && repeat == RepeatMode.One) return index;
        if (shuffle && count > 1)
        {
            int pick;
            do pick = rng.Next(count); while (pick == index);
            return pick;
        }
        var next = index + 1;
        if (next < count) return next;
        return repeat == RepeatMode.Off ? -1 : 0;
    }

    /// <summary>Restart the current track if we are past 3 s in, else step back (wrapping with Repeat All).</summary>
    public static int Previous(int index, int count, double positionSeconds, RepeatMode repeat)
    {
        if (count == 0) return -1;
        if (positionSeconds > 3 || index < 0) return Math.Max(index, 0);
        if (index > 0) return index - 1;
        return repeat == RepeatMode.All ? count - 1 : 0;
    }
}
