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

    // Clockwise turn order per user specification: yellow → green → red → blue.
    private static readonly string[] FullClockwiseOrder = { "yellow", "green", "red", "blue" };

    // Active subset of FullClockwiseOrder for the current match.
    private readonly System.Collections.Generic.List<string> _activeTurnOrder = new();
    private int _currentTurnIndex = 0;

    // slot index → spawned PlayerItemSet, populated in SpawnItemSet.
    private readonly System.Collections.Generic.Dictionary<int, PlayerItemSet> _itemSetsBySlot = new();

    // Cubilete geometry cached at setup time.
    private float _cubileteRadius      = 0.744f;
    private float _cubileteHeightOff   = 0.075f;
    private bool  _rollConnected       = false;

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
        _takenChairCount  = 0;
        _activeTurnOrder.Clear();
        _currentTurnIndex = 0;
        _rollConnected    = false;
        _itemSetsBySlot.Clear();
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
                    var redPos          = _chairsByColor.TryGetValue("red", out var rc) ? rc.GlobalPosition : Vector3.Zero;
                    var boardCenterW    = GetNodeOrNull<Node3D>("tablero")?.GlobalPosition ?? ToGlobal(BoardCenter);
                    OphanimNode?.DescendAndActivate(redPos, boardCenterW);
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
                var redPos       = _chairsByColor.TryGetValue("red", out var rc) ? rc.GlobalPosition : Vector3.Zero;
                var boardCenterW = GetNodeOrNull<Node3D>("tablero")?.GlobalPosition ?? ToGlobal(BoardCenter);
                OphanimNode?.DescendAndActivate(redPos, boardCenterW);
            }
            else if (action == "initiative_sequence")
            {
                if (OphanimNode != null && root.TryGetProperty("shots", out var shotsEl))
                {
                    int    winnerUserId = root.TryGetProperty("winner_user_id", out var wEl) ? wEl.GetInt32() : -1;
                    string winnerName   = _usernameByUserId.TryGetValue(winnerUserId, out var wName) ? wName : "";

                    // Record which turn slot the winner occupies.
                    if (winnerUserId >= 0 && _colorByUserId.TryGetValue(winnerUserId, out var wColor))
                    {
                        int idx = _activeTurnOrder.IndexOf(wColor.ToLower());
                        _currentTurnIndex = idx >= 0 ? idx : 0;
                    }

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

                    // Build item grant rays: one (worldPos, spawnCallback) per item per player.
                    var itemGrants = new System.Collections.Generic.List<(Vector3, System.Action)>();
                    if (root.TryGetProperty("item_grants", out var grantsEl))
                    {
                        foreach (var grant in grantsEl.EnumerateArray())
                        {
                            int gUid = grant.TryGetProperty("user_id", out var gUidEl) ? gUidEl.GetInt32() : -1;
                            if (gUid < 0 || !grant.TryGetProperty("items", out var itemsEl)) continue;
                            if (!_colorByUserId.TryGetValue(gUid, out var gColor)) continue;
                            int slot = ColorToSlot(gColor.ToLower());
                            if (slot < 0 || !_itemSetsBySlot.TryGetValue(slot, out var itemSet)) continue;

                            foreach (var itemEl in itemsEl.EnumerateArray())
                            {
                                string itemName    = itemEl.GetString();
                                Vector3 itemPos    = itemSet.GetItemWorldPosition(itemName);
                                var capturedSet    = itemSet;
                                var capturedName   = itemName;
                                itemGrants.Add((itemPos, () => capturedSet.SpawnItem(capturedName)));
                            }
                        }
                    }

                    if (shots.Count > 0)
                        OphanimNode.StartInitiativeSequence(shots.ToArray(), winnerName,
                            itemGrants.Count > 0 ? itemGrants.ToArray() : null);

                    // Show the cubilete once the gun sequence finishes.
                    if (!OphanimNode.IsConnected(
                            Ophanim.SignalName.InitiativeSequenceCompleted,
                            Callable.From(OnInitiativeSequenceCompleted)))
                    {
                        OphanimNode.InitiativeSequenceCompleted += OnInitiativeSequenceCompleted;
                    }
                }
            }
            else if (action == "dice_result")
            {
                int userId = root.TryGetProperty("user_id", out var drUid) ? drUid.GetInt32() : -1;
                int die1   = root.TryGetProperty("die1",    out var dr1)   ? dr1.GetInt32()   : 0;
                int die2   = root.TryGetProperty("die2",    out var dr2)   ? dr2.GetInt32()   : 0;
                int total  = root.TryGetProperty("total",   out var drTot) ? drTot.GetInt32() : die1 + die2;

                if (userId >= 0 && userId != LiveConnectionManager.LocalUserId)
                {
                    string who = _usernameByUserId.TryGetValue(userId, out var dName) ? dName : $"#{userId}";
                    ChatManager.AddLog($"[color=#aaaaff][DICE][/color] {who} rolled {die1}, {die2} (Total: {total})");

                    if (_activeTurnOrder.Count > 0)
                    {
                        _currentTurnIndex = (_currentTurnIndex + 1) % _activeTurnOrder.Count;
                        string nextColor  = _activeTurnOrder[_currentTurnIndex];
                        HandleRemoteRoll(die1, die2, who, nextColor);
                    }
                }
            }
            else if (action == "turn_start")
            {
                int userId = root.TryGetProperty("user_id", out var tsUid) ? tsUid.GetInt32() : -1;
                GD.Print($"[TM] turn_start user_id={userId}");
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

    /// <summary>Spawn chairs, item sets, and board pieces for the given number of players (2–4).</summary>
    public void Setup(int playerCount)
    {
        playerCount = Mathf.Clamp(playerCount, 2, 4);

        // Build the active clockwise turn order from the slots in use.
        var activeSlots = SlotOrders[playerCount - 2];
        var activeColorSet = new System.Collections.Generic.HashSet<string>();
        foreach (int slot in activeSlots)
            activeColorSet.Add(SlotColorNames[slot].ToLower());

        _activeTurnOrder.Clear();
        foreach (var c in FullClockwiseOrder)
            if (activeColorSet.Contains(c)) _activeTurnOrder.Add(c);

        // Spawn chairs and item sets.
        foreach (int slot in activeSlots)
        {
            var itemSet = SpawnItemSet(slot);
            SpawnChair(slot, itemSet);
        }

        // Deferred so child nodes' global transforms are ready.
        Callable.From(SetupPiecesAndCubilete).CallDeferred();
    }

    private void SetupPiecesAndCubilete()
    {
        // Spawn pieces on the board for each active color.
        var tableroCtrl = GetNodeOrNull<TableroController>("tablero");
        if (tableroCtrl != null)
            foreach (var color in _activeTurnOrder)
                tableroCtrl.SpawnPlayer(color);

        // Cache cubilete geometry and hide it initially.
        var cubilete = GetNodeOrNull<CubileteController>("CubileteAndDice");
        var tablero  = GetNodeOrNull<Node3D>("tablero");
        if (cubilete != null && tablero != null)
        {
            var off = cubilete.GlobalPosition - tablero.GlobalPosition;
            _cubileteRadius    = new Vector2(off.X, off.Z).Length();
            // Cubilete._Ready already lowered Y by HiddenDepth, so restore it for the surface position.
            _cubileteHeightOff = off.Y + cubilete.HiddenDepth;
            GD.Print($"[TM] Cubilete radius={_cubileteRadius:F3} heightOff={_cubileteHeightOff:F3}");
        }

        // Connect roll-completed signal (once per match).
        if (cubilete != null && !_rollConnected)
        {
            cubilete.RollCompleted += OnRollCompleted;
            _rollConnected = true;
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

    // ── Cubilete turn management ──────────────────────────────────────────────

    private void OnInitiativeSequenceCompleted()
    {
        if (_activeTurnOrder.Count == 0) return;
        string starterColor = _activeTurnOrder[_currentTurnIndex];
        var pos = GetCubiletePositionForColor(starterColor);
        GetNodeOrNull<CubileteController>("CubileteAndDice")?.AppearAt(pos);
        GD.Print($"[TM] Cubilete appearing for starter: {starterColor} at {pos}");
    }

    private async void HandleRemoteRoll(int die1, int die2, string who, string nextColor)
    {
        // Fire-and-forget physics throw on the local cubilete (cosmetic; dice snap back after ~1 s).
        GetNodeOrNull<CubileteController>("CubileteAndDice")?.PlayRemoteThrow(1.5f);

        // Wait the full throw duration before showing the HUD result.
        await ToSignal(GetTree().CreateTimer(1.0f), SceneTreeTimer.SignalName.Timeout);
        if (!IsInsideTree()) return;

        DiceHUD.ShowStatic(die1, die2);
        await ToSignal(GetTree().CreateTimer(2.5f), SceneTreeTimer.SignalName.Timeout);
        if (!IsInsideTree()) return;
        DiceHUD.HideResult();
        await ToSignal(GetTree().CreateTimer(0.45f), SceneTreeTimer.SignalName.Timeout);
        if (!IsInsideTree()) return;

        var targetPos   = GetCubiletePositionForColor(nextColor);
        var boardCenter = GetNodeOrNull<Node3D>("tablero")?.GlobalPosition ?? GlobalPosition;
        GetNodeOrNull<CubileteController>("CubileteAndDice")?.MoveToPlayer(targetPos, boardCenter);
        GD.Print($"[TM] Remote roll by {who}; cubilete arcing to {nextColor}");
    }

    private void OnRollCompleted(int die1, int die2)
    {
        // Send result to server so it can validate and broadcast to other clients.
        string rollJson = $"{{\"action\":\"roll_dice\",\"die1\":{die1},\"die2\":{die2}}}";
        LiveConnectionManager.SendGameAction(
            LiveConnectionManager.CurrentMatchId,
            LiveConnectionManager.LocalUserId,
            rollJson);

        if (_activeTurnOrder.Count == 0) return;
        _currentTurnIndex = (_currentTurnIndex + 1) % _activeTurnOrder.Count;
        string nextColor  = _activeTurnOrder[_currentTurnIndex];
        var targetPos     = GetCubiletePositionForColor(nextColor);
        var boardCenter   = GetNodeOrNull<Node3D>("tablero")?.GlobalPosition ?? GlobalPosition;
        GetNodeOrNull<CubileteController>("CubileteAndDice")?.MoveToPlayer(targetPos, boardCenter);
        GD.Print($"[TM] Cubilete arcing to next player: {nextColor} at {targetPos}");
    }

    private Vector3 GetCubiletePositionForColor(string color)
    {
        var tablero = GetNodeOrNull<Node3D>("tablero");
        if (tablero == null) return GlobalPosition;

        var boardCenter = tablero.GlobalPosition;

        if (!_chairsByColor.TryGetValue(color, out var chair))
            return boardCenter;

        var toChair = chair.GlobalPosition - boardCenter;
        toChair.Y = 0f;
        if (toChair.LengthSquared() < 0.001f) return boardCenter;
        toChair = toChair.Normalized();

        return new Vector3(
            boardCenter.X + toChair.X * _cubileteRadius,
            boardCenter.Y + _cubileteHeightOff,
            boardCenter.Z + toChair.Z * _cubileteRadius);
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
        {
            itemSetScript.SlotIndex  = slot;
            _itemSetsBySlot[slot]    = itemSetScript;
        }

        return itemSetScript;
    }

    private int ColorToSlot(string colorLower)
    {
        for (int i = 0; i < SlotColorNames.Length; i++)
            if (SlotColorNames[i].ToLower() == colorLower) return i;
        return -1;
    }
}
