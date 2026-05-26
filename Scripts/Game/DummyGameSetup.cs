using Godot;

/// <summary>
/// Attach to any Node3D in the main scene to get a live tablero + item set + cubilete
/// for testing animations without joining a match.
///
/// Setup:
///   1. Add a Node3D to MainScene, attach this script.
///   2. Set TableroScene   → res://Scenes/Game/Tablero.tscn
///   3. Set ItemSetScene   → res://Scenes/Game/PlayerItemSet.tscn
///   4. Set CubileteScene  → res://Scenes/Game/CubileteAndDice.tscn
///   5. Adjust SpawnColors (default: all four).
///   6. Move the Node3D wherever you want — children inherit the position.
/// </summary>
public partial class DummyGameSetup : Node3D
{
    [Export] public PackedScene TableroScene;
    [Export] public PackedScene ItemSetScene;
    [Export] public PackedScene CubileteScene;

    [ExportGroup("Spawn")]
    [Export] public string[] SpawnColors  = { "yellow", "blue", "red", "green" };
    [Export] public bool     RevealPieces = true;

    public override void _Ready()
    {
        if (TableroScene != null)
        {
            var tablero = TableroScene.Instantiate<TableroController>();
            AddChild(tablero);
            Callable.From(() => SpawnPieces(tablero)).CallDeferred();
        }
        else GD.PushWarning("[DummyGameSetup] TableroScene not set — assign Scenes/Game/Tablero.tscn");

        if (ItemSetScene != null)
        {
            var itemSet = ItemSetScene.Instantiate<PlayerItemSet>();
            AddChild(itemSet); // _Ready queues HideAndDisableAll via CallDeferred
            // Same-frame deferred: runs right after HideAndDisableAll populates _originalScales.
            // RevealAll makes items visible; SetOwned(true) then enables their Interactables.
            Callable.From(() => { itemSet.RevealAll(); itemSet.SetOwned(true); }).CallDeferred();
        }
        else GD.PushWarning("[DummyGameSetup] ItemSetScene not set — assign Scenes/Game/PlayerItemSet.tscn");

        if (CubileteScene != null)
        {
            var cubilete = CubileteScene.Instantiate<CubileteController>();
            AddChild(cubilete);

            // Capture surface position now — used for AppearAt and for re-centering after a roll.
            var boardPos = GlobalPosition;

            Callable.From(() =>
            {
                cubilete.AppearAt(boardPos);
                cubilete.SetInteractionEnabled(true);
            }).CallDeferred();

            // After each roll, wait 3 s then re-animate the cup rising from the table
            // so it can be grabbed and rolled again without restarting the scene.
            cubilete.RollCompleted += (die1, die2) =>
            {
                GetTree().CreateTimer(3.0f).Timeout += () =>
                {
                    if (IsInstanceValid(cubilete))
                        cubilete.AppearAt(boardPos);
                };
            };
        }
        else GD.PushWarning("[DummyGameSetup] CubileteScene not set — assign Scenes/Game/CubileteAndDice.tscn");
    }

    private void SpawnPieces(TableroController tablero)
    {
        foreach (var color in SpawnColors)
        {
            tablero.SpawnPlayer(color);
            if (RevealPieces)
                for (int i = 0; i < 4; i++)
                    tablero.RevealFicha(color, i);
        }
    }
}
