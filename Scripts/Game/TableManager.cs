using Godot;
using System;
using System.Text.Json;

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
    [Export] public Ophanim     OphanimNode;

    // Color key (lowercase) → spawned chair/token, for network event lookups.
    private readonly System.Collections.Generic.Dictionary<string, Chair>     _chairsByColor     = new();
    private readonly System.Collections.Generic.Dictionary<string, SeatToken> _seatTokensByColor = new();
    private readonly System.Collections.Generic.Dictionary<int, string>       _colorByUserId     = new();
    private readonly System.Collections.Generic.Dictionary<int, string>       _usernameByUserId  = new();

    private int _takenChairCount = 0;

    // Color the local player chose this match ("" = not chosen yet).
    private string _localChosenColor = "";

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
        new[] { 3, 2 },          // 2 players: RED, YELLOW
        new[] { 3, 2, 1 },       // 3 players: RED, YELLOW, GREEN
        new[] { 3, 2, 1, 0 },    // 4 players: RED, YELLOW, GREEN, BLUE
    };

    public override void _Ready()
    {
        GD.Print($"[TM] _Ready (instance {GetInstanceId()})");
    }

    public void StartGame(int playerCount)
    {
        GD.Print($"[TM] StartGame playerCount={playerCount}");
        Setup(playerCount);
        GD.Print($"[TM] Setup done, chairs in dict: {_chairsByColor.Count} ({string.Join(", ", _chairsByColor.Keys)})");
    }

    public void ResetGame()
    {
        Chair.ResetLocalChoice();
        _localChosenColor = "";
        foreach (var child in GetChildren())
            if (child is Chair || child is PlayerItemSet || child is SeatToken)
                child.QueueFree();
        _chairsByColor.Clear();
        _seatTokensByColor.Clear();
        _colorByUserId.Clear();
        _usernameByUserId.Clear();
        _takenChairCount = 0;
    }

    public override void _Process(double delta)
    {
        while (LiveConnectionManager.PendingGameActions.TryDequeue(out var json))
            HandleGameAction(json);
    }

    private void HandleGameAction(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root      = doc.RootElement;
            if (!root.TryGetProperty("action", out var actionEl)) return;
            string action = actionEl.GetString();

            if (action == "chair_taken")
            {
                string color    = root.GetProperty("color").GetString();
                string username = root.TryGetProperty("username", out var unEl) ? unEl.GetString() : "?";
                int    skinId   = root.TryGetProperty("skin_id",  out var skEl) ? skEl.GetInt32()  : 101;
                int    userId   = root.TryGetProperty("user_id",  out var uidEl) ? uidEl.GetInt32() : -1;

                if (userId >= 0)
                {
                    _colorByUserId[userId]    = color;
                    _usernameByUserId[userId] = username;
                }

                if (_chairsByColor.TryGetValue(color, out var chair))
                {
                    if (color == Chair.LocalChosenKey)
                        _localChosenColor = color;

                    if (color != _localChosenColor)
                    {
                        chair.SetTaken();
                        SpawnSeatToken(chair, color, username, skinId);
                    }
                }
                else
                {
                    GD.PrintErr($"[TM] chair_taken: color '{color}' not in dict. Keys: {string.Join(", ", _chairsByColor.Keys)}");
                }

                // Client-side fallback: trigger Ophanim when all chairs are filled.
                // _hasActivated guard prevents a double-trigger if server also sends chairs_locked.
                _takenChairCount++;
                if (_takenChairCount >= _chairsByColor.Count && _chairsByColor.Count > 0)
                {
                    var redPos = _chairsByColor.TryGetValue("red", out var rc) ? rc.GlobalPosition : Vector3.Zero;
                    OphanimNode?.DescendAndActivate(redPos);
                }
            }
            else if (action == "chair_vacated")
            {
                string color  = root.GetProperty("color").GetString();
                int    userId = root.TryGetProperty("user_id", out var uvEl) ? uvEl.GetInt32() : -1;

                if (_seatTokensByColor.TryGetValue(color, out var token) && IsInstanceValid(token))
                {
                    token.QueueFree();
                    _seatTokensByColor.Remove(color);
                }

                if (_chairsByColor.TryGetValue(color, out var vacatedChair))
                    vacatedChair.SetVacated();

                if (userId >= 0)
                {
                    _colorByUserId.Remove(userId);
                    _usernameByUserId.Remove(userId);
                }
                if (_takenChairCount > 0) _takenChairCount--;
            }
            else if (action == "chairs_locked")
            {
                var redPos = _chairsByColor.TryGetValue("red", out var rc) ? rc.GlobalPosition : Vector3.Zero;
                OphanimNode?.DescendAndActivate(redPos);
            }
            else if (action == "initiative_sequence")
            {
                if (OphanimNode != null && root.TryGetProperty("shots", out var shotsEl))
                {
                    int    winnerUserId = root.TryGetProperty("winner_user_id", out var wEl) ? wEl.GetInt32() : -1;
                    string winnerName   = _usernameByUserId.TryGetValue(winnerUserId, out var wName) ? wName : "";

                    var shots = new System.Collections.Generic.List<(Vector3, string)>();
                    foreach (var shot in shotsEl.EnumerateArray())
                    {
                        int    uid    = shot.TryGetProperty("user_id", out var sUid) ? sUid.GetInt32() : -1;
                        string result = shot.TryGetProperty("result",  out var sRes) ? sRes.GetString() : "click";
                        if (_colorByUserId.TryGetValue(uid, out var sColor) &&
                            _chairsByColor.TryGetValue(sColor, out var sChair))
                        {
                            shots.Add((sChair.GlobalPosition, result));
                        }
                    }
                    if (shots.Count > 0)
                        OphanimNode.StartInitiativeSequence(shots.ToArray(), winnerName);
                }
            }
        }
        catch (Exception ex)
        {
            GD.PrintErr($"[TM] HandleGameAction exception: {ex.Message}");
        }
    }

    private void SpawnSeatToken(Chair chair, string color, string username, int skinId)
    {
        if (_seatTokensByColor.TryGetValue(color, out var old) && IsInstanceValid(old))
            old.QueueFree();

        var token = new SeatToken();
        AddChild(token);
        // Token is at the chair's world origin with chair's rotation.
        // The sitting offset is applied internally by SeatToken using the CharacterEntry.
        token.GlobalPosition = chair.GlobalPosition;
        token.GlobalRotation = chair.GlobalRotation;
        token.SetPlayerInfo(username, color, skinId, chair);
        token.Appear();
        _seatTokensByColor[color] = token;
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
            _chairsByColor[SlotColorNames[slot].ToLower()] = chairScript;
            chairScript.Appear();
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
