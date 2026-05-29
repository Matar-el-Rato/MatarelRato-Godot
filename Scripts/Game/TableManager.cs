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
    [Export] public PackedScene        ChairScene;
    [Export] public PackedScene        PlayerItemSetScene;
    [Export] public Ophanim            OphanimNode;
// Drag the ColorRect's ShaderMaterial here so raycast undistortion stays in sync with the shader.
    [Export] public ShaderMaterial     FisheyeMaterial;
    // Drag the room's AudioStreamPlayer3D node here so it can be crossfaded when the game starts.
    [Export] public AudioStreamPlayer3D RoomMusicPlayer;

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
    private bool          _initiativeInProgress = false;
    private DamoclesSword         _sword;
    private int                   _pendingSwordUserId  = -1;
    private bool                  _swordFalling        = false;
    private readonly Queue<string> _swordPendingJsons  = new();

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
    private int          _pendingBonusMoves; // extra moves from capture (+20) or goal (+10)
    private bool         _isMyTurn = false;
    private readonly List<FichaNode> _selectablePieces = new();

    // Hover tracking — driven by manual camera ray (physics picking blocked by tablero trimesh).
    private FichaNode _hoveredPiece = null;
    private Camera3D  _hoverCamera  = null;

    private System.Threading.Tasks.Task _lastMoveTask =
        System.Threading.Tasks.Task.CompletedTask;

    // Set on extra_turn/doubles so OnTurnStart skips the ReadyForRoll call (cup was already reset by ReturnDiceEarly).
    private bool _doublesAnimating     = false;
    // Updated from dice_result; passed to the reroll sound selector.
    private int  _consecutiveDoubles   = 0;
    // Plays the game music track; started on initiative_sequence and cleaned up on reset.
    private AudioStreamPlayer _gameMusicPlayer;

    // ── Item mechanics ────────────────────────────────────────────────────────
    // Local player's item script references (null until WireLocalItemEvents is called).
    private Handcuffs       _localHandcuffs;
    private Cigarette       _localCigarette;
    private FireAxe         _localFireAxe;
    private Gun             _localGun;
    private MagnifyingGlass _localMagnifyingGlass;
    // Maps handcuffed userId → Handcuffs instance currently on their head.
    private readonly Dictionary<int, Handcuffs> _activeHandcuffs = new();
    // Set by OnCigaretteResult; polled by the async OnSmokingComplete before showing HUD.
    private bool _cigaretteResultReady = false;

    // True only for the room whose match is currently running. PendingGameActions is a
    // single process-wide queue shared by all three rooms' TableManagers; without this
    // gate every TableManager would race to drain it and process each other's actions.
    // The local player is only ever in one match at a time, so exactly one is active.
    private bool _matchActive = false;

    // ── Lifecycle ─────────────────────────────────────────────────────────────

    public override void _Ready()
    {
        GD.Print($"[TM] _Ready (instance {GetInstanceId()})");
    }

    public void StartGame(int playerCount)
    {
        GD.Print($"[TM] StartGame playerCount={playerCount}");
        // Drop any stale actions left in the shared queue from a previous or aborted
        // match so this room starts clean. Our own match's actions arrive after this.
        while (LiveConnectionManager.PendingGameActions.TryDequeue(out _)) { }
        _matchActive = true;
        Setup(playerCount);
    }

    public void ResetGame()
    {
        _matchActive = false;
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
        _rollConnected        = false;
        _cubileteAtUserId     = -1;
        _initiativeInProgress = false;
        _doublesAnimating     = false;
        _sword?.Dismiss();

        if (_gameMusicPlayer != null && IsInstanceValid(_gameMusicPlayer))
        {
            var dying = _gameMusicPlayer;
            _gameMusicPlayer = null;
            var fadeOut = dying.CreateTween();
            fadeOut.TweenProperty(dying, "volume_db", -80.0f, 2.0f);
            fadeOut.Finished += () => { if (IsInstanceValid(dying)) dying.QueueFree(); };
        }
        _pendingSwordUserId   = -1;
        _lastMoveTask         = System.Threading.Tasks.Task.CompletedTask;
        _itemSetsBySlot.Clear();
        foreach (var row in _boardPositions)
            Array.Clear(row, 0, row.Length);
        ClearSelectables();
        FocusController.FocusStateChanged -= OnFireAxeFocusChanged;
        _localHandcuffs       = null;
        _localCigarette       = null;
        _localFireAxe         = null;
        _localGun             = null;
        _localMagnifyingGlass = null;
        _pendingGunTargetUserId = -1;
        _gunAwaitingGrab        = false;
        if (OphanimNode != null) OphanimNode.GunSkipRequested -= OnOphanimGunSkipRequested;
        OphanimNode?.HideGunDecisionPrompt();
        OphanimNode?.FullReset(); // ascend back to hidden, reset activation for next match
        _gunSwordOrigHangDistance = 0f;
        _gunHoveringPiece?.SetHovering(false);
        _gunHoveringPiece = null;
        _activeHandcuffs.Clear();
        _cigaretteResultReady = false;
    }

    public override void _Process(double delta)
    {
        // Only the active room's TableManager consumes the shared action queue.
        if (!_matchActive) return;

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

            // While the Damocles sword is falling, defer turn-progression events
            // so the animation plays out before the game state advances.
            if (_swordFalling)
            {
                switch (action)
                {
                    case "life_lost":
                    case "turn_end":
                    case "turn_start":
                    case "handcuff_skip":
                    case "player_eliminated":
                        _swordPendingJsons.Enqueue(json);
                        return;
                }
            }

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
                case "handcuffs_applied":    OnHandcuffsApplied(root);    break;
                case "gun_available":           OnGunAvailable(root);           break;
                case "gun_result":              OnGunResult(root);              break;
                case "magnifying_glass_result": OnMagnifyingGlassResult(root);  break;
                case "fire_axe_used":        OnFireAxeUsed(root);         break;
                case "cigarette_result":     OnCigaretteResult(root);     break;
                case "life_lost":            OnLifeLost(root);            break;
                case "player_eliminated":
                    // Create the TCS before the async animation starts so OnGameOver can await it.
                    {
                        int peUid = root.TryGetProperty("user_id", out var peu) ? peu.GetInt32() : -1;
                        if (peUid >= 0 && peUid != LiveConnectionManager.LocalUserId)
                            _remoteEliminationTcs = new System.Threading.Tasks.TaskCompletionSource<bool>(
                                System.Threading.Tasks.TaskCreationOptions.RunContinuationsAsynchronously);
                    }
                    OnPlayerEliminated(root);
                    break;
                case "game_over":            OnGameOver(root);            break;
                case "turn_timer_warning":   OnTimerWarning(root);        break;
                case "turn_timer_expired":   OnTimerExpired(root);        break;
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
            {
                _localChosenColor = color;
                // Wire item events now that we know our own slot.
                int localSlot = ColorToSlot(color.ToLower());
                if (localSlot >= 0 && _itemSetsBySlot.TryGetValue(localSlot, out var localSet))
                    WireLocalItemEvents(localSet);
            }

            if (color != _localChosenColor) {
                chair.SetTaken();
                SpawnSeatToken(chair, color, username, skinId, userId);
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

        if (_seatTokensByColor.TryGetValue(color, out var token) && IsInstanceValid(token))
        {
            token.Petrify(); // turn to stone; node stays in scene as a statue
            _seatTokensByColor.Remove(color);
        }

        // Petrify all pieces belonging to this player — grey briefly, then fire-disappear.
        var tablero = GetNodeOrNull<TableroController>("tablero");
        if (tablero != null)
        {
            for (int p = 0; p < 4; p++)
            {
                var ficha = tablero.GetPiece(color.ToLower(), p);
                if (ficha != null && IsInstanceValid(ficha))
                    ficha.Petrify();
            }
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

        StartGameMusic();

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

        // Cubilete appears after BEGIN alongside the pieces, not before items fire.

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

        // Cubilete appears with the pieces (after BEGIN), at the winner's position.
        if (winnerUserId >= 0 && _colorByUserId.TryGetValue(winnerUserId, out var winnerColor))
        {
            Vector3 cubPos = GetCubiletePositionForColor(winnerColor.ToLower());
            int     wUid   = winnerUserId;
            pieceGrants.Insert(0, (cubPos, () =>
            {
                GetNodeOrNull<CubileteController>("CubileteAndDice")?.AppearAt(cubPos);
                _cubileteAtUserId = wUid;
            }));
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

        GetNodeOrNull<CubileteController>("CubileteAndDice")?.SetInteractionEnabled(_isMyTurn);
        // Handcuffs + magnifying glass are available before rolling; cigarette only opens after dice_result.
        // Fire axe is gated inside EnableFireAxe — only activates if barriers exist on the board.
        if (_isMyTurn)
        {
            EnableHandcuffs(true);
            EnableMagnifyingGlass(true);
            EnableFireAxe(true);
        }

        if (_initiativeInProgress) return;

        if (OphanimNode != null)
        {
            float tableroY = GetNodeOrNull<Node3D>("tablero")?.GlobalPosition.Y ?? BoardCenter.Y;
            _sword?.SpawnFromOphanim(OphanimNode, tableroY);
        }

        if (userId == _cubileteAtUserId)
        {
            // Same player keeps the turn (doubles / 5+5 / extra roll). Always force the cup
            // back to a grabbable Stationary state. Previously this skipped ReadyForRoll when
            // _doublesAnimating was set and relied on ReturnDiceEarly — but after a physical
            // roll the cup mesh is hidden and in State.Moving, and if that animation hadn't
            // finished the cup stayed hidden/ungrabbable, so the player timed out unable to
            // roll. ReadyForRoll is idempotent and re-shows the cup, so it's safe to always run.
            if (_isMyTurn)
                GetNodeOrNull<CubileteController>("CubileteAndDice")?.ReadyForRoll();
            _doublesAnimating = false;
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

    private async void OnDiceResult(JsonElement root)
    {

        // Extract all data from root BEFORE any await (JsonElement lifetime is tied to JsonDocument).
        int  userId   = root.TryGetProperty("user_id",             out var drU) ? drU.GetInt32()   : -1;
        int  die1     = root.TryGetProperty("die1",                out var dr1) ? dr1.GetInt32()   : 0;
        int  die2     = root.TryGetProperty("die2",                out var dr2) ? dr2.GetInt32()   : 0;
        bool isDoubles= root.TryGetProperty("is_doubles",          out var idEl) && idEl.GetBoolean();
        int  consec   = root.TryGetProperty("consecutive_doubles", out var cdEl) ? cdEl.GetInt32() : 0;

        var moveableFromServer = new HashSet<int>();
        if (root.TryGetProperty("moveable_pieces", out var mpEl))
            foreach (var el in mpEl.EnumerateArray())
                moveableFromServer.Add(el.GetInt32());
        // root no longer accessed past this point.

        bool isMyRoll = userId == LiveConnectionManager.LocalUserId;
        if (isMyRoll) FocusController.IsBlocked = false; // dice landed — allow tablero focus
        if (isDoubles) _consecutiveDoubles = consec;

        if (!isMyRoll)
        {
            bool hasMoves = moveableFromServer.Count > 0;
            GetNodeOrNull<CubileteController>("CubileteAndDice")?.PlayRemoteThrow(1.5f, hasMoves);
            DiceHUD.ShowRemote(die1, die2);
            return;
        }

        // ── Our roll ──────────────────────────────────────────────────────────

        string ourColor = _colorByUserId.TryGetValue(LiveConnectionManager.LocalUserId, out var oc)
            ? oc.ToLower() : "";

        List<int> localMoveable = ourColor.Length > 0
            ? ParchisLogic.GetMoveablePieces(ourColor, die1, die2, _boardPositions)
            : new List<int>();
        var moveable = moveableFromServer.Count > 0
            ? new List<int>(moveableFromServer)
            : localMoveable;

        // die2 == 0 signals a second-move dice_result sent privately after a 5+X or 5+5 exit.
        // In that case, await the exit animation so the counter is already at the right value,
        // then just update pending dice and highlight — do NOT reset the counter.
        bool isSecondMove = die2 == 0;
        if (isSecondMove)
        {
            await _lastMoveTask;
            if (!IsInsideTree()) return;

            _pendingDie1       = die1;
            _pendingDie2       = 0;
            _pendingBonusMoves = 0;
            HighlightMoveablePieces(ourColor, moveable);
            if (moveable.Count == 0)
                DiceHUD.ShowNoMovesIcon();
            return;
        }

        // Normal first-roll for this turn.
        _pendingDie1       = die1;
        _pendingDie2       = die2;
        _pendingBonusMoves = 0;

        HighlightMoveablePieces(ourColor, moveable);

        // Enable the cigarette during this window (dice rolled, piece not yet moved).
        EnableCigarette(true);
        // Magnifying glass stays enabled only on doubles (can still peek at the next roll).
        if (!isDoubles)
            EnableMagnifyingGlass(false);

        if (moveable.Count > 0)
            MoveCounterHUD.Show(die1 + die2);
        else if (!isDoubles)
            DiceHUD.ShowNoMovesIcon();

        if (isDoubles && ourColor.Length > 0)
            GetNodeOrNull<CubileteController>("CubileteAndDice")?
                .ReturnDiceEarly(GetCubiletePositionForColor(ourColor));
    }

    private async void OnPieceMoved(JsonElement root)
    {
        int    userId   = root.TryGetProperty("user_id",  out var u)  ? u.GetInt32()  : -1;
        int    pieceId  = root.TryGetProperty("piece_id", out var pid) ? pid.GetInt32(): -1;
        int    from     = root.TryGetProperty("from",     out var f)   ? f.GetInt32()  : 0;
        int    to       = root.TryGetProperty("to",       out var t)   ? t.GetInt32()  : 0;
        int    steps    = root.TryGetProperty("steps",    out var st)  ? st.GetInt32() : 0;

        if (userId < 0 || pieceId < 0) return;

        if (!_colorByUserId.TryGetValue(userId, out var color)) return;
        color = color.ToLower();
        int slot = ParchisLogic.ColorToSlot(color);
        if (slot >= 0 && slot < 4) _boardPositions[slot][pieceId] = to;

        bool wasMyMove = userId == LiveConnectionManager.LocalUserId;
        ClearSelectables();

        var tablero = GetNodeOrNull<TableroController>("tablero");
        if (tablero != null)
        {
            var tcs = new System.Threading.Tasks.TaskCompletionSource<bool>();
            _lastMoveTask = tcs.Task;
            await tablero.ApplyServerMove(color, pieceId, from, to, wasMyMove, steps);
            tcs.SetResult(true);
        }

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

        // Hitmarker on every capture (broadcast event, so every client hears it).
        var hitClip = GD.Load<AudioStream>("res://Assets/Sound FX/hitmarker.wav");
        if (hitClip != null)
        {
            var sfx = new AudioStreamPlayer { Stream = hitClip, VolumeDb = -2f };
            AddChild(sfx);
            sfx.Play();
            sfx.Finished += () => sfx.QueueFree();
        }
    }

    private void OnGoalScored(JsonElement root)
    {
        int userId  = root.TryGetProperty("user_id",       out var u)   ? u.GetInt32()   : -1;
        int pieceId = root.TryGetProperty("piece_id",      out var pid) ? pid.GetInt32() : -1;
        int inGoal  = root.TryGetProperty("pieces_in_goal",out var ig)  ? ig.GetInt32()  : 0;
        GD.Print($"[TM] goal_scored: user {userId} piece {pieceId}, total in goal: {inGoal}");
    }

    private async void OnBarrierFormed(JsonElement root)
    {
        int sq     = root.TryGetProperty("square",  out var s) ? s.GetInt32() : -1;
        int userId = root.TryGetProperty("user_id", out var u) ? u.GetInt32() : -1;
        if (sq < 0) return;

        string color = userId >= 0 && _colorByUserId.TryGetValue(userId, out var c) ? c.ToLower() : "?";

        await _lastMoveTask;
        if (!IsInsideTree()) return;

        GetNodeOrNull<TableroController>("tablero")?.ApplyBarrierPositions(sq);
        GD.Print($"[TM] barrier_formed at square {sq} ({color})");

        // A new barrier may make the axe usable for the first time this turn.
        if (_isMyTurn) EnableFireAxe(true);
    }

    private async void OnBarrierBroken(JsonElement root)
    {
        int sq     = root.TryGetProperty("square",  out var s) ? s.GetInt32() : -1;
        int userId = root.TryGetProperty("user_id", out var u) ? u.GetInt32() : -1;
        if (sq < 0) return;

        string color = userId >= 0 && _colorByUserId.TryGetValue(userId, out var c) ? c.ToLower() : "?";

        await _lastMoveTask;
        if (!IsInsideTree()) return;

        GetNodeOrNull<TableroController>("tablero")?.CenterPieceAt(sq);
        GD.Print($"[TM] barrier_broken at square {sq} ({color})");

        // The last barrier may have just been destroyed — re-evaluate axe availability.
        if (_isMyTurn) EnableFireAxe(true);
    }

    private async void OnExtraTurn(JsonElement root)
    {
        int    userId  = root.TryGetProperty("user_id", out var u)  ? u.GetInt32()  : -1;
        string reason  = root.TryGetProperty("reason",  out var r)  ? r.GetString() : "";
        int    pending = root.TryGetProperty("pending_movements", out var pm) ? pm.GetInt32() : 0;

        bool isUs = userId == LiveConnectionManager.LocalUserId;

        if (reason == "doubles")
        {
            _doublesAnimating = true;
            // Wait for the piece to land before showing the reroll icon and returning focus.
            await _lastMoveTask;
            if (!IsInsideTree()) return;
            if (isUs)
            {
                DiceHUD.ShowRerollIcon(_consecutiveDoubles);
                FocusController.Instance?.ExitFocus();
            }
            else
            {
                DiceHUD.ShowRemoteRerollIcon(_consecutiveDoubles);
            }
            return;
        }

        if (isUs && pending > 0)
        {
            // Highlight immediately so the player sees selectable pieces.
            _pendingBonusMoves = pending;
            string ourColor = _colorByUserId.TryGetValue(userId, out var oc) ? oc.ToLower() : "";
            if (ourColor.Length > 0)
            {
                int slot = ParchisLogic.ColorToSlot(ourColor);
                var valid = new List<int>();
                for (int p = 0; p < 4; p++)
                    if (_boardPositions[slot][p] > 0 && !ParchisLogic.IsGoal(_boardPositions[slot][p]))
                        valid.Add(p);
                HighlightMoveablePieces(ourColor, valid);
            }
            // Wait for the current animation so leftover Step() callbacks don't corrupt the new counter value.
            await _lastMoveTask;
            if (!IsInsideTree()) return;
            MoveCounterHUD.Show(pending);
        }
    }

    private async void OnTurnEnd(JsonElement root)
    {
        _sword?.Dismiss();
        FocusController.IsBlocked = false; // safety: clear any stale block from this turn
        ClearSelectables();
        EnableCigarette(false);
        EnableMagnifyingGlass(false);
        FocusController.FocusStateChanged -= OnFireAxeFocusChanged;
        // Cancel any in-progress handcuff/fire-axe targeting and disable until next turn.
        // (?. only guards null — the node may be freed after the item is consumed.)
        if (IsInstanceValid(_localHandcuffs)) _localHandcuffs.CancelTargeting();
        if (IsInstanceValid(_localFireAxe))   _localFireAxe.CancelTargeting();
        EnableHandcuffs(false);
        EnableFireAxe(false);
        _pendingDie1       = 0;
        _pendingDie2       = 0;
        _pendingBonusMoves = 0;
        bool wasMyTurn     = _isMyTurn;
        _isMyTurn          = false;
        // Wait for any in-progress piece animation so the counter reaches 0 before fading.
        await _lastMoveTask;
        if (!IsInsideTree()) return;
        MoveCounterHUD.Hide();
        DiceHUD.HideResult();
        if (wasMyTurn)
            FocusController.Instance?.ExitFocus();
    }

    private async void OnGoldenSquareEvent(JsonElement root)
    {
        int    userId    = root.TryGetProperty("user_id",    out var u)  ? u.GetInt32()  : -1;
        string finalItem = root.TryGetProperty("final_item", out var fi) && fi.ValueKind == JsonValueKind.String
                         ? fi.GetString() : null;

        string who     = _usernameByUserId.TryGetValue(userId, out var n) ? n : $"#{userId}";
        string display = finalItem != null ? GoldenItemDisplayName(finalItem) : "nothing";
        GD.Print($"[TM] golden_square_event user={userId} final_item={finalItem}");

        // ── Wait for the piece to finish moving to the golden square ──────────
        await _lastMoveTask;
        if (!IsInsideTree()) return;

        // ── Play arrival sound ────────────────────────────────────────────────
        var comboClip = GD.Load<AudioStream>("res://Assets/Sound FX/combosound.wav");
        if (comboClip != null)
        {
            var sfx = new AudioStreamPlayer { Stream = comboClip, VolumeDb = -4f };
            AddChild(sfx);
            sfx.Play();
            sfx.Finished += () => sfx.QueueFree();
        }

        // ── Re-descend Ophanim + shorten rope so sword doesn't clip tablero ─────
        float descentAmount    = OphanimNode?.CurrentDescendOffset ?? 0f;
        float descentDuration  = OphanimNode?.GoldenDescentDuration ?? 1.5f;
        float origHangDistance = _sword?.HangDistance ?? 0f;

        OphanimNode?.ReDescend();

        if (_sword != null && _sword.IsInsideTree() && descentAmount > 0f)
        {
            var ropeTween = _sword.CreateTween();
            ropeTween.TweenMethod(
                Callable.From((float v) => _sword.HangDistance = v),
                origHangDistance, origHangDistance - descentAmount, descentDuration)
                .SetTrans(Tween.TransitionType.Cubic).SetEase(Tween.EaseType.Out);
        }

        float waitTime = descentDuration;
        await ToSignal(GetTree().CreateTimer(waitTime), SceneTreeTimer.SignalName.Timeout);
        if (!IsInsideTree()) return;

        // ── Ophanim taunts ────────────────────────────────────────────────────
        if (OphanimNode != null)
        {
            await OphanimNode.SayAsync(OphanimNode.GoldenLuckyMessage, 1.5f);
            if (!IsInsideTree()) return;
        }

        // ── Ophanim vibrates as if deciding ──────────────────────────────────
        if (OphanimNode != null)
        {
            await OphanimNode.VibrateDecidingAsync();
            if (!IsInsideTree()) return;
        }
        else
        {
            await ToSignal(GetTree().CreateTimer(1.0f), SceneTreeTimer.SignalName.Timeout);
            if (!IsInsideTree()) return;
        }

        // ── Build item grant ─────────────────────────────────────────────────
        _colorByUserId.TryGetValue(userId, out var gColor);
        int slot = gColor != null ? ColorToSlot(gColor.ToLower()) : -1;
        _itemSetsBySlot.TryGetValue(slot, out var itemSet);

        Vector3 itemWorldPos = itemSet != null && finalItem != null
            ? itemSet.GetItemWorldPosition(finalItem)
            : OphanimNode?.GlobalPosition ?? GlobalPosition;

        // SpawnItem eagerly enables interaction the instant the item is revealed. Re-gate
        // it to the CURRENT turn state in the same step, because by the time the item
        // appears the long grant animation has usually outlasted our turn (the cubilete
        // has already moved to the next player) — or this may be a remote player's grant.
        // Doing it inside onGrant (rather than after the animation) leaves no window where
        // the item is interactable out of turn.
        System.Action onGrant = finalItem != null && itemSet != null
            ? () =>
            {
                itemSet.SpawnItem(finalItem);
                if (userId == LiveConnectionManager.LocalUserId)
                {
                    if (finalItem == "handcuffs")           EnableHandcuffs(_isMyTurn);       // usable the whole turn, if it's still ours
                    else if (finalItem == "magnifying_glass") EnableMagnifyingGlass(_isMyTurn); // usable pre-roll, or after doubles
                    else if (finalItem == "fire_axe")         EnableFireAxe(_isMyTurn);         // enabled only if barriers exist (gated inside EnableFireAxe)
                    else if (finalItem == "cigarette")        EnableCigarette(false);           // only opens in the post-roll window
                }
            }
            : (System.Action)null;

        // ── Ophanim announces + shoots ray ───────────────────────────────────
        string msg = $"{who.ToUpper()} HAS BEEN GRANTED: {display.ToUpper()}.";
        ChatManager.AddLog($"[color=#ffd633][GOLDEN][/color] {who} got: {display}");

        if (OphanimNode != null)
            await OphanimNode.AnnounceGoldenGrant(msg, itemWorldPos, onGrant);
        else
            onGrant?.Invoke();

        // ── Restore rope length as Ophanim ascends back up ───────────────────
        if (_sword != null && _sword.IsInsideTree() && descentAmount > 0f)
        {
            var restoreTween = _sword.CreateTween();
            restoreTween.TweenMethod(
                Callable.From((float v) => _sword.HangDistance = v),
                _sword.HangDistance, origHangDistance, descentDuration)
                .SetTrans(Tween.TransitionType.Cubic).SetEase(Tween.EaseType.In);
        }
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
        int slot    = ParchisLogic.ColorToSlot(color);
        var tablero = GetNodeOrNull<TableroController>("tablero");

        // Fire a red ray from Ophanim toward the piece before sending it home.
        if (tablero != null && OphanimNode != null && slot >= 0 && slot < 4)
        {
            int currentSq = _boardPositions[slot][pieceId];
            if (currentSq > 0)
                OphanimNode.ShootLifeLostRay(tablero.GetBoardWorldPosition(currentSq, color));
        }

        if (slot >= 0 && slot < 4) _boardPositions[slot][pieceId] = 0;
        tablero?.ReturnToBase(color, pieceId);
        GD.Print($"[TM] triple_double_penalty: {color} piece {pieceId} sent home");
    }

    private void OnHandcuffSkip(JsonElement root)
    {
        int userId = root.TryGetProperty("user_id", out var u) ? u.GetInt32() : -1;
        string who = _usernameByUserId.TryGetValue(userId, out var n) ? n : $"#{userId}";
        GD.Print($"[TM] handcuff_skip: {who} loses their turn");

        // Ophanim taunts the skipped player — broadcast event, so every client sees it.
        // Hold the line long enough to register before the next turn starts.
        if (OphanimNode != null) _ = OphanimNode.SayAsync("TOO BAD...", 2.0f);

        if (_activeHandcuffs.TryGetValue(userId, out var handcuffs))
        {
            if (IsInstanceValid(handcuffs))
                handcuffs.BurnDisappear();
            _activeHandcuffs.Remove(userId);
        }
    }

    private void OnHandcuffsApplied(JsonElement root)
    {
        int attackerUserId = root.TryGetProperty("attacker_user_id", out var aEl) ? aEl.GetInt32() : -1;
        int targetUserId   = root.TryGetProperty("target_user_id",   out var tEl) ? tEl.GetInt32() : -1;
        if (attackerUserId < 0 || targetUserId < 0) return;

        // Resolve target's head world position. Remote players are represented by a
        // SeatToken on our client; the LOCAL player has no SeatToken (they are the
        // first-person camera), so derive the head pos from the camera in that case —
        // otherwise we'd silently bail out and the cuffs would never be shown on us.
        Vector3 headPos;
        if (targetUserId == LiveConnectionManager.LocalUserId)
        {
            var cam = GetViewport().GetCamera3D();
            if (cam == null) return;
            headPos = cam.GlobalPosition + Vector3.Up * 0.25f;
        }
        else
        {
            if (!_colorByUserId.TryGetValue(targetUserId, out var tColor)) return;
            if (!_seatTokensByColor.TryGetValue(tColor.ToLower(), out var targetToken)) return;
            if (!IsInstanceValid(targetToken)) return;
            headPos = targetToken.HeadWorldPosition;
        }

        if (attackerUserId == LiveConnectionManager.LocalUserId)
        {
            // Local player already placed their handcuffs optimistically in OnHandcuffsPlayerSelected.
            // Just register the tracking entry.
            if (_localHandcuffs != null)
                _activeHandcuffs[targetUserId] = _localHandcuffs;
            return;
        }

        // Remote attacker — fly their Handcuffs to the target's head.
        if (!_colorByUserId.TryGetValue(attackerUserId, out var aColor)) return;
        int aSlot = ColorToSlot(aColor.ToLower());
        if (!_itemSetsBySlot.TryGetValue(aSlot, out var aItemSet)) return;

        var handcuffs = aItemSet.GetNodeOrNull<Handcuffs>("Handcuffs");
        if (handcuffs == null) return;

        handcuffs.ApplyRemoteToHead(headPos);
        _activeHandcuffs[targetUserId] = handcuffs;
    }

    private void OnCigaretteResult(JsonElement root)
    {
        int userId = root.TryGetProperty("user_id", out var u) ? u.GetInt32() : -1;
        int die1   = root.TryGetProperty("die1",    out var d1) ? d1.GetInt32() : 0;
        int die2   = root.TryGetProperty("die2",    out var d2) ? d2.GetInt32() : 0;

        var moveableFromServer = new HashSet<int>();
        if (root.TryGetProperty("moveable_pieces", out var mpEl))
            foreach (var el in mpEl.EnumerateArray())
                moveableFromServer.Add(el.GetInt32());

        bool isMyReroll = userId == LiveConnectionManager.LocalUserId;
        GD.Print($"[TM] cigarette_result user={userId} die1={die1} die2={die2}");

        // Burn the cigarette for the player who used it (on all clients).
        if (userId >= 0 && _colorByUserId.TryGetValue(userId, out var color))
        {
            int slot = ColorToSlot(color.ToLower());
            if (_itemSetsBySlot.TryGetValue(slot, out var itemSet))
                itemSet.GetNodeOrNull<Cigarette>("CigaretteInteraction")?.BurnDisappear();
        }

        if (!isMyReroll)
        {
            DiceHUD.ShowRemote(die1, die2);
            return;
        }

        // Our reroll — update pending dice and re-highlight moveable pieces.
        // HUD is intentionally NOT shown here; OnSmokingComplete shows it after the vibration sequence.
        _pendingDie1       = die1;
        _pendingDie2       = die2;
        _pendingBonusMoves = 0;

        string ourColor = _colorByUserId.TryGetValue(LiveConnectionManager.LocalUserId, out var oc)
            ? oc.ToLower() : "";

        var moveable = moveableFromServer.Count > 0
            ? new System.Collections.Generic.List<int>(moveableFromServer)
            : (ourColor.Length > 0 ? ParchisLogic.GetMoveablePieces(ourColor, die1, die2, _boardPositions)
                                   : new System.Collections.Generic.List<int>());

        HighlightMoveablePieces(ourColor, moveable);

        // Signal the async OnSmokingComplete that the result is ready.
        _cigaretteResultReady = true;
    }

    private void OnLifeLost(JsonElement root)
    {
        int userId    = root.TryGetProperty("user_id",         out var u) ? u.GetInt32() : -1;
        int lives     = root.TryGetProperty("lives_remaining", out var l) ? l.GetInt32() : 0;
        string reason = root.TryGetProperty("reason",          out var r) ? r.GetString() : "";
        string who    = _usernameByUserId.TryGetValue(userId, out var n) ? n : $"#{userId}";

        GD.Print($"[TM] life_lost: {who} now has {lives} lives");

        if (!_colorByUserId.TryGetValue(userId, out var color)) return;
        int slot = ColorToSlot(color.ToLower());
        if (slot >= 0 && _itemSetsBySlot.TryGetValue(slot, out var itemSet))
            itemSet.SetLives(lives);

        // Apply camera/audio tension and hit vignette for the local player only.
        if (userId == LiveConnectionManager.LocalUserId)
        {
            PlayerCameraController.LocalInstance?.SetTension(lives);
            PlayerCameraController.LocalInstance?.PlayHitVignette();
        }
    }

    /// <summary>Fired after the local player's elimination animation finishes. RoomJoin subscribes to eject them.</summary>
    public static event System.Action LocalPlayerEliminated;

    /// <summary>Set by OnGameOver so RoomJoin can display it in chat after the respawn animation.</summary>
    public static string PendingWinnerMessage = "";

    /// <summary>
    /// Resolved when the remote-player elimination animation (DevourAsync) completes.
    /// Created in HandleGameAction before OnPlayerEliminated starts so OnGameOver can await it.
    /// </summary>
    private static System.Threading.Tasks.TaskCompletionSource<bool> _remoteEliminationTcs;

    private async void OnPlayerEliminated(JsonElement root)
    {
        int userId = root.TryGetProperty("user_id", out var u) ? u.GetInt32() : -1;
        if (userId < 0 || OphanimNode == null) return;

        string who = _usernameByUserId.TryGetValue(userId, out var n) ? n : $"#{userId}";
        GD.Print($"[TM] player_eliminated: {who}");

        // Wait for any in-progress move animation before starting the sequence.
        await _lastMoveTask;
        if (!IsInsideTree()) return;

        // Descend Ophanim close to the board (mirrors golden_square_event pattern).
        float descentAmount   = OphanimNode.CurrentDescendOffset;
        float descentDuration = OphanimNode.GoldenDescentDuration;
        float origHang        = _sword?.HangDistance ?? 0f;

        OphanimNode.ReDescend();

        if (_sword != null && _sword.IsInsideTree() && descentAmount > 0f)
        {
            var ropeTween = _sword.CreateTween();
            ropeTween.TweenMethod(
                Callable.From((float v) => _sword.HangDistance = v),
                origHang, origHang - descentAmount, descentDuration)
                .SetTrans(Tween.TransitionType.Cubic).SetEase(Tween.EaseType.Out);
        }

        await ToSignal(GetTree().CreateTimer(descentDuration), SceneTreeTimer.SignalName.Timeout);
        if (!IsInsideTree()) return;

        bool isLocal = userId == LiveConnectionManager.LocalUserId;

        await OphanimNode.SayAsync("Hmm...", 1.8f);
        if (!IsInsideTree()) return;

        // Let the speech bubble sit for 2 seconds so it's fully readable before
        // the devour sequence begins pulling the player toward Ophanim.
        await ToSignal(GetTree().CreateTimer(2.0f), SceneTreeTimer.SignalName.Timeout);
        if (!IsInsideTree()) return;

        // Collect targets: seated character model + all board pieces.
        var targets = new System.Collections.Generic.List<Node3D>();

        if (_colorByUserId.TryGetValue(userId, out var color))
        {
            if (_seatTokensByColor.TryGetValue(color, out var token) && IsInstanceValid(token))
                targets.Add(token);

            var tablero = GetNodeOrNull<TableroController>("tablero");
            if (tablero != null)
                for (int p = 0; p < 4; p++)
                {
                    var ficha = tablero.GetPiece(color.ToLower(), p);
                    if (ficha != null && IsInstanceValid(ficha) && ficha.Visible)
                        targets.Add(ficha);
                }
        }

        // Local player camera effect: fly toward Ophanim + fade to black, in
        // parallel with DevourAsync so the player sees themselves being consumed.
        CanvasLayer devourFade = null;
        Tween flyTween = null;
        if (isLocal)
        {
            devourFade = new CanvasLayer { Layer = 100 };
            var rect = new ColorRect { Color = new Color(0f, 0f, 0f, 0f) };
            rect.SetAnchorsAndOffsetsPreset(Control.LayoutPreset.FullRect);
            devourFade.AddChild(rect);
            GetTree().Root.AddChild(devourFade);

            var pcc = PlayerCameraController.LocalInstance;
            if (pcc != null)
            {
                flyTween = pcc.CreateTween().SetParallel(true);
                flyTween.TweenProperty(pcc, "global_position", OphanimNode.GlobalPosition, 1.3f)
                        .SetTrans(Tween.TransitionType.Cubic).SetEase(Tween.EaseType.In);
                flyTween.TweenProperty(rect, "color:a", 1.0f, 1.3f)
                        .SetTrans(Tween.TransitionType.Quad).SetEase(Tween.EaseType.In);
            }
            else
            {
                // No camera found — just fade directly.
                rect.CreateTween().TweenProperty(rect, "color:a", 1.0f, 0.5f);
            }
        }

        // Sound effects played at the start of the devour sequence.
        foreach (var sfxPath in new[] { "res://Assets/Sound FX/cracking.wav", "res://Assets/Sound FX/burnfx.wav" })
        {
            var clip = GD.Load<AudioStream>(sfxPath);
            if (clip != null)
            {
                var sfx = new AudioStreamPlayer { Stream = clip };
                AddChild(sfx);
                sfx.Play();
                sfx.Finished += () => { if (IsInstanceValid(sfx)) sfx.QueueFree(); };
            }
        }

        await OphanimNode.DevourAsync(targets);
        if (!IsInsideTree()) return;

        // Restore sword rope length after Ophanim ascends.
        if (_sword != null && _sword.IsInsideTree() && origHang > 0f)
        {
            var restoreTween = _sword.CreateTween();
            restoreTween.TweenMethod(
                Callable.From((float v) => _sword.HangDistance = v),
                _sword.HangDistance, origHang, descentDuration)
                .SetTrans(Tween.TransitionType.Cubic).SetEase(Tween.EaseType.Out);
        }

        if (!isLocal)
        {
            // Animation is done — show game-over message and signal OnGameOver to proceed.
            if (!string.IsNullOrEmpty(PendingWinnerMessage))
            {
                ChatManager.AddLog(PendingWinnerMessage);
                PendingWinnerMessage = "";
            }
            _remoteEliminationTcs?.TrySetResult(true);
            _remoteEliminationTcs = null;
        }

        if (isLocal)
        {
            flyTween?.Kill();

            // Teleport and reset the local player while the screen is still black.
            // We do this here in TableManager rather than relying on RoomJoin.EjectPlayer
            // so it is guaranteed to run at exactly the right moment in the animation.
            var pccLocal = PlayerCameraController.LocalInstance;
            if (pccLocal != null)
            {
                pccLocal.ForceUnsit();
                pccLocal.GlobalPosition = pccLocal.InitialSpawnPosition;
                pccLocal.Velocity       = Vector3.Zero;
                pccLocal.SetTension(3);
                pccLocal.PlayLookUpSequence(2.5f);
            }

            // Trigger room-state reset (StartFlickerOff, doors, game reset, etc.)
            LocalPlayerEliminated?.Invoke();

            await ToSignal(GetTree().CreateTimer(0.3f), SceneTreeTimer.SignalName.Timeout);
            if (!IsInsideTree()) return;

            if (devourFade != null && IsInstanceValid(devourFade))
            {
                var rect = devourFade.GetChildOrNull<ColorRect>(0);
                if (rect != null)
                {
                    var fadeOut = rect.CreateTween();
                    fadeOut.TweenProperty(rect, "color:a", 0.0f, 0.9f)
                           .SetTrans(Tween.TransitionType.Quad).SetEase(Tween.EaseType.Out);
                    fadeOut.Finished += () =>
                    {
                        if (IsInstanceValid(devourFade)) devourFade.QueueFree();
                    };
                }
            }
        }
    }

    private async void OnGameOver(JsonElement root)
    {
        int    winnerId = root.TryGetProperty("winner_user_id", out var w) ? w.GetInt32() : -1;
        string reason   = root.TryGetProperty("reason", out var r) ? r.GetString() : "race";
        string who      = _usernameByUserId.TryGetValue(winnerId, out var n) ? n : $"#{winnerId}";
        GD.Print($"[TM] game_over! Winner: {who} ({reason})");

        string msg = $"[color=#ffd633]> {who.ToUpper()} WINS![/color]";

        if (reason == "elimination")
            PendingWinnerMessage = msg; // shown by win sequence or EjectPlayer timer
        else
            ChatManager.AddLog(msg);   // race win: no animation pending

        // ── Win sequence for the local winner ─────────────────────────────────
        if (winnerId != LiveConnectionManager.LocalUserId || OphanimNode == null) return;

        // For elimination: await the TCS that OnPlayerEliminated resolves when DevourAsync ends.
        // For race: a brief pause lets the goal-piece animation settle.
        if (reason == "elimination")
        {
            var tcs = _remoteEliminationTcs;
            if (tcs != null)
                await tcs.Task;           // precise: resumes the moment the animation ends
            else
                await ToSignal(GetTree().CreateTimer(6.5f), SceneTreeTimer.SignalName.Timeout);
        }
        else
        {
            await ToSignal(GetTree().CreateTimer(1.0f), SceneTreeTimer.SignalName.Timeout);
        }
        if (!IsInsideTree()) return;

        // ── Fisheye starts now, as speech begins ──────────────────────────────
        // EaseIn means slow build during the bubble, then aggressive as the pull starts.
        // Duration covers speech (~3 s) + grace (2 s) + fly (1.5 s) ≈ 6.5 s total.
        if (FisheyeMaterial != null)
        {
            float fromK1   = FisheyeMaterial.GetShaderParameter("k1").As<float>();
            float fromZoom = FisheyeMaterial.GetShaderParameter("zoom").As<float>();
            var   st       = CreateTween().SetParallel(true);
            st.TweenMethod(
                Callable.From((float v) => FisheyeMaterial.SetShaderParameter("k1",   v)),
                Variant.From(fromK1), Variant.From(2.2f), 6.5f)
                .SetTrans(Tween.TransitionType.Expo).SetEase(Tween.EaseType.In);
            st.TweenMethod(
                Callable.From((float v) => FisheyeMaterial.SetShaderParameter("zoom", v)),
                Variant.From(fromZoom), Variant.From(3.2f), 6.5f)
                .SetTrans(Tween.TransitionType.Expo).SetEase(Tween.EaseType.In);
        }

        // ── Ophanim speaks first — player reads, then gets pulled in ─────────────
        await OphanimNode.SayAsync("Didn't think you'd make it this far", 2.0f);
        if (!IsInsideTree()) return;

        // Same 2-second grace period as the death sequence.
        await ToSignal(GetTree().CreateTimer(2.0f), SceneTreeTimer.SignalName.Timeout);
        if (!IsInsideTree()) return;

        // ── White overlay ─────────────────────────────────────────────────────
        var winFade = new CanvasLayer { Layer = 100 };
        var rect    = new ColorRect { Color = new Color(1f, 1f, 1f, 0f) };
        rect.SetAnchorsAndOffsetsPreset(Control.LayoutPreset.FullRect);
        winFade.AddChild(rect);
        GetTree().Root.AddChild(winFade);

        // ── Fly camera toward Ophanim ─────────────────────────────────────────
        var pcc = PlayerCameraController.LocalInstance;
        Tween flyTween = null;
        if (pcc != null)
        {
            flyTween = pcc.CreateTween().SetParallel(true);
            flyTween.TweenProperty(pcc, "global_position", OphanimNode.GlobalPosition, 1.5f)
                    .SetTrans(Tween.TransitionType.Cubic).SetEase(Tween.EaseType.In);
        }

        // ── Ophanim phrase + growing white light ──────────────────────────────
        // WinFlashAsync returns the light at peak WITHOUT freeing it so we can
        // keep it blazing all the way through the white-screen fade below.
        var winLight = await OphanimNode.WinFlashAsync(); // ~2 s say + 3.5 s light
        if (!IsInsideTree()) return;

        // ── Fade to white while the light is still at full intensity ──────────
        rect.CreateTween()
            .TweenProperty(rect, "color:a", 1.0f, 0.8f)
            .SetTrans(Tween.TransitionType.Quad).SetEase(Tween.EaseType.In);
        await ToSignal(GetTree().CreateTimer(0.8f), SceneTreeTimer.SignalName.Timeout);
        if (!IsInsideTree()) return;

        // Screen is now fully white — safe to kill the light.
        if (winLight != null && IsInstanceValid(winLight)) winLight.QueueFree();

        flyTween?.Kill();

        // ── Reset local player (same as death reset) ──────────────────────────
        if (pcc != null)
        {
            pcc.ForceUnsit();
            pcc.GlobalPosition = pcc.InitialSpawnPosition;
            pcc.Velocity       = Vector3.Zero;
            pcc.SetTension(3);
            pcc.PlayLookUpSequence(2.5f);
        }

        // Room reset (StartFlickerOff → ResetGame → FullReset Ophanim etc.)
        LocalPlayerEliminated?.Invoke();

        // Show winner message now that we're back in the street
        if (!string.IsNullOrEmpty(PendingWinnerMessage))
        {
            ChatManager.AddLog(PendingWinnerMessage);
            PendingWinnerMessage = "";
        }

        // ── Fade back from white ──────────────────────────────────────────────
        await ToSignal(GetTree().CreateTimer(0.3f), SceneTreeTimer.SignalName.Timeout);
        if (!IsInsideTree()) return;

        var fadeBack = rect.CreateTween();
        fadeBack.TweenProperty(rect, "color:a", 0.0f, 0.9f)
                .SetTrans(Tween.TransitionType.Quad).SetEase(Tween.EaseType.Out);
        fadeBack.Finished += () => { if (IsInstanceValid(winFade)) winFade.QueueFree(); };
    }

    private void OnTimerWarning(JsonElement root)
    {
        int remaining = root.TryGetProperty("seconds_remaining", out var sr) ? sr.GetInt32() : 0;
        _sword?.OnTimerWarning(remaining);
    }

    private void OnTimerExpired(JsonElement root)
    {
        _pendingSwordUserId = root.TryGetProperty("user_id", out var u) ? u.GetInt32() : -1;

        ClearSelectables();
        _isMyTurn = false;

        if (_sword != null && IsInstanceValid(_sword))
        {
            _swordFalling = true;
            _sword.Cut();
        }
        // If sword is null, _swordFalling stays false so turn_end/turn_start flow through immediately.
    }

    private async void OnSwordImpaled()
    {
        // 1. Fire red ray the moment the blade strikes.
        if (_pendingSwordUserId >= 0 && OphanimNode != null
            && _colorByUserId.TryGetValue(_pendingSwordUserId, out var color))
        {
            OphanimNode.ShootLifeLostRay(GetCubiletePositionForColor(color));
        }
        _pendingSwordUserId = -1;

        // 2. Flush life_lost immediately (updates HUD lives); hold everything else.
        // Call OnLifeLost directly — going through HandleGameAction would re-buffer
        // the message because _swordFalling is still true at this point.
        var deferred = new List<string>();
        while (_swordPendingJsons.TryDequeue(out var buffered))
        {
            using var d = JsonDocument.Parse(buffered);
            var  root = d.RootElement;
            string a = root.TryGetProperty("action", out var ae) ? ae.GetString() : "";
            if (a == "life_lost") OnLifeLost(root);
            else                  deferred.Add(buffered);
        }

        // 3. Blade stays stuck for 1 second.
        await ToSignal(GetTree().CreateTimer(1.0f), SceneTreeTimer.SignalName.Timeout);
        if (!IsInsideTree()) return;

        _sword?.Dismiss();
        _swordFalling = false;

        // 4. Replay turn-progression messages in arrival order.
        foreach (var buffered in deferred)
            HandleGameAction(buffered);

        // Also drain anything that arrived during the 1-second pause.
        while (_swordPendingJsons.TryDequeue(out var late))
            HandleGameAction(late);
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
        _hoverCamera = GetViewport().GetCamera3D();
        if (_hoverCamera == null) return;

        FichaNode hit = null;

        if (_selectablePieces.Count > 0)
        {
            var mousePos  = UndistortMousePos(GetViewport().GetMousePosition());
            var rayOrigin = _hoverCamera.ProjectRayOrigin(mousePos);
            var rayEnd    = rayOrigin + _hoverCamera.ProjectRayNormal(mousePos) * 12f;

            var query = PhysicsRayQueryParameters3D.Create(rayOrigin, rayEnd, FichaNode.HoverLayer);
            var result = GetWorld3D().DirectSpaceState.IntersectRay(query);

            if (result.Count > 0)
            {
                var parent = result["collider"].As<Node>()?.GetParent();
                if (parent is FichaNode fn && _selectablePieces.Contains(fn))
                    hit = fn;
            }
        }

        if (hit == _hoveredPiece) return;

        if (_hoveredPiece != null && IsInstanceValid(_hoveredPiece))
            _hoveredPiece.SetHovered(false);

        GetNodeOrNull<TableroController>("tablero")?.HidePathPreview();

        _hoveredPiece = hit;

        if (hit != null)
        {
            hit.SetHovered(true);
            ShowPiecePath(hit);
        }
    }

    public override void _UnhandledInput(InputEvent @event)
    {
        // While the gun prompt is active and the player hasn't grabbed it yet,
        // Escape skips the gun and takes the free capture instead.
        if (_gunAwaitingGrab
            && @event is InputEventKey escKey && escKey.Pressed && escKey.Keycode == Key.Escape)
        {
            GetViewport().SetInputAsHandled();
            SendGunSkip();
            return;
        }

        if (_hoveredPiece == null) return;
        if (FocusController.Instance?.IsFocused != true) return;
        if (@event is InputEventMouseButton mb && mb.ButtonIndex == MouseButton.Left && mb.Pressed)
            OnPieceClicked(_hoveredPiece);
    }

    // Applies the same forward transform as fisheye.gdshader so that raycasting
    // operates on undistorted coordinates matching what the player sees on screen.
    private Vector2 UndistortMousePos(Vector2 mousePos)
    {
        if (FisheyeMaterial == null) return mousePos;

        var vp     = GetViewport().GetVisibleRect().Size;
        float k1   = (float)FisheyeMaterial.GetShaderParameter("k1");
        float k2   = (float)FisheyeMaterial.GetShaderParameter("k2");
        float zoom = (float)FisheyeMaterial.GetShaderParameter("zoom");
        float aspect = vp.X / vp.Y;

        Vector2 uv = (mousePos / vp - Vector2.One * 0.5f) / zoom;
        uv.X *= aspect;

        float r2 = uv.Dot(uv);
        float r4  = r2 * r2;
        Vector2 d = uv * (1f + k1 * r2 + k2 * r4);

        d.X /= aspect;
        d   += Vector2.One * 0.5f;
        return d * vp;
    }

    private void ShowPiecePath(FichaNode piece)
    {
        var tablero = GetNodeOrNull<TableroController>("tablero");
        if (tablero == null) return;

        string color = piece.PlayerColor.ToLower();
        int    from  = piece.BoardIndex;
        int    total = _pendingBonusMoves > 0 ? _pendingBonusMoves : (_pendingDie1 + _pendingDie2);

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

        var path = tablero.BuildPath(color, from, to, total);
        if (path.Count > 0)
            tablero.ShowPathPreview(path, color, piece.GlobalPosition);
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
        EnableCigarette(false); // committed to a move — can't smoke anymore
        string moveJson = $"{{\"action\":\"move_piece\",\"piece_id\":{ficha.PieceIndex}}}";
        LiveConnectionManager.SendGameAction(
            LiveConnectionManager.CurrentMatchId,
            LiveConnectionManager.LocalUserId,
            moveJson);
        GD.Print($"[TM] move_piece piece_id={ficha.PieceIndex} ({ficha.PlayerColor})");
    }

    // ── Cubilete ──────────────────────────────────────────────────────────────

    private async void OnInitiativeSequenceCompleted()
    {
        _initiativeInProgress = false;
        if (OphanimNode == null) return;

        // Wait for Ophanim to finish ascending to its resting position before spawning
        // the sword — otherwise it spawns too low and immediately hits the board.
        await ToSignal(
            GetTree().CreateTimer(OphanimNode.DescentDuration),
            SceneTreeTimer.SignalName.Timeout);
        if (!IsInsideTree()) return;

        float tableroY = GetNodeOrNull<Node3D>("tablero")?.GlobalPosition.Y ?? BoardCenter.Y;
        _sword?.SpawnFromOphanim(OphanimNode, tableroY);
    }

    private void OnCubileteGrabbed()
    {
        EnableMagnifyingGlass(false);
    }

    private void OnRollCompleted(int die1, int die2)
    {
        // Block tablero focus until the server's dice_result arrives — prevents
        // the player from clicking pieces before knowing which ones are moveable.
        FocusController.IsBlocked = true;

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

        _sword = GetParent()?.GetNodeOrNull<DamoclesSword>("damocles");
        if (_sword != null)
        {
            _sword.Impaled -= OnSwordImpaled;
            _sword.Impaled += OnSwordImpaled;
        }

        if (cubilete != null && !_rollConnected)
        {
            cubilete.RollCompleted   += OnRollCompleted;
            cubilete.CubileteGrabbed += OnCubileteGrabbed;
            _rollConnected = true;
        }
    }

    private void SpawnSeatToken(Chair chair, string color, string username, int skinId, int userId)
    {
        if (_seatTokensByColor.TryGetValue(color, out var old) && IsInstanceValid(old))
            old.QueueFree();
        var token = new SeatToken();
        AddChild(token);
        token.GlobalPosition = chair.GlobalPosition;
        token.GlobalRotation = chair.GlobalRotation;
        token.SetUserId(userId);
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

    // ── Item event wiring ─────────────────────────────────────────────────────

    /// <summary>
    /// Called once when the local player's chair_taken event arrives.
    /// Caches item references and connects signals for local-player item mechanics.
    /// </summary>
    private void WireLocalItemEvents(PlayerItemSet itemSet)
    {
        _localHandcuffs       = itemSet.GetNodeOrNull<Handcuffs>("Handcuffs");
        _localCigarette       = itemSet.GetNodeOrNull<Cigarette>("CigaretteInteraction");
        _localFireAxe         = itemSet.GetNodeOrNull<FireAxe>("FireAxe");
        _localGun             = itemSet.GetNodeOrNull<Gun>("Gun");
        _localMagnifyingGlass = itemSet.GetNodeOrNull<MagnifyingGlass>("MagnifyingGlass");

        if (_localMagnifyingGlass != null)
        {
            _localMagnifyingGlass.Used += OnMagnifyingGlassUsed;
            _localMagnifyingGlass.SetInteractionEnabled(false);
        }

        if (_localGun != null)
        {
            _localGun.Grabbed        += OnGunGrabbed;
            _localGun.TargetSelected += OnGunTargetSelected;
            _localGun.Cancelled      += OnGunCancelled;
            _localGun.SetInteractionEnabled(false);
        }

        if (_localHandcuffs != null)
        {
            _localHandcuffs.Grabbed        += OnHandcuffsGrabbed;
            _localHandcuffs.PlayerSelected += OnHandcuffsPlayerSelected;
            // Gate: handcuffs disabled until it's our turn.
            _localHandcuffs.SetInteractionEnabled(false);
        }
        if (_localCigarette != null)
        {
            _localCigarette.SmokingComplete += OnSmokingComplete;
            // Gate: cigarette disabled until dice_result opens the window.
            _localCigarette.SetInteractionEnabled(false);
        }
        if (_localFireAxe != null)
        {
            _localFireAxe.Grabbed         += OnFireAxeGrabbed;
            _localFireAxe.BarrierSelected += OnFireAxeBarrierSelected;
            _localFireAxe.SetInteractionEnabled(false);
        }
    }

    private void OnHandcuffsGrabbed()
    {
        // Build the list of opponent SeatTokens to highlight.
        var targets = new System.Collections.Generic.List<SeatToken>();
        foreach (var kvp in _seatTokensByColor)
        {
            if (kvp.Key.ToLower() == _localChosenColor.ToLower()) continue;
            if (IsInstanceValid(kvp.Value)) targets.Add(kvp.Value);
        }
        _localHandcuffs?.BeginTargeting(targets);
    }

    private void OnHandcuffsPlayerSelected(SeatToken token)
    {
        if (token == null) return;
        int targetUserId = token.UserId;

        // Send server action.
        string json = $"{{\"action\":\"use_handcuffs\",\"target_user_id\":{targetUserId}}}";
        LiveConnectionManager.SendGameAction(
            LiveConnectionManager.CurrentMatchId,
            LiveConnectionManager.LocalUserId,
            json);
        GD.Print($"[TM] use_handcuffs → target_user_id={targetUserId}");

        // Optimistically fly handcuffs to target's head (server will confirm via handcuffs_applied).
        _localHandcuffs?.PlaceOnPlayerHead(token.HeadWorldPosition);

        // Track so we can BurnDisappear on handcuff_skip.
        if (_localHandcuffs != null)
            _activeHandcuffs[targetUserId] = _localHandcuffs;
    }

    private async void OnSmokingComplete()
    {
        if (!IsInsideTree()) return;

        // Disable the cigarette — it's been consumed.
        _localCigarette?.SetInteractionEnabled(false);

        // Hide HUDs and block piece interaction for the duration of the reroll sequence.
        ClearSelectables();
        MoveCounterHUD.Hide();
        DiceHUD.HideResult();
        FocusController.Instance?.ExitFocus();
        FocusController.IsBlocked = true;

        // Arc dice back into the cup so there's no stale physics dice on screen.
        var cubilete = GetNodeOrNull<CubileteController>("CubileteAndDice");
        Vector3 cupPos = Vector3.Zero;
        if (cubilete != null)
        {
            cupPos = GetCubiletePositionForColor(_localChosenColor.ToLower());
            cubilete.ReturnDiceForReroll(cupPos);
        }

        // Request a reroll from the server.
        _cigaretteResultReady = false;
        string json = "{\"action\":\"use_cigarette\"}";
        LiveConnectionManager.SendGameAction(
            LiveConnectionManager.CurrentMatchId,
            LiveConnectionManager.LocalUserId,
            json);
        GD.Print("[TM] use_cigarette sent");

        // Ophanim acknowledges the smoke with a line before re-rolling, so it doesn't feel abrupt.
        if (OphanimNode != null) await OphanimNode.SayAsync("FAIR ENOUGH.", 0.8f);
        if (!IsInsideTree()) return;

        // Wait for cigarette_result (up to 3 extra seconds as a safety net).
        float waitedExtra = 0f;
        while (!_cigaretteResultReady && waitedExtra < 3.0f)
        {
            await ToSignal(GetTree().CreateTimer(0.1f), SceneTreeTimer.SignalName.Timeout);
            if (!IsInsideTree()) return;
            waitedExtra += 0.1f;
        }

        // Shoot a small cream ray toward the cup to telegraph the result.
        if (OphanimNode != null && cupPos != Vector3.Zero)
            OphanimNode.ShootRerollRay(cupPos);

        // The dice change without a physical throw, so fake the clack of them landing.
        cubilete?.PlayThrowClacks();

        // Brief pause so the ray is visible before the HUD pops in.
        await ToSignal(GetTree().CreateTimer(0.2f), SceneTreeTimer.SignalName.Timeout);
        if (!IsInsideTree()) return;

        // Unblock input and show the updated HUD with the server's new roll.
        FocusController.IsBlocked = false;

        if (_pendingDie1 > 0)
        {
            DiceHUD.ShowStatic(_pendingDie1, _pendingDie2);
            if (_selectablePieces.Count > 0)
                MoveCounterHUD.Show(_pendingDie1 + _pendingDie2);
            else
                DiceHUD.ShowNoMovesIcon();
        }
    }

    private void EnableCigarette(bool enabled)
    {
        if (!IsInstanceValid(_localCigarette)) return;
        if (enabled && !_localCigarette.Visible) return;
        _localCigarette.SetInteractionEnabled(enabled);
    }

    private void EnableMagnifyingGlass(bool enabled)
    {
        if (!IsInstanceValid(_localMagnifyingGlass)) return;
        if (enabled && !_localMagnifyingGlass.Visible) return;
        _localMagnifyingGlass.SetInteractionEnabled(enabled);
    }

    // ── Magnifying glass handlers ─────────────────────────────────────────────

    private void OnMagnifyingGlassUsed()
    {
        EnableMagnifyingGlass(false);
        string json = "{\"action\":\"use_magnifying_glass\"}";
        LiveConnectionManager.SendGameAction(
            LiveConnectionManager.CurrentMatchId,
            LiveConnectionManager.LocalUserId,
            json);
        GD.Print("[TM] use_magnifying_glass sent");
    }

    private async void OnMagnifyingGlassResult(JsonElement root)
    {
        int die1 = root.TryGetProperty("die1", out var d1) ? d1.GetInt32() : 0;
        int die2 = root.TryGetProperty("die2", out var d2) ? d2.GetInt32() : 0;
        if (die1 < 1 || die2 < 1) return;

        GD.Print($"[TM] magnifying_glass_result die1={die1} die2={die2}");

        var cubilete = GetNodeOrNull<CubileteController>("CubileteAndDice");
        if (cubilete == null) return;

        // Store values — used to orient dice during the actual roll.
        cubilete.LockNextRollValues(die1, die2);

        // Place glass between the camera and the dice so the player looks through it.
        if (IsInstanceValid(_localMagnifyingGlass))
        {
            var camera      = GetViewport().GetCamera3D();
            Vector3 diceCenter = cubilete.GlobalPosition + Vector3.Up * 0.15f;
            Vector3 glassTarget = camera != null
                ? diceCenter.Lerp(camera.GlobalPosition, 0.38f) - Vector3.Up * 0.065f
                : diceCenter + Vector3.Up * 0.22f;

            var glassTween = _localMagnifyingGlass.CreateTween().SetParallel(true);
            glassTween.TweenProperty(_localMagnifyingGlass, "global_position", glassTarget, 0.4f)
                .SetTrans(Tween.TransitionType.Sine).SetEase(Tween.EaseType.Out);

            // Rotate lens toward camera. Model lens is along +X, so build a basis
            // with +X facing the camera rather than using LookingAt (which aligns -Z).
            if (camera != null)
            {
                var lookDir = (camera.GlobalPosition - glassTarget).Normalized();
                if (lookDir.LengthSquared() > 0.001f)
                {
                    var xAxis = lookDir;
                    var zAxis = lookDir.Cross(Vector3.Up);
                    if (zAxis.LengthSquared() < 0.001f)
                        zAxis = lookDir.Cross(Vector3.Right);
                    zAxis = zAxis.Normalized();
                    var yAxis = zAxis.Cross(xAxis).Normalized();
                    var targetRotation = new Basis(xAxis, yAxis, zAxis).GetEuler();
                    glassTween.TweenProperty(_localMagnifyingGlass, "global_rotation", targetRotation, 0.4f)
                        .SetTrans(Tween.TransitionType.Sine).SetEase(Tween.EaseType.Out);
                }
            }
        }

        // Dice pop up showing the peeked values; HUD confirms them.
        DiceHUD.ShowStatic(die1, die2);
        await cubilete.PeekDiceAsync(die1, die2);
        if (!IsInsideTree()) return;

        DiceHUD.HideResult();

        if (IsInstanceValid(_localMagnifyingGlass))
            _localMagnifyingGlass.BurnDisappear();
    }

    /// <summary>
    /// Enables/disables the local handcuffs, but only if the node is still visible (owned).
    /// </summary>
    private void EnableHandcuffs(bool enabled)
    {
        // The node is freed when the item is consumed, so guard against a stale
        // reference before touching it.
        if (!IsInstanceValid(_localHandcuffs)) return;
        if (enabled && !_localHandcuffs.Visible) return; // not granted yet
        _localHandcuffs.SetInteractionEnabled(enabled);
    }

    // ── Gun handlers ──────────────────────────────────────────────────────────

    // Stored from gun_available so OnGunGrabbed can find the target's SeatToken.
    private int      _pendingGunTargetUserId   = -1;
    // True between gun_available and the grab (or a skip), so Escape can skip.
    private bool     _gunAwaitingGrab          = false;
    // The attacker's piece that is hovering while the decision is pending.
    private FichaNode _gunHoveringPiece        = null;
    // Sword rope length captured in OnGunAvailable; restored in OnGunResult.
    private float    _gunSwordOrigHangDistance = 0f;

    /// <summary>
    /// Private message: local player landed on an enemy while holding the gun.
    /// Show a "!" exclamation on the gun and make it grabbable. The server will
    /// decide kill/misfire once the player fires.
    /// </summary>
    private async void OnGunAvailable(JsonElement root)
    {
        int targetUserId  = root.TryGetProperty("target_user_id",  out var tu) ? tu.GetInt32() : -1;
        int targetPieceId = root.TryGetProperty("target_piece_id", out var tp) ? tp.GetInt32() : -1;
        int square        = root.TryGetProperty("square",          out var sq) ? sq.GetInt32() : -1;

        _pendingGunTargetUserId = targetUserId;

        string targetName = _usernameByUserId.TryGetValue(targetUserId, out var n) ? n : $"#{targetUserId}";
        GD.Print($"[TM] gun_available: target={targetName} piece={targetPieceId} sq={square}");

        _gunAwaitingGrab = true;

        // Block other items during the decision — only the gun matters now.
        if (IsInstanceValid(_localHandcuffs)) _localHandcuffs.CancelTargeting();
        EnableHandcuffs(false);
        EnableFireAxe(false);

        if (_localGun != null && IsInstanceValid(_localGun))
            _localGun.SetAvailable(true);

        // Ophanim descends close to the board — shorten sword rope so it doesn't clip the table.
        float gunDescentAmount   = OphanimNode?.CurrentDescendOffset ?? 0f;
        float gunDescentDuration = OphanimNode?.GoldenDescentDuration ?? 1.5f;
        _gunSwordOrigHangDistance = _sword?.HangDistance ?? 0f;

        OphanimNode?.ReDescend();
        // Hold the bubble open indefinitely — dismissed when the decision resolves.
        _ = OphanimNode?.SayAsync("GRAB THE GUN AND GAMBLE 50/50... OR INTERACT WITH ME TO EAT FOR FREE.", 300f);

        if (OphanimNode != null)
        {
            OphanimNode.GunSkipRequested -= OnOphanimGunSkipRequested;
            OphanimNode.GunSkipRequested += OnOphanimGunSkipRequested;
            OphanimNode.ShowGunDecisionPrompt();
        }

        if (_sword != null && _sword.IsInsideTree() && gunDescentAmount > 0f)
        {
            var ropeTween = _sword.CreateTween();
            ropeTween.TweenMethod(
                Callable.From((float v) => _sword.HangDistance = v),
                _gunSwordOrigHangDistance, _gunSwordOrigHangDistance - gunDescentAmount, gunDescentDuration)
                .SetTrans(Tween.TransitionType.Cubic).SetEase(Tween.EaseType.Out);
        }

        // Wait for the landing move animation to finish before starting the hover,
        // otherwise the hover tween and the move tween fight over global_position.
        await _lastMoveTask;
        if (!IsInsideTree()) return;

        // Release any tablero focus so the player can aim at and interact with Ophanim.
        FocusController.Instance?.ExitFocus();

        var tablero = GetNodeOrNull<TableroController>("tablero");
        _gunHoveringPiece = null;
        if (tablero != null && _colorByUserId.TryGetValue(LiveConnectionManager.LocalUserId, out var myCol))
        {
            for (int p = 0; p < 4; p++)
            {
                var ficha = tablero.GetPiece(myCol.ToLower(), p);
                if (ficha != null && IsInstanceValid(ficha) && ficha.BoardIndex == square)
                {
                    _gunHoveringPiece = ficha;
                    ficha.SetHovering(true);
                    break;
                }
            }
        }
    }

    private void OnGunGrabbed()
    {
        _gunAwaitingGrab = false;
        if (_gunHoveringPiece != null && IsInstanceValid(_gunHoveringPiece))
            _gunHoveringPiece.SetHovering(false);
        _gunHoveringPiece = null;
        // Gun grabbed — hide Ophanim prompt (Interactor.IsLocked prevents interaction anyway).
        if (OphanimNode != null) OphanimNode.GunSkipRequested -= OnOphanimGunSkipRequested;
        OphanimNode?.HideGunDecisionPrompt();
        // Player picked up the gun — begin targeting the opponent that was landed on.
        int targetUserId = _pendingGunTargetUserId;
        _pendingGunTargetUserId = -1;

        var targets = new System.Collections.Generic.List<SeatToken>();
        if (targetUserId >= 0 && _colorByUserId.TryGetValue(targetUserId, out var tColor)
            && _seatTokensByColor.TryGetValue(tColor.ToLower(), out var token)
            && IsInstanceValid(token))
        {
            targets.Add(token);
        }

        _localGun?.BeginTargeting(targets);
    }

    private void OnGunTargetSelected(SeatToken token)
    {
        // Player fired — gun animation already played locally. Send to server.
        string json = "{\"action\":\"use_gun\",\"choice\":\"fire\"}";
        LiveConnectionManager.SendGameAction(
            LiveConnectionManager.CurrentMatchId,
            LiveConnectionManager.LocalUserId,
            json);
        GD.Print("[TM] use_gun → fire");
    }

    private void OnGunCancelled()
    {
        _gunAwaitingGrab = false;
        SendGunSkip();
    }

    private void OnOphanimGunSkipRequested() => SendGunSkip();

    private void SendGunSkip()
    {
        _gunAwaitingGrab        = false;
        _pendingGunTargetUserId = -1;
        if (_gunHoveringPiece != null && IsInstanceValid(_gunHoveringPiece))
            _gunHoveringPiece.SetHovering(false);
        _gunHoveringPiece = null;
        if (_localGun != null && IsInstanceValid(_localGun))
            _localGun.SetAvailable(false);
        if (OphanimNode != null) OphanimNode.GunSkipRequested -= OnOphanimGunSkipRequested;
        OphanimNode?.HideGunDecisionPrompt();
        OphanimNode?.DismissBubble();
        if (_isMyTurn) { EnableHandcuffs(true); EnableFireAxe(true); }
        string json = "{\"action\":\"use_gun\",\"choice\":\"skip\"}";
        LiveConnectionManager.SendGameAction(
            LiveConnectionManager.CurrentMatchId,
            LiveConnectionManager.LocalUserId,
            json);
        GD.Print("[TM] use_gun → skip");
    }

    /// <summary>
    /// Broadcast: gun was fired (kill or misfire). For the local player the gun already
    /// animated (optimistic); for remote players play FireInitiativeShot then burn.
    /// </summary>
    private async void OnGunResult(JsonElement root)
    {
        int attackerUserId  = root.TryGetProperty("attacker_user_id",  out var aEl) ? aEl.GetInt32() : -1;
        int attackerPieceId = root.TryGetProperty("attacker_piece_id", out var apEl) ? apEl.GetInt32() : -1;
        int targetUserId    = root.TryGetProperty("target_user_id",    out var tEl) ? tEl.GetInt32() : -1;
        string result       = root.TryGetProperty("result",            out var rEl) ? rEl.GetString() : "";
        bool kill           = result == "kill";

        _gunAwaitingGrab  = false;
        _gunHoveringPiece?.SetHovering(false);
        _gunHoveringPiece = null;
        GD.Print($"[TM] gun_result attacker={attackerUserId} result={result}");

        if (OphanimNode != null) OphanimNode.GunSkipRequested -= OnOphanimGunSkipRequested;
        OphanimNode?.HideGunDecisionPrompt();

        bool isLocalAttacker = attackerUserId == LiveConnectionManager.LocalUserId;
        if (isLocalAttacker) { EnableHandcuffs(_isMyTurn); EnableFireAxe(_isMyTurn); }

        // ── Misfire: send the attacker's piece back to base ───────────────────
        if (!kill && attackerPieceId >= 0 && attackerUserId >= 0)
        {
            if (_colorByUserId.TryGetValue(attackerUserId, out var aCol))
            {
                aCol = aCol.ToLower();
                int aSlot = ParchisLogic.ColorToSlot(aCol);
                if (aSlot >= 0 && aSlot < 4) _boardPositions[aSlot][attackerPieceId] = 0;
                var tablero = GetNodeOrNull<TableroController>("tablero");
                if (tablero != null)
                {
                    await _lastMoveTask;
                    if (!IsInsideTree()) return;
                    tablero.ReturnToBase(aCol, attackerPieceId);
                }
            }
        }

        // ── Find and play gun animation ───────────────────────────────────────
        Gun gun = null;
        if (isLocalAttacker)
            gun = _localGun;
        else if (_colorByUserId.TryGetValue(attackerUserId, out var gunColor))
        {
            int aSlot = ColorToSlot(gunColor.ToLower());
            if (_itemSetsBySlot.TryGetValue(aSlot, out var aItemSet))
                gun = aItemSet.GetNodeOrNull<Gun>("Gun");
        }

        if (gun != null && IsInstanceValid(gun))
        {
            gun.FireInitiativeShot(kill);
            GetTree().CreateTimer(0.65f).Timeout += () => { if (IsInstanceValid(gun)) gun.BurnDisappear(); };
        }

        // ── Kill extras: Ophanim ray + hitmarker ─────────────────────────────
        if (kill)
        {
            if (OphanimNode != null && targetUserId >= 0
                && _colorByUserId.TryGetValue(targetUserId, out var tColor))
            {
                Vector3 rayTarget = _seatTokensByColor.TryGetValue(tColor.ToLower(), out var tok)
                                    && IsInstanceValid(tok)
                    ? tok.HeadWorldPosition
                    : GetCubiletePositionForColor(tColor.ToLower());
                OphanimNode.ShootLifeLostRay(rayTarget);
            }

            var hitClip = GD.Load<AudioStream>("res://Assets/Sound FX/hitmarker.wav");
            if (hitClip != null)
            {
                var sfx = new AudioStreamPlayer { Stream = hitClip, VolumeDb = -2f };
                AddChild(sfx);
                sfx.Play();
                sfx.Finished += () => sfx.QueueFree();
            }
        }

        // ── Ophanim reacts then ascends; sword rope restores in sync ─────────────
        if (OphanimNode != null)
        {
            await OphanimNode.SayAsync(kill ? "GOOD..." : "HAPPENS TO THE BEST OF US", 2.0f);
            if (!IsInsideTree()) return;
            OphanimNode.AscendAndHide();
        }

        if (_sword != null && _sword.IsInsideTree() && _gunSwordOrigHangDistance > 0f)
        {
            var restoreTween = _sword.CreateTween();
            restoreTween.TweenMethod(
                Callable.From((float v) => _sword.HangDistance = v),
                _sword.HangDistance, _gunSwordOrigHangDistance, OphanimNode?.GoldenDescentDuration ?? 1.5f)
                .SetTrans(Tween.TransitionType.Cubic).SetEase(Tween.EaseType.In);
        }
        _gunSwordOrigHangDistance = 0f;
    }

    /// <summary>
    /// Enables the local fire axe only when there are actual breakable barriers.
    /// If disabling, always applies regardless of board state.
    /// </summary>
    private void EnableFireAxe(bool enabled)
    {
        if (!IsInstanceValid(_localFireAxe)) return;
        if (enabled && !_localFireAxe.Visible) return;
        if (enabled)
        {
            var tablero = GetNodeOrNull<TableroController>("tablero");
            enabled = tablero != null && tablero.GetAxeableBarriers().Count > 0;
        }
        _localFireAxe.SetInteractionEnabled(enabled);
    }

    private void OnFireAxeGrabbed()
    {
        // Don't call BeginTargeting immediately — wait for the player to click the
        // tablero to enter focus. FocusController fires FocusStateChanged which we listen to.
        FocusController.FocusStateChanged -= OnFireAxeFocusChanged;
        FocusController.FocusStateChanged += OnFireAxeFocusChanged;

        // Edge case: tablero was already focused when the axe was grabbed.
        if (FocusController.Instance?.IsFocused == true)
            OnFireAxeFocusChanged(true);
    }

    private void OnFireAxeFocusChanged(bool focused)
    {
        if (!IsInstanceValid(_localFireAxe))
        {
            FocusController.FocusStateChanged -= OnFireAxeFocusChanged;
            return;
        }

        if (focused)
        {
            var tablero = GetNodeOrNull<TableroController>("tablero");
            if (tablero == null) return;

            // Recalculate barriers at the moment the player enters focus (freshest data).
            var squares = tablero.GetAxeableBarriers();
            if (squares.Count == 0)
            {
                _ = OphanimNode?.SayAsync("NOTHING TO BREAK.", 1.2f);
                FocusController.Instance?.ExitFocus();
                return;
            }
            _localFireAxe.BeginTargeting(tablero, squares);
        }
        else
        {
            // Focus exited (Escape / right-click) — return axe to camera hand.
            _localFireAxe.ReturnToHand();
        }
    }

    private void OnFireAxeBarrierSelected(int square)
    {
        // Unsubscribe now — FireAxe will call ExitFocus from ResetToFresh after the
        // full strike + burn animation, so we don't want FocusStateChanged to fire
        // OnFireAxeFocusChanged(false) in the middle of the animation.
        FocusController.FocusStateChanged -= OnFireAxeFocusChanged;

        var tablero = GetNodeOrNull<TableroController>("tablero");
        if (tablero == null || _localFireAxe == null) return;

        // Send the action — server will validate and broadcast fire_axe_used.
        string json = $"{{\"action\":\"use_fire_axe\",\"target_square\":{square}}}";
        LiveConnectionManager.SendGameAction(
            LiveConnectionManager.CurrentMatchId,
            LiveConnectionManager.LocalUserId,
            json);
        GD.Print($"[TM] use_fire_axe → target_square={square}");

        // Optimistically swing onto the barrier; the fire_axe_used broadcast confirms.
        Vector3 impactPos = tablero.GetBoardWorldPosition(square);
        _localFireAxe.StrikeBarrier(impactPos);
    }

    private async void OnFireAxeUsed(JsonElement root)
    {
        int attackerUserId = root.TryGetProperty("attacker_user_id", out var aEl) ? aEl.GetInt32() : -1;
        int targetSquare   = root.TryGetProperty("target_square",    out var sEl) ? sEl.GetInt32() : -1;
        if (targetSquare < 1) return;

        int    fwdUser = -1, fwdPiece = -1, fwdTo = -1;
        int    bwdUser = -1, bwdPiece = -1, bwdTo = -1;
        if (root.TryGetProperty("forward", out var fEl) && fEl.ValueKind == JsonValueKind.Object)
        {
            if (fEl.TryGetProperty("user_id",   out var fu)) fwdUser  = fu.GetInt32();
            if (fEl.TryGetProperty("piece_id",  out var fp)) fwdPiece = fp.GetInt32();
            if (fEl.TryGetProperty("to_square", out var ft)) fwdTo    = ft.GetInt32();
        }
        if (root.TryGetProperty("backward", out var bEl) && bEl.ValueKind == JsonValueKind.Object)
        {
            if (bEl.TryGetProperty("user_id",   out var bu)) bwdUser  = bu.GetInt32();
            if (bEl.TryGetProperty("piece_id",  out var bp)) bwdPiece = bp.GetInt32();
            if (bEl.TryGetProperty("to_square", out var bt)) bwdTo    = bt.GetInt32();
        }
        if (fwdUser < 0 || bwdUser < 0 || fwdPiece < 0 || bwdPiece < 0 || fwdTo < 0 || bwdTo < 0)
            return;

        _colorByUserId.TryGetValue(fwdUser, out var fwdColor);
        _colorByUserId.TryGetValue(bwdUser, out var bwdColor);
        if (fwdColor == null || bwdColor == null) return;
        fwdColor = fwdColor.ToLower();
        bwdColor = bwdColor.ToLower();

        // Update our local board state first so subsequent barrier checks see the new positions.
        int fwdSlot = ParchisLogic.ColorToSlot(fwdColor);
        int bwdSlot = ParchisLogic.ColorToSlot(bwdColor);
        if (fwdSlot >= 0 && fwdSlot < 4) _boardPositions[fwdSlot][fwdPiece] = fwdTo;
        if (bwdSlot >= 0 && bwdSlot < 4) _boardPositions[bwdSlot][bwdPiece] = bwdTo;

        var tablero = GetNodeOrNull<TableroController>("tablero");
        if (tablero == null) return;

        // Remote attacker: descend a copy of their axe onto the barrier so everyone sees the hit.
        if (attackerUserId != LiveConnectionManager.LocalUserId)
        {
            if (_colorByUserId.TryGetValue(attackerUserId, out var aColor))
            {
                int aSlot = ColorToSlot(aColor.ToLower());
                if (_itemSetsBySlot.TryGetValue(aSlot, out var aItemSet))
                {
                    var axe = aItemSet.GetNodeOrNull<FireAxe>("FireAxe");
                    axe?.ApplyRemoteStrike(tablero.GetBoardWorldPosition(targetSquare));
                }
            }
        }

        // Slight delay so the impact reads visually before the pieces split apart.
        await ToSignal(GetTree().CreateTimer(0.25f), SceneTreeTimer.SignalName.Timeout);
        if (!IsInsideTree()) return;

        await tablero.ApplyFireAxeSplit(fwdColor, fwdPiece, fwdTo, bwdColor, bwdPiece, bwdTo);
    }

    // ── Music crossfade ───────────────────────────────────────────────────────

    private void StartGameMusic()
    {
        if (_gameMusicPlayer != null && IsInstanceValid(_gameMusicPlayer)) return;

        const float CrossfadeDuration = 10.0f;
        const float GameMusicVolume   = -16.0f;

        if (RoomMusicPlayer != null && IsInstanceValid(RoomMusicPlayer))
        {
            RoomMusicPlayer.CreateTween()
                .TweenProperty(RoomMusicPlayer, "volume_db", -80.0f, CrossfadeDuration)
                .SetTrans(Tween.TransitionType.Cubic).SetEase(Tween.EaseType.InOut);
        }

        var stream = GD.Load<AudioStreamMP3>("res://Assets/Music/Buckshot_Roulette_OST.mp3");
        if (stream == null) return;

        var looped = (AudioStreamMP3)stream.Duplicate();
        looped.Loop = true;

        _gameMusicPlayer          = new AudioStreamPlayer();
        _gameMusicPlayer.Stream   = looped;
        _gameMusicPlayer.VolumeDb = -80.0f;
        _gameMusicPlayer.Bus      = "Master";
        AddChild(_gameMusicPlayer);
        _gameMusicPlayer.Play();

        _gameMusicPlayer.CreateTween()
            .TweenProperty(_gameMusicPlayer, "volume_db", GameMusicVolume, CrossfadeDuration)
            .SetTrans(Tween.TransitionType.Cubic).SetEase(Tween.EaseType.InOut);
    }
}
