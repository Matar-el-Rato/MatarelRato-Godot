using Godot;

/// <summary>
/// Attached to PlayingSetup. Instantiates chairs and per-player item sets
/// based on the number of players at the table.
///
/// Chair slots (numbered by original scene order):
///   0 = front (+Z from board centre, original chair1), rotation 0°
///   1 = back  (-Z from board centre, original chair2), rotation 180°
///   2 = left  (-X from board centre, original chair3), rotation 90°
///   3 = right (+X from board centre, original chair4), rotation -90°
///
/// Item sets are anchored at the board centre and rotated so each set
/// faces its matching chair:
///   The original item layout sits on the +X side (slot 3, 0° rotation).
///   90° → +Z side (slot 0), -90° → -Z side (slot 1), 180° → -X side (slot 2).
///
/// Player count → slots used:
///   2 players: slots 0 + 1  (opposite, front/back)
///   3 players: slots 0 + 1 + 2
///   4 players: all four slots
/// </summary>
public partial class TableManager : Node3D
{
    [Export] public PackedScene ChairScene;
    [Export] public PackedScene PlayerItemSetScene;

    // Tablero centre in PlayingSetup local space (from original tablero node transform).
    private static readonly Vector3 BoardCenter = new Vector3(-15.453938f, 0.7376139f, -5.248031f);

    private static readonly Vector3[] ChairPositions = new[]
    {
        new Vector3(-15.410943f, 0.4724453f, -3.848565f),  // slot 0: front
        new Vector3(-15.410943f, 0.4724453f, -6.683965f),  // slot 1: back
        new Vector3(-16.878002f, 0.4724453f, -5.2408323f), // slot 2: left
        new Vector3(-14.007259f, 0.4724453f, -5.2408323f), // slot 3: right
    };

    private static readonly float[] ChairRotationsY  = { 0f, 180f,  -90f, +90f };
    private static readonly float[] ItemSetRotationsY = { 90f, -90f, 180f,  0f };

    private static readonly Color[] SlotColors = new[]
    {
        new Color(0.2f, 0.5f,  1f),    // slot 0: blue
        new Color(0.2f, 0.85f, 0.2f),  // slot 1: green
        new Color(1f,   0.85f, 0f),    // slot 2: yellow
        new Color(1f,   0.15f, 0.15f), // slot 3: red
    };

    private static readonly string[] SlotColorNames = { "BLUE", "GREEN", "YELLOW", "RED" };

    private static readonly int[][] SlotOrders = new[]
    {
        new[] { 0, 1 },          // 2 players
        new[] { 0, 1, 2 },       // 3 players
        new[] { 0, 1, 2, 3 },    // 4 players
    };

    public override void _Ready()
    {
        Setup(4); // placeholder; will be replaced by server-driven call
    }

    /// <summary>Spawn chairs and item sets for the given number of players (2–4).</summary>
    public void Setup(int playerCount)
    {
        playerCount = Mathf.Clamp(playerCount, 2, 4);
        foreach (int slot in SlotOrders[playerCount - 2])
        {
            var itemSet = SpawnItemSet(slot);
            SpawnChair(slot, itemSet);
        }
    }

    private void SpawnChair(int slot, PlayerItemSet itemSet)
    {
        if (ChairScene == null) return;
        var chair = ChairScene.Instantiate<Node3D>();
        chair.Position        = ChairPositions[slot];
        chair.RotationDegrees = new Vector3(0f, ChairRotationsY[slot], 0f);
        AddChild(chair);
        if (chair is Chair chairScript)
        {
            chairScript.SetSlotColor(SlotColors[slot], SlotColorNames[slot]);
            chairScript.LinkItemSet(itemSet);
        }
    }

    private PlayerItemSet SpawnItemSet(int slot)
    {
        if (PlayerItemSetScene == null) return null;
        var set = PlayerItemSetScene.Instantiate<Node3D>();
        set.Position        = BoardCenter;
        set.RotationDegrees = new Vector3(0f, ItemSetRotationsY[slot], 0f);
        AddChild(set);

        var itemSetScript = set as PlayerItemSet;
        if (itemSetScript != null)
            itemSetScript.SlotIndex = slot;

        return itemSetScript;
    }
}
