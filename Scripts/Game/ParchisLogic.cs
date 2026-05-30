// ═══════════════════════════════════════════════════
// ParchisLogic.cs
// Client-side mirror of the server's parchis_logic.c.
// Used ONLY for UX: highlighting which pieces can move
// after receiving dice_result. NOT authoritative.
// ═══════════════════════════════════════════════════
using System.Collections.Generic;

public static class ParchisLogic
{
    // ── Board constants (slot order: 0=blue 1=green 2=yellow 3=red) ───────────

    private static readonly string[] SlotColorNames = { "blue", "green", "yellow", "red" };

    private static readonly Dictionary<string, int> StartSquare = new() {
        { "blue",   22 }, { "green", 56 }, { "yellow", 5 }, { "red", 39 },
    };

    private static readonly Dictionary<string, int> EntrySquare = new() {
        { "blue",   17 }, { "green", 51 }, { "yellow", 68 }, { "red", 34 },
    };

    private static readonly Dictionary<string, int> CorrBase = new() {
        { "blue",  111 }, { "green", 131 }, { "yellow", 101 }, { "red", 121 },
    };

    private static readonly Dictionary<string, int> GoalSquare = new() {
        { "blue",  118 }, { "green", 138 }, { "yellow", 108 }, { "red", 128 },
    };

    // Universal safe squares (not capturable).
    private static readonly HashSet<int> UniversalSafe = new() {
        1, 12, 17, 29, 34, 46, 51, 63, 68
    };

    // ── Core API ──────────────────────────────────────────────────────────────

    /// <summary>
    /// Returns the destination board index after advancing <paramref name="steps"/> from
    /// <paramref name="fromSq"/>. Returns -1 if the move would overshoot or is invalid.
    /// </summary>
    public static int Advance(string color, int fromSq, int steps)
    {
        if (fromSq <= 0) return -1; // in home — caller handles exit separately

        int entry    = EntrySquare[color];
        int corrBase = CorrBase[color];

        if (fromSq >= 100) {
            int corrStep = fromSq - corrBase + 1;
            int newStep  = corrStep + steps;
            if (newStep > 8) {
                int overshoot  = newStep - 8;
                int bounceStep = 8 - overshoot;
                if (bounceStep < 1) return -1;
                return corrBase + bounceStep - 1;
            }
            return corrBase + newStep - 1;
        }

        // On main ring — distance to entry point (wrapping).
        int stepsToEntry = entry >= fromSq ? entry - fromSq : (68 - fromSq) + entry;

        if (steps <= stepsToEntry) {
            int to = fromSq + steps;
            if (to > 68) to -= 68;
            return to;
        }

        // Enters corridor.
        int remaining = steps - stepsToEntry;
        if (remaining > 8) {
            int overshoot  = remaining - 8;
            int bounceStep = 8 - overshoot;
            if (bounceStep < 1) return -1;
            return corrBase + bounceStep - 1;
        }
        return corrBase + remaining - 1;
    }

    /// <summary>True if <paramref name="sq"/> is safe for <paramref name="moverColor"/>.</summary>
    public static bool IsSafe(int sq, string moverColor)
    {
        if (UniversalSafe.Contains(sq)) return true;
        return sq == StartSquare[moverColor];
    }

    /// <summary>True if <paramref name="blockerColor"/> has 2+ pieces at <paramref name="sq"/>.</summary>
    public static bool IsBarrier(int sq, string blockerColor, int[][] positions)
    {
        int slot  = ColorToSlot(blockerColor);
        if (slot < 0) return false;
        int count = 0;
        for (int p = 0; p < 4; p++)
            if (positions[slot][p] == sq) count++;
        return count >= 2;
    }

    /// <summary>
    /// True if no enemy barrier exists on intermediate squares between
    /// <paramref name="fromSq"/> and the destination (exclusive of from, exclusive of dest).
    /// </summary>
    public static bool PathClear(string moverColor, int fromSq, int steps, int[][] positions)
    {
        if (steps <= 1) return true;

        int entry    = EntrySquare[moverColor];
        int corrBase = CorrBase[moverColor];
        int pos      = fromSq;

        for (int i = 0; i < steps - 1; i++)
        {
            if (pos >= 100)        pos++;
            else if (pos == entry) pos = corrBase;
            else                   pos = pos == 68 ? 1 : pos + 1;

            // 1. Own barrier blocks passage.
            if (IsBarrier(pos, moverColor, positions)) return false;

            // 2. Safe square check: any 2+ pieces of any color block.
            if (IsSafe(pos, moverColor))
            {
                int totalOcc = 0;
                for (int s = 0; s < 4; s++)
                {
                    for (int p = 0; p < 4; p++)
                    {
                        if (positions[s][p] == pos) totalOcc++;
                    }
                }
                if (totalOcc >= 2) return false;
            }
            else
            {
                // 3. Non-safe square: enemy barriers block.
                foreach (var color in SlotColorNames)
                {
                    if (color == moverColor) continue;
                    if (IsBarrier(pos, color, positions)) return false;
                }
            }
        }
        return true;
    }

    /// <summary>
    /// Returns the list of piece indices (0-3) that can legally be moved given the dice.
    /// <paramref name="positions"/> is [colorSlot][pieceIndex], slot order 0=blue 1=green 2=yellow 3=red.
    /// </summary>
    public static List<int> GetMoveablePieces(string color, int die1, int die2, int[][] positions)
    {
        var result   = new List<int>();
        int slot     = ColorToSlot(color);
        if (slot < 0) return result;

        int  total    = die1 + die2;
        bool canExit  = die1 == 5 || die2 == 5 || total == 5;

        for (int p = 0; p < 4; p++)
        {
            int from = positions[slot][p];

            if (from <= 0) {
                // In home — need a 5.
                if (!canExit) continue;
                int exitSq = StartSquare[color];
                if (!CanLand(exitSq, color, positions)) continue;
                result.Add(p);
            } else {
                // On ring or corridor.
                int to = Advance(color, from, total);
                if (to < 0) continue;
                if (!PathClear(color, from, total, positions)) continue;
                if (!CanLand(to, color, positions)) continue;
                result.Add(p);
            }
        }
        return result;
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private static bool CanLand(int sq, string moverColor, int[][] positions)
    {
        if (sq <= 0) return false;
        if (IsGoal(sq)) return true;

        int totalOcc = 0;
        for (int s = 0; s < 4; s++)
        {
            for (int p = 0; p < 4; p++)
            {
                if (positions[s][p] == sq) totalOcc++;
            }
        }
        return totalOcc < 2;
    }

    public static int ColorToSlot(string color) => color switch {
        "blue"   => 0,
        "green"  => 1,
        "yellow" => 2,
        "red"    => 3,
        _        => -1,
    };

    public static string SlotToColor(int slot) => slot switch {
        0 => "blue",
        1 => "green",
        2 => "yellow",
        3 => "red",
        _ => "",
    };

    public static bool IsGoal(int sq) =>
        sq == 108 || sq == 118 || sq == 128 || sq == 138;
}
