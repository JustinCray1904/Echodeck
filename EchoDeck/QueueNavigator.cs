namespace EchoDeck;

/// <summary>
/// Which song comes next in the queue (Next / Previous, and what follows when a song ends), as plain functions with no window:
/// the queue is only a count and a position here, so every rule (shuffle, repeat one / all, auto-advance) can be tested.
/// Every method returns the index of the song to play, or -1 for "none" (stop, or nothing in that direction).
/// </summary>
internal static class QueueNavigator
{
    /// <summary>The song for Previous (<paramref name="delta"/> -1) or Next (+1): any other song when shuffling, otherwise the neighbour.</summary>
    public static int Adjacent(int count, int index, int delta, bool shuffle, Random rng)
    {
        if (count == 0) return -1;

        if (shuffle && count > 1)
        {
            int pick;
            do { pick = rng.Next(count); } while (pick == index);
            return pick;
        }

        int i = index + delta;
        return i < 0 || i >= count ? -1 : i;
    }

    /// <summary>What plays when the current song has ended (-1: nothing, the player stops).</summary>
    public static int AfterEnd(int count, int index, LoopMode loop, bool shuffle, bool autoAdvance, Random rng)
    {
        if (loop == LoopMode.One && index >= 0 && index < count) return index;
        if (shuffle && count > 1) return Adjacent(count, index, +1, shuffle, rng);
        if (!autoAdvance && loop != LoopMode.All) return -1;

        if (index >= 0 && index + 1 < count) return index + 1;
        if (loop == LoopMode.All && count > 0) return 0;
        return -1;
    }
}
