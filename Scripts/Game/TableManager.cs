using Godot;
using System;
using System.Collections.Generic;
using System.Text.Json;

/// <summary>
/// Attached to PlayingSetup. Drives the full Parchís turn loop by listening to
/// server broadcasts and wiring into TableroController for piece animation.
///
/// Chair slots:   0=front(+Z)  1=back(-Z)  2=left(-X)  3=right(+X)
/// Slot → color:  0=blue  1=green  2=yellow  3=red
/// </summary>
public partial class TableManager : Node3D
{
    [Export] public PackedScene ChairScene;
    [Export] public PackedScene PlayerItemSetScene;
    [Export] public Ophanim     OphanimNode;

    // ── Maps ──────────────────────────────────────────────────────────────────
    private readonly Dictionary<string, Chair>     _chairsByColor     = new();
    private readonly Dictionary<string, SeatToken> _seatTokensByColor = new();
    private readonly Dictionary<int, string>       _colorByUserId     = new();
    private readonly Dictionary<int, string>       _usernameByUserId  = new();

    private int _takenChairCount = 0;
    private string _localChosenColor = "";

    // ── Layout ────────────────────────────────────────────────────────────────
    private static readonly Vector3 BoardCenter = new Vector3(-15.453938f, 0.7376139f, -5.248031f);

    private static readonly Vector3[] ChairPositions = new[]
    {
        new Vector3(-15.410943f, 0.4724453f, -3.848565f),
        new Vector3(-15.410943f, 0.4724453f, -6.683965f),
        new Vector3(-16.878002f, 0.4724453f, -5.2408323f),
        new Vector3(-14.007259f, 0.4724453f, -5.2408323f),
    };

    private static readonly float[] ChairRotationsY  = { 0f, 180f, -90f, +90f };
    private static readonly float[] ItemSetRotationsY = { 90f, -90f, 180f, 0f };

    private static readonly Color[] SlotColors = new[]
    {
        new Color(0.2f, 0.5f,  1f),
        new Color(0.2f, 0.85f, 0.2f),
        new Color(1f,   0.85f, 0f),
        new Color(1f,   0.15f, 0.15f),
    };

    private static readonly string[] SlotColorNames = { "BLUE", "GREEN", "YELLOW", "RED" };

    private static readonly int[][] SlotOrders = new[]
    {
        new[] { 3, 2 },
        new[] { 3, 2, 1 },
        new[] { 3, 2, 1, 0 },
    };

    private static readonly string[] FullClockwiseOrder = { "yellow", "green", "red", "blue" };

    private readonly List<string> _activeTurnOrder = new();
    private readonly Dictionary<int, PlayerItemSet> _itemSetsBySlot = new();

    // ── Cubilete ──────────────────────────────────────────────────────────────
    private float _cubileteRadius    = 0.744f;
    private float _cubileteHeightOff = 0.075f;
    private bool  _rollConnected     = false;
    // Track which player the cubilete is currently at so we skip redundant arcs
    // (first turn_start after initiative, doubles extra turn).
    private int   _cubileteAtUserId  = -1;
    // Suppress MoveToPlayer while Ophanim's initiative sequence is still playing.
    private bool  _initiativeInProgress = false;

    // ── Game board state ──────────────────────────────────────────────────────
    // positions[colorSlot][pieceIndex]; slot 0=blue 1=green 2=yellow 3=red.
    private readonly int[][] _boardPositions = new int[4][]
    {
        new int[4], new int[4], new int[4], new int[4],
    };

    private int[] _goldenSquares = Array.Empty<int>();

    // Current dice and move state.
    private int          _pendingDie1;
    private int          _pendingDie2;
    private bool         _isMyTurn = false;
    private readonly List<FichaNode> _selectablePieces = new();

    // Hover tracking — driven by manual camera ray (physics picking blocked by tablero trimesh).
    private FichaNode _hoveredPiece = null;
    private Camera3D  _hoverCamera  = null;

    // ── Lifecycle ─────────────────────────────────────────────────────────────

