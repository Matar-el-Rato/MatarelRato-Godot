// ═══════════════════════════════════════════════════
// ConnectedPlayersBoard.cs
// 3D board near the Pig NPC showing the live list of connected
// players pushed by the server. Always visible so it can be
// positioned in the editor; columns are empty until the first
// MSG_USER_LIST arrives after login.
//
// The list is split into four Label3D columns (16 players each)
// so up to 64 names (MAX_CLIENTS) can be displayed across the
// 2.5 m-wide board without overflowing.
//
// SUBSCRIPTION TIMING:
// The board node may be removed from the scene tree during the
// intro DitherEffect / ground-camera sweep — before
// AuthManager.NotifySuccess() calls LiveConnectionManager.Connect().
// Subscribing in _EnterTree() (not _Ready()) ensures the subscription
// is restored every time the node re-enters the tree. _Ready() then
// applies LiveConnectionManager.LastKnownPlayers to catch up on any
// broadcast that arrived while the board was absent.
//
// All Godot node mutations are marshalled to the main thread via
// ConcurrentQueue<Action> drained in _Process() — the same pattern
// used by ClipboardUI.cs — because OnUserListUpdated fires on the
// background LiveConnection listener thread.
// ═══════════════════════════════════════════════════
using Godot;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;

/// <summary>
/// Node3D that displays the live connected-players list pushed by the server.
/// Place it in the scene near the NPC. The board is always visible; player
/// names appear automatically once the local player authenticates and the
/// server starts broadcasting MSG_USER_LIST packets.
/// </summary>
public partial class ConnectedPlayersBoard : Node3D
{
    // ── Node references ───────────────────────────────────────────────────────
    private Label3D _col1Label;
    private Label3D _col2Label;
    private Label3D _col3Label;
    private Label3D _col4Label;
    private Label3D _counterLabel;

    // ── Thread-safe update queue ──────────────────────────────────────────────
    //
    // MUTUAL EXCLUSION:
    // OnUserListUpdated fires on the background LiveConnection listener thread.
    // Godot's scene tree is not thread-safe: calling Label3D.Text from a
    // background thread risks crashes or silent corruption. We enqueue lambdas
    // here and drain them in _Process(), which always runs on the Godot main
    // thread. ConcurrentQueue<T> is safe for concurrent Enqueue (background
    // thread) and TryDequeue (main thread) without additional locking.
    private readonly ConcurrentQueue<Action> _mainThreadQueue = new();

    // Last received player list, kept for the /players chat command.
    // Written only from within an enqueued action (main thread), so no lock needed.
    private List<string> _currentPlayers = new();

    /// <summary>Singleton reference used by the /players chat command.</summary>
    public static ConnectedPlayersBoard Instance { get; private set; }

    private const int MaxClients = 64;

    // ── Lifecycle ─────────────────────────────────────────────────────────────

    public override void _EnterTree()
    {
        // SUBSCRIPTION TIMING FIX:
        // Subscribe here, not in _Ready(), so that every time this node
        // re-enters the tree (e.g. after the intro sweep removes/re-adds it)
        // the subscription is restored before any broadcast can be missed.
        LiveConnectionManager.OnUserListUpdated += OnUserListUpdated;
    }

    public override void _Ready()
    {
        Instance = this;

        _col1Label    = GetNodeOrNull<Label3D>("Col1Label");
        _col2Label    = GetNodeOrNull<Label3D>("Col2Label");
        _col3Label    = GetNodeOrNull<Label3D>("Col3Label");
        _col4Label    = GetNodeOrNull<Label3D>("Col4Label");
        _counterLabel = GetNodeOrNull<Label3D>("CounterLabel");

        Visible = true;

        // CATCH-UP: apply any broadcast that arrived while the board was absent
        // from the scene tree (i.e. during the intro sequence). If no broadcast
        // has arrived yet, LastKnownPlayers is an empty list — no harm done.
        var cached = LiveConnectionManager.LastKnownPlayers;
        if (cached.Count > 0)
            ApplyPlayerList(cached);
    }

    public override void _Process(double delta)
    {
        while (_mainThreadQueue.TryDequeue(out var action))
            action();
    }

    public override void _ExitTree()
    {
        // Unsubscribe to prevent the static event from holding a delegate into
        // a freed node, which would crash when the event fires after scene exit.
        LiveConnectionManager.OnUserListUpdated -= OnUserListUpdated;
        if (Instance == this) Instance = null;
    }

    // ── Event handlers ────────────────────────────────────────────────────────

    /// <summary>
    /// Called on the BACKGROUND listener thread — must never touch Godot API.
    /// Enqueues the label update to be applied on the main thread.
    /// </summary>
    private void OnUserListUpdated(List<string> users)
    {
        _mainThreadQueue.Enqueue(() =>
        {
            _currentPlayers = users;
            ApplyPlayerList(users);
        });
    }

    // ── Rendering ─────────────────────────────────────────────────────────────

    /// <summary>
    /// Splits <paramref name="users"/> across 4 columns of 16 players each
    /// and updates the counter label. Safe to call from the main thread only.
    /// </summary>
    private void ApplyPlayerList(List<string> users)
    {
        if (_counterLabel != null)
            _counterLabel.Text = $"{users.Count} / {MaxClients}";

        if (users.Count == 0)
        {
            SetColumn(_col1Label, users, 0, 0);
            SetColumn(_col2Label, users, 0, 0);
            SetColumn(_col3Label, users, 0, 0);
            SetColumn(_col4Label, users, 0, 0);
            return;
        }

        // Divide into 4 equal-ish buckets: ceiling-split so early columns are
        // never shorter than later ones.
        int total    = users.Count;
        int baseSize = total / 4;
        int extra    = total % 4;   // first `extra` columns get one more name

        int end0 = baseSize + (extra > 0 ? 1 : 0);
        int end1 = end0 + baseSize + (extra > 1 ? 1 : 0);
        int end2 = end1 + baseSize + (extra > 2 ? 1 : 0);

        SetColumn(_col1Label, users, 0,    end0);
        SetColumn(_col2Label, users, end0, end1);
        SetColumn(_col3Label, users, end1, end2);
        SetColumn(_col4Label, users, end2, total);
    }

    /// <summary>
    /// Writes names from <paramref name="users"/>[<paramref name="from"/>..
    /// <paramref name="to"/>] into <paramref name="label"/>, one per line.
    /// </summary>
    private static void SetColumn(Label3D label, List<string> users, int from, int to)
    {
        if (label == null) return;
        if (from >= to) { label.Text = ""; return; }

        var sb = new System.Text.StringBuilder();
        for (int i = from; i < to; i++)
        {
            if (i > from) sb.Append('\n');
            sb.Append(users[i]);
        }
        label.Text = sb.ToString();
    }

    // ── Public API (main thread only) ─────────────────────────────────────────

    /// <summary>
    /// Returns the last known player list snapshot. Call from the main thread only.
    /// </summary>
    public List<string> GetCurrentPlayers() => _currentPlayers;
}