    public override void _Ready()
    {
        GD.Print($"[TM] _Ready (instance {GetInstanceId()})");
    }

    public void StartGame(int playerCount)
    {
        GD.Print($"[TM] StartGame playerCount={playerCount}");
        Setup(playerCount);
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
        _rollConnected       = false;
        _cubileteAtUserId    = -1;
        _initiativeInProgress = false;
        _itemSetsBySlot.Clear();
        foreach (var row in _boardPositions)
            Array.Clear(row, 0, row.Length);
        ClearSelectables();
    }

    public override void _Process(double delta)
    {
        while (LiveConnectionManager.PendingGameActions.TryDequeue(out var json))
            HandleGameAction(json);

        if (_selectablePieces.Count > 0 && FocusController.Instance?.IsFocused == true)
            UpdatePieceHover();
    }

    // ── Game action dispatcher ─────────────────────────────────────────────────

    private void HandleGameAction(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root      = doc.RootElement;
            if (!root.TryGetProperty("action", out var actionEl)) return;
            string action = actionEl.GetString();

            switch (action)
            {
                case "chair_taken":          OnChairTaken(root);          break;
                case "chair_vacated":        OnChairVacated(root);        break;
                case "chairs_locked":        OnChairsLocked(root);        break;
                case "initiative_sequence":  OnInitiativeSequence(root);  break;
                case "turn_start":           OnTurnStart(root);           break;
                case "dice_result":          OnDiceResult(root);          break;
                case "piece_moved":          OnPieceMoved(root);          break;
                case "capture":              OnCapture(root);             break;
                case "goal_scored":          OnGoalScored(root);          break;
                case "barrier_formed":       OnBarrierFormed(root);       break;
                case "barrier_broken":       OnBarrierBroken(root);       break;
                case "extra_turn":           OnExtraTurn(root);           break;
                case "turn_end":             OnTurnEnd(root);             break;
                case "golden_square_event":  OnGoldenSquareEvent(root);   break;
                case "triple_double_penalty": OnTripleDouble(root);       break;
                case "handcuff_skip":        OnHandcuffSkip(root);        break;
                case "life_lost":            OnLifeLost(root);            break;
                case "game_over":            OnGameOver(root);            break;
            }
        }
        catch (Exception ex)
        {
            GD.PrintErr($"[TM] HandleGameAction exception: {ex.Message}");
        }
    }

    // ── Chair phase ───────────────────────────────────────────────────────────

    private void OnChairTaken(JsonElement root)
    {
        string color    = root.GetProperty("color").GetString();
        string username = root.TryGetProperty("username", out var unEl) ? unEl.GetString() : "?";
        int    skinId   = root.TryGetProperty("skin_id",  out var skEl) ? skEl.GetInt32()  : 101;
        int    userId   = root.TryGetProperty("user_id",  out var uidEl) ? uidEl.GetInt32() : -1;

        if (userId >= 0) {
            _colorByUserId[userId]    = color;
            _usernameByUserId[userId] = username;
        }

        if (_chairsByColor.TryGetValue(color, out var chair))
        {
            if (color == Chair.LocalChosenKey)
                _localChosenColor = color;

            if (color != _localChosenColor) {
                chair.SetTaken();
                SpawnSeatToken(chair, color, username, skinId);
            }
        }

        _takenChairCount++;
        if (_takenChairCount >= _chairsByColor.Count && _chairsByColor.Count > 0)
        {
            var redPos       = _chairsByColor.TryGetValue("red", out var rc) ? rc.GlobalPosition : Vector3.Zero;
            var boardCenterW = GetNodeOrNull<Node3D>("tablero")?.GlobalPosition ?? ToGlobal(BoardCenter);
            OphanimNode?.DescendAndActivate(redPos, boardCenterW);
        }
    }

    private void OnChairVacated(JsonElement root)
    {
        string color  = root.GetProperty("color").GetString();
        int    userId = root.TryGetProperty("user_id", out var uvEl) ? uvEl.GetInt32() : -1;

        if (_seatTokensByColor.TryGetValue(color, out var token) && IsInstanceValid(token)) {
            token.Disappear();
            _seatTokensByColor.Remove(color);
        }
        if (_chairsByColor.TryGetValue(color, out var vacatedChair))
            vacatedChair.SetVacated();
        if (userId >= 0) {
            _colorByUserId.Remove(userId);
            _usernameByUserId.Remove(userId);
        }
        if (_takenChairCount > 0) _takenChairCount--;
    }

    private void OnChairsLocked(JsonElement root)
    {
        var redPos       = _chairsByColor.TryGetValue("red", out var rc) ? rc.GlobalPosition : Vector3.Zero;
        var boardCenterW = GetNodeOrNull<Node3D>("tablero")?.GlobalPosition ?? ToGlobal(BoardCenter);
        OphanimNode?.DescendAndActivate(redPos, boardCenterW);
    }

    private void OnInitiativeSequence(JsonElement root)
    {
        // Suppress cubilete arc during the full Ophanim sequence.
        _initiativeInProgress = true;

        // Store golden squares if present.
        if (root.TryGetProperty("golden_squares", out var gsEl))
        {
            var tmp = new List<int>();
            foreach (var el in gsEl.EnumerateArray()) tmp.Add(el.GetInt32());
            _goldenSquares = tmp.ToArray();
        }

        if (OphanimNode == null || !root.TryGetProperty("shots", out var shotsEl)) return;

        int    winnerUserId = root.TryGetProperty("winner_user_id", out var wEl) ? wEl.GetInt32() : -1;
        string winnerName   = _usernameByUserId.TryGetValue(winnerUserId, out var wName) ? wName : "";

        var shots = new List<(Vector3, string)>();
        foreach (var shot in shotsEl.EnumerateArray())
        {
            int    uid    = shot.TryGetProperty("user_id", out var sUid) ? sUid.GetInt32() : -1;
            string result = shot.TryGetProperty("result",  out var sRes) ? sRes.GetString() : "click";
            if (_colorByUserId.TryGetValue(uid, out var sColor) &&
                _chairsByColor.TryGetValue(sColor, out var sChair))
                shots.Add((sChair.GlobalPosition, result));
        }

        var itemGrants = new List<(Vector3, Action)>();
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
                    string itemName  = itemEl.GetString();
                    Vector3 itemPos  = itemSet.GetItemWorldPosition(itemName);
                    var capturedSet  = itemSet;
                    var capturedName = itemName;
                    itemGrants.Add((itemPos, () => capturedSet.SpawnItem(capturedName)));
                }
            }
        }

        // Prepend a cubilete spawn ray so it appears at the winner's spot
        // with a brimstone beam, before the item rays fire.
        if (winnerUserId >= 0 && _colorByUserId.TryGetValue(winnerUserId, out var winnerColor))
        {
            Vector3 cubPos = GetCubiletePositionForColor(winnerColor.ToLower());
            int     wUid   = winnerUserId;
            (Vector3, Action) cubiletGrant = (cubPos, () =>
            {
                GetNodeOrNull<CubileteController>("CubileteAndDice")?.AppearAt(cubPos);
                _cubileteAtUserId = wUid;
            });
            itemGrants.Insert(0, cubiletGrant);
        }

        // Build a separate list for golden square rays (Ophanim introduces them first).
        var goldenGrants = new List<(Vector3, Action)>();
        var tableroForGolden = GetNodeOrNull<TableroController>("tablero");
        if (tableroForGolden != null)
        {
            foreach (int sq in _goldenSquares)
            {
                int capturedSq       = sq;
                Vector3 sqWorldPos   = tableroForGolden.GetBoardWorldPosition(capturedSq);
                var     capturedCtrl = tableroForGolden;
                goldenGrants.Add((sqWorldPos, () => capturedCtrl.MarkGoldenSquare(capturedSq)));
            }
        }

        // One ray per active player's shell set, fired just before the lives speech.
        var shellGrants = new List<(Vector3, Action)>();
        foreach (var itemSet in _itemSetsBySlot.Values)
        {
            var capturedSet = itemSet;
            shellGrants.Add((capturedSet.GetItemWorldPosition("shell"), () => capturedSet.SpawnShells()));
        }

        // One ray per board piece — fired after BEGIN announcement.
        var pieceGrants = new List<(Vector3, Action)>();
        var tableroForPieces = GetNodeOrNull<TableroController>("tablero");
        if (tableroForPieces != null)
        {
            foreach (var color in _activeTurnOrder)
            {
                for (int p = 0; p < 4; p++)
                {
                    var capturedColor = color;
                    var capturedP     = p;
                    var ficha         = tableroForPieces.GetPiece(color, p);
                    if (ficha == null) continue;
                    Vector3 piecePos = ficha.GlobalPosition;
                    pieceGrants.Add((piecePos, () => tableroForPieces.RevealFicha(capturedColor, capturedP)));
                }
            }
        }

        if (shots.Count > 0)
            OphanimNode.StartInitiativeSequence(shots.ToArray(), winnerName,
                itemGrants.Count > 0   ? itemGrants.ToArray()   : null,
                goldenGrants.Count > 0 ? goldenGrants.ToArray() : null,
                shellGrants.Count > 0  ? shellGrants.ToArray()  : null,
                pieceGrants.Count > 0  ? pieceGrants.ToArray()  : null);

        if (!OphanimNode.IsConnected(Ophanim.SignalName.InitiativeSequenceCompleted,
                Callable.From(OnInitiativeSequenceCompleted)))
            OphanimNode.InitiativeSequenceCompleted += OnInitiativeSequenceCompleted;
    }

    // ── Turn flow ─────────────────────────────────────────────────────────────

    private void OnTurnStart(JsonElement root)
    {
        int userId = root.TryGetProperty("user_id", out var tsUid) ? tsUid.GetInt32() : -1;
        if (userId < 0) return;

        _isMyTurn = (userId == LiveConnectionManager.LocalUserId);
        GD.Print($"[TM] turn_start user_id={userId} isMyTurn={_isMyTurn}");

        if (_initiativeInProgress) return;

        if (userId == _cubileteAtUserId)
        {
            // Doubles: cup stays in place but must be re-armed so the local player can grab it again.
            if (_isMyTurn)
                GetNodeOrNull<CubileteController>("CubileteAndDice")?.ReadyForRoll();
            return;
        }

        if (_colorByUserId.TryGetValue(userId, out var color))
        {
            var targetPos   = GetCubiletePositionForColor(color);
            var boardCenter = GetNodeOrNull<Node3D>("tablero")?.GlobalPosition ?? GlobalPosition;
            GetNodeOrNull<CubileteController>("CubileteAndDice")?.MoveToPlayer(targetPos, boardCenter);
            _cubileteAtUserId = userId;
        }
    }

    private void OnDiceResult(JsonElement root)
    {
        int    userId     = root.TryGetProperty("user_id",    out var drU)   ? drU.GetInt32()   : -1;
        int    die1       = root.TryGetProperty("die1",       out var dr1)   ? dr1.GetInt32()   : 0;
        int    die2       = root.TryGetProperty("die2",       out var dr2)   ? dr2.GetInt32()   : 0;
        int    total      = die1 + die2;
        bool   isMyRoll   = userId == LiveConnectionManager.LocalUserId;

        string who = _usernameByUserId.TryGetValue(userId, out var dName) ? dName : $"#{userId}";

        if (!isMyRoll)
        {
            ChatManager.AddLog($"[color=#aaaaff][DICE][/color] {who} rolled {die1}, {die2} (Total: {total})");
            GetNodeOrNull<CubileteController>("CubileteAndDice")?.PlayRemoteThrow(1.5f);
            return;
        }

        // Our roll — store pending dice and highlight moveable pieces.
        _pendingDie1 = die1;
        _pendingDie2 = die2;

        // Server already computed moveable_pieces; use that if present.
        var moveableFromServer = new HashSet<int>();
        if (root.TryGetProperty("moveable_pieces", out var mpEl))
            foreach (var el in mpEl.EnumerateArray())
                moveableFromServer.Add(el.GetInt32());

        // Also compute locally as a fallback/confirmation.
        string ourColor = _colorByUserId.TryGetValue(LiveConnectionManager.LocalUserId, out var oc)
            ? oc.ToLower() : "";

        List<int> localMoveable = ourColor.Length > 0
            ? ParchisLogic.GetMoveablePieces(ourColor, die1, die2, _boardPositions)
            : new List<int>();

        // Use server list if non-empty, else fallback to local.
        var moveable = moveableFromServer.Count > 0
            ? new List<int>(moveableFromServer)
            : localMoveable;

        HighlightMoveablePieces(ourColor, moveable);
    }

    private async void OnPieceMoved(JsonElement root)
    {
        int    userId   = root.TryGetProperty("user_id",  out var u)  ? u.GetInt32()  : -1;
        int    pieceId  = root.TryGetProperty("piece_id", out var pid) ? pid.GetInt32(): -1;
        int    from     = root.TryGetProperty("from",     out var f)   ? f.GetInt32()  : 0;
        int    to       = root.TryGetProperty("to",       out var t)   ? t.GetInt32()  : 0;

        if (userId < 0 || pieceId < 0) return;

        if (!_colorByUserId.TryGetValue(userId, out var color)) return;
        color = color.ToLower();
        int slot = ParchisLogic.ColorToSlot(color);
        if (slot >= 0 && slot < 4) _boardPositions[slot][pieceId] = to;

        bool wasMyMove = userId == LiveConnectionManager.LocalUserId;
        ClearSelectables();

        var tablero = GetNodeOrNull<TableroController>("tablero");
        if (tablero != null)
            await tablero.ApplyServerMove(color, pieceId, from, to);

        if (wasMyMove)
            FocusController.Instance?.ExitFocus();
    }

    private void OnCapture(JsonElement root)
    {
        int victimUserId  = root.TryGetProperty("victim_user_id",  out var vu) ? vu.GetInt32() : -1;
        int victimPieceId = root.TryGetProperty("victim_piece_id", out var vp) ? vp.GetInt32() : -1;
        if (victimUserId < 0 || victimPieceId < 0) return;

        if (!_colorByUserId.TryGetValue(victimUserId, out var victimColor)) return;
        victimColor = victimColor.ToLower();
        int slot = ParchisLogic.ColorToSlot(victimColor);
        if (slot >= 0 && slot < 4) _boardPositions[slot][victimPieceId] = 0;

        var tablero = GetNodeOrNull<TableroController>("tablero");
        tablero?.ReturnToBase(victimColor, victimPieceId);
        GD.Print($"[TM] capture: {victimColor} piece {victimPieceId} sent home");
    }

    private void OnGoalScored(JsonElement root)
    {
        int userId  = root.TryGetProperty("user_id",       out var u)   ? u.GetInt32()   : -1;
        int pieceId = root.TryGetProperty("piece_id",      out var pid) ? pid.GetInt32() : -1;
        int inGoal  = root.TryGetProperty("pieces_in_goal",out var ig)  ? ig.GetInt32()  : 0;
        GD.Print($"[TM] goal_scored: user {userId} piece {pieceId}, total in goal: {inGoal}");
    }

    private void OnBarrierFormed(JsonElement root)
    {
        int sq = root.TryGetProperty("square", out var s) ? s.GetInt32() : -1;
        GD.Print($"[TM] barrier_formed at square {sq}");
    }

    private void OnBarrierBroken(JsonElement root)
    {
        int sq = root.TryGetProperty("square", out var s) ? s.GetInt32() : -1;
        GD.Print($"[TM] barrier_broken at square {sq}");
    }

    private void OnExtraTurn(JsonElement root)
    {
        int    userId  = root.TryGetProperty("user_id", out var u)  ? u.GetInt32()  : -1;
        string reason  = root.TryGetProperty("reason",  out var r)  ? r.GetString() : "";
        int    pending = root.TryGetProperty("pending_movements", out var pm) ? pm.GetInt32() : 0;

        bool isUs = userId == LiveConnectionManager.LocalUserId;
        GD.Print($"[TM] extra_turn reason={reason} pending={pending} isUs={isUs}");

        if (isUs && pending > 0)
        {
            // Bonus move: highlight pieces that can move by pending_movements.
            string ourColor = _colorByUserId.TryGetValue(userId, out var oc) ? oc.ToLower() : "";
            if (ourColor.Length > 0)
            {
                // All on-board pieces are valid for bonus move (server validates actual target).
                int slot = ParchisLogic.ColorToSlot(ourColor);
                var valid = new List<int>();
                for (int p = 0; p < 4; p++)
                    if (_boardPositions[slot][p] > 0 && !ParchisLogic.IsGoal(_boardPositions[slot][p]))
                        valid.Add(p);
                HighlightMoveablePieces(ourColor, valid);
            }
        }
        // For "doubles" reason: turn_start fires again for same player → OnTurnStart handles it.
    }

    private void OnTurnEnd(JsonElement root)
    {
        ClearSelectables();
        _pendingDie1 = 0;
        _pendingDie2 = 0;
        _isMyTurn    = false;
    }

    private void OnGoldenSquareEvent(JsonElement root)
    {
        int    userId    = root.TryGetProperty("user_id",    out var u)  ? u.GetInt32()  : -1;
        string finalItem = root.TryGetProperty("final_item", out var fi) && fi.ValueKind == JsonValueKind.String
                         ? fi.GetString() : null;

        string who     = _usernameByUserId.TryGetValue(userId, out var n) ? n : $"#{userId}";
        string display = finalItem != null ? GoldenItemDisplayName(finalItem) : "nothing";

        ChatManager.AddLog($"[color=#ffd633][GOLDEN][/color] {who} got: {display}");
        GD.Print($"[TM] golden_square_event user={userId} final_item={finalItem}");
    }

    private static string GoldenItemDisplayName(string itemName) => itemName switch
    {
        "gun"              => "Gun",
        "cigarette"        => "Cigarette",
        "magnifying_glass" => "Magnifying Glass",
        "handcuffs"        => "Handcuffs",
        "fire_axe"         => "Fire Axe",
        _                  => itemName,
    };

    private void OnTripleDouble(JsonElement root)
    {
        int userId  = root.TryGetProperty("user_id",  out var u)   ? u.GetInt32()  : -1;
        int pieceId = root.TryGetProperty("piece_id", out var pid) ? pid.GetInt32(): -1;
        if (userId < 0 || pieceId < 0) return;
        if (!_colorByUserId.TryGetValue(userId, out var color)) return;
        color = color.ToLower();
        int slot = ParchisLogic.ColorToSlot(color);
        if (slot >= 0 && slot < 4) _boardPositions[slot][pieceId] = 0;
        GetNodeOrNull<TableroController>("tablero")?.ReturnToBase(color, pieceId);
        GD.Print($"[TM] triple_double_penalty: {color} piece {pieceId} sent home");
    }

    private void OnHandcuffSkip(JsonElement root)
    {
        int userId = root.TryGetProperty("user_id", out var u) ? u.GetInt32() : -1;
        string who = _usernameByUserId.TryGetValue(userId, out var n) ? n : $"#{userId}";
        GD.Print($"[TM] handcuff_skip: {who} loses their turn");
    }

    private void OnLifeLost(JsonElement root)
    {
        int userId = root.TryGetProperty("user_id", out var u)  ? u.GetInt32()  : -1;
        int lives  = root.TryGetProperty("lives_remaining", out var l) ? l.GetInt32() : 0;
        string who = _usernameByUserId.TryGetValue(userId, out var n) ? n : $"#{userId}";
        GD.Print($"[TM] life_lost: {who} now has {lives} lives");

        if (!_colorByUserId.TryGetValue(userId, out var color)) return;
        int slot = ColorToSlot(color.ToLower());
        if (slot >= 0 && _itemSetsBySlot.TryGetValue(slot, out var itemSet))
            itemSet.SetLives(lives);
    }

    private void OnGameOver(JsonElement root)
    {
        int winnerId = root.TryGetProperty("winner_user_id", out var w) ? w.GetInt32() : -1;
        string who   = _usernameByUserId.TryGetValue(winnerId, out var n) ? n : $"#{winnerId}";
        GD.Print($"[TM] game_over! Winner: {who}");
    }

    // ── Piece selection ───────────────────────────────────────────────────────

    private void HighlightMoveablePieces(string color, List<int> pieceIndices)
    {
        ClearSelectables();
        var tablero = GetNodeOrNull<TableroController>("tablero");
        if (tablero == null || color.Length == 0) return;

        foreach (int p in pieceIndices)
        {
            var ficha = tablero.GetPiece(color, p);
            if (ficha == null) continue;
            ficha.SetSelectable(true);
            ficha.PieceClicked += OnPieceClicked;
            _selectablePieces.Add(ficha);
        }
    }

    private void UpdatePieceHover()
    {
        if (_hoverCamera == null)
            _hoverCamera = GetViewport().GetCamera3D();
        if (_hoverCamera == null) return;

        var mousePos = GetViewport().GetMousePosition();
        var rayOrigin = _hoverCamera.ProjectRayOrigin(mousePos);
        var rayDir    = _hoverCamera.ProjectRayNormal(mousePos);

        FichaNode nearest  = null;
        float     nearestT = float.MaxValue;

        foreach (var piece in _selectablePieces)
        {
            if (!IsInstanceValid(piece)) continue;
            // Sphere intersection: center 5 cm above piece node origin, radius 5 cm.
            var   center  = piece.GlobalPosition + Vector3.Up * 0.05f;
            float t       = (center - rayOrigin).Dot(rayDir);
            if (t < 0f) continue;
            float distSq  = (rayOrigin + rayDir * t - center).LengthSquared();
            if (distSq < 0.05f * 0.05f && t < nearestT) { nearest = piece; nearestT = t; }
        }

        if (nearest == _hoveredPiece) return;

        if (_hoveredPiece != null && IsInstanceValid(_hoveredPiece))
            _hoveredPiece.SetHovered(false);

        GetNodeOrNull<TableroController>("tablero")?.HidePathPreview();

        _hoveredPiece = nearest;

        if (nearest != null)
        {
            nearest.SetHovered(true);
            ShowPiecePath(nearest);
        }
    }

    private void ShowPiecePath(FichaNode piece)
    {
        var tablero = GetNodeOrNull<TableroController>("tablero");
        if (tablero == null) return;

        string color = piece.PlayerColor.ToLower();
        int    from  = piece.BoardIndex;
        int    total = _pendingDie1 + _pendingDie2;

        int to;
        if (from <= 0)
        {
            TableroController.StartPositions.TryGetValue(color, out to);
        }
        else
        {
            to = ParchisLogic.Advance(color, from, total);
            if (to < 0) return;
        }

        var path = tablero.BuildPath(color, from, to);
        if (path.Count > 0)
            tablero.ShowPathPreview(path, color);
    }

    private void ClearSelectables()
    {
        if (_hoveredPiece != null)
        {
            if (IsInstanceValid(_hoveredPiece)) _hoveredPiece.SetHovered(false);
            _hoveredPiece = null;
        }
        GetNodeOrNull<TableroController>("tablero")?.HidePathPreview();

        foreach (var f in _selectablePieces)
        {
            if (!IsInstanceValid(f)) continue;
            f.SetSelectable(false);
            if (f.IsConnected(FichaNode.SignalName.PieceClicked,
                              Callable.From<FichaNode>(OnPieceClicked)))
                f.PieceClicked -= OnPieceClicked;
        }
        _selectablePieces.Clear();
    }

    private void OnPieceClicked(FichaNode ficha)
    {
        ClearSelectables(); // prevent double-send
        string moveJson = $"{{\"action\":\"move_piece\",\"piece_id\":{ficha.PieceIndex}}}";
        LiveConnectionManager.SendGameAction(
            LiveConnectionManager.CurrentMatchId,
            LiveConnectionManager.LocalUserId,
            moveJson);
        GD.Print($"[TM] move_piece piece_id={ficha.PieceIndex} ({ficha.PlayerColor})");
    }

    // ── Cubilete ──────────────────────────────────────────────────────────────

    private void OnInitiativeSequenceCompleted()
    {
        _initiativeInProgress = false;
        // Cubilete is already at the winner's position from the initiative ray.
    }

    private void OnRollCompleted(int die1, int die2)
    {
        string rollJson = $"{{\"action\":\"roll_dice\",\"die1\":{die1},\"die2\":{die2}}}";
        LiveConnectionManager.SendGameAction(
            LiveConnectionManager.CurrentMatchId,
            LiveConnectionManager.LocalUserId,
            rollJson);
        GD.Print($"[TM] roll_dice sent: {die1},{die2}");
        // Do NOT advance turn locally — wait for turn_start from server.
    }

    private Vector3 GetCubiletePositionForColor(string color)
    {
        var tablero = GetNodeOrNull<Node3D>("tablero");
        if (tablero == null) return GlobalPosition;

        var boardCenter = tablero.GlobalPosition;
        if (!_chairsByColor.TryGetValue(color, out var chair)) return boardCenter;

        var toChair = chair.GlobalPosition - boardCenter;
        toChair.Y = 0f;
        if (toChair.LengthSquared() < 0.001f) return boardCenter;
        toChair = toChair.Normalized();

        return new Vector3(
            boardCenter.X + toChair.X * _cubileteRadius,
            boardCenter.Y + _cubileteHeightOff,
            boardCenter.Z + toChair.Z * _cubileteRadius);
    }

    // ── Setup ─────────────────────────────────────────────────────────────────

    public void Setup(int playerCount)
    {
        playerCount = Mathf.Clamp(playerCount, 2, 4);

        var activeSlots    = SlotOrders[playerCount - 2];
        var activeColorSet = new HashSet<string>();
        foreach (int slot in activeSlots)
            activeColorSet.Add(SlotColorNames[slot].ToLower());

        _activeTurnOrder.Clear();
        foreach (var c in FullClockwiseOrder)
            if (activeColorSet.Contains(c)) _activeTurnOrder.Add(c);

        foreach (int slot in activeSlots)
        {
            var itemSet = SpawnItemSet(slot);
            SpawnChair(slot, itemSet);
        }

        Callable.From(SetupPiecesAndCubilete).CallDeferred();
    }

    private void SetupPiecesAndCubilete()
    {
        var tableroCtrl = GetNodeOrNull<TableroController>("tablero");
        if (tableroCtrl != null)
            foreach (var color in _activeTurnOrder)
                tableroCtrl.SpawnPlayer(color);

        var cubilete = GetNodeOrNull<CubileteController>("CubileteAndDice");
        var tablero  = GetNodeOrNull<Node3D>("tablero");
        if (cubilete != null && tablero != null)
        {
            var off = cubilete.GlobalPosition - tablero.GlobalPosition;
            _cubileteRadius    = new Vector2(off.X, off.Z).Length();
            _cubileteHeightOff = off.Y + cubilete.HiddenDepth;
        }

        if (cubilete != null && !_rollConnected)
        {
            cubilete.RollCompleted += OnRollCompleted;
            _rollConnected = true;
        }
    }

    private void SpawnSeatToken(Chair chair, string color, string username, int skinId)
    {
        if (_seatTokensByColor.TryGetValue(color, out var old) && IsInstanceValid(old))
            old.QueueFree();
        var token = new SeatToken();
        AddChild(token);
        token.GlobalPosition = chair.GlobalPosition;
        token.GlobalRotation = chair.GlobalRotation;
        token.SetPlayerInfo(username, color, skinId, chair);
        token.Appear();
        _seatTokensByColor[color] = token;
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
        {
            itemSetScript.SlotIndex = slot;
            _itemSetsBySlot[slot]   = itemSetScript;
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
