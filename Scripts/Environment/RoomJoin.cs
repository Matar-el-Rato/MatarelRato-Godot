// ═══════════════════════════════════════════════════
// RoomJoin.cs
// Manages room entry/exit for a private room.
// On entry: each interior light flickers on independently.
// On exit:  each light flickers off independently, then doors close.
// Also checks player proximity to trigger the room's standalone NPC.
// ═══════════════════════════════════════════════════
using Godot;
using System;
using System.Collections.Generic;

/// <summary>
/// Attach to a Node3D inside a room. Wire up exports in the editor:
/// <list type="bullet">
/// <item><see cref="LightingNode"/> — Node3D whose Light3D descendants are the interior lights.</item>
/// <item><see cref="RoomArea"/>    — Area3D covering the room interior (player enter/exit detection).</item>
/// <item><see cref="Player"/>      — The player CharacterBody3D.</item>
/// <item><see cref="RoomNpc"/>     — The room's standalone <see cref="RoomNPC"/> instance.</item>
/// <item><see cref="LeftDoorPath"/> / <see cref="RightDoorPath"/> — Paths to the door pair.</item>
/// </list>
/// </summary>
public partial class RoomJoin : Node3D
{
	[Export] public NodePath        LeftDoorPath;
	[Export] public NodePath        RightDoorPath;
	[Export] public Node3D          LightingNode;
	[Export] public Area3D          RoomArea;
	[Export] public CharacterBody3D Player;
	[Export] public RoomNPC         RoomNpc;
	[Export] public AudioStream     FlickerSound;
	/// <summary>Apparel props that slide open on welcome and slide back on room exit.</summary>
	[Export] public Node3D          LeftApparel;
	[Export] public Node3D          RightApparel;
	/// <summary>The playing table setup — starts hidden below floor, rises when apparel opens.</summary>
	[Export] public Node3D          PlayingSetup;
	/// <summary>Which room this manager owns (1-3). Must match the RoomJoinerUI.RoomId.</summary>
	[Export] public int             RoomId       = 1;
	/// <summary>The RoomJoinerUI panel inside this room. Auto-found by RoomId if left empty.</summary>
	[Export] public RoomJoinerUI    RoomJoinerUi;

	[ExportGroup("Tuning")]
	[Export] public float NpcProximityDistance = 4.0f;
	[Export] public float NpcHysteresis        = 1.5f;
	/// <summary>Delay after lights finish flickering off before the doors close.</summary>
	[Export] public float DoorCloseDelay       = 2.0f;

	private Door _leftDoor;
	private Door _rightDoor;
	private readonly List<(Light3D light, float originalEnergy)> _lights = new();
	private bool  _playerInside        = false;
	private AudioStreamPlayer _flickerAudio;
	private AudioStreamPlayer _stoneScrapeAudio;
	private AudioStreamPlayer _countdownClackAudio;
	private float _leftApparelBaseZ;
	private float _rightApparelBaseZ;
	private float _playingSetupBaseY;
	private Vector3 _preRoomPosition;
	private const float PlayingSetupHideOffset = 1.2f;
	private int      _transitionGrace     = 0;
	private bool     _inRoomOfficially    = false;
	private bool     _localGameActive     = false; // true while a match is running; gates EjectPlayer
	private string[] _lastSeenPlayers     = null;
	private int      _lastCountdownSecs   = -1;

	// ── Lifecycle ─────────────────────────────────────────────────────────────

	public override void _Ready()
	{
		_leftDoor  = LeftDoorPath  is { IsEmpty: false } ? GetNodeOrNull<Door>(LeftDoorPath)  : null;
		_rightDoor = RightDoorPath is { IsEmpty: false } ? GetNodeOrNull<Door>(RightDoorPath) : null;

		FlickerSound ??= ResourceLoader.Load<AudioStream>("res://Assets/Sound FX/light_flicker.wav");
		if (FlickerSound != null)
		{
			_flickerAudio        = new AudioStreamPlayer();
			_flickerAudio.Stream = FlickerSound;
			AddChild(_flickerAudio);
		}

		var stoneScrapeStream = ResourceLoader.Load<AudioStream>("res://Assets/Sound FX/stone_scrape.wav");
		if (stoneScrapeStream != null)
		{
			_stoneScrapeAudio        = new AudioStreamPlayer();
			_stoneScrapeAudio.Stream = stoneScrapeStream;
			AddChild(_stoneScrapeAudio);
		}

		var clackStream = ResourceLoader.Load<AudioStream>("res://Assets/Sound FX/revolver_clack.wav");
		if (clackStream != null)
		{
			_countdownClackAudio        = new AudioStreamPlayer();
			_countdownClackAudio.Stream = clackStream;
			AddChild(_countdownClackAudio);
		}

		if (LeftApparel == null)
			LeftApparel  = GetTree().Root.FindChild("left_apparel",  true, false) as Node3D;
		if (RightApparel == null)
			RightApparel = GetTree().Root.FindChild("right_apparel", true, false) as Node3D;

		if (LeftApparel  != null) _leftApparelBaseZ  = LeftApparel.Position.Z;
		if (RightApparel != null) _rightApparelBaseZ = RightApparel.Position.Z;

		if (PlayingSetup == null)
			PlayingSetup = GetParent()?.GetNodeOrNull<Node3D>("PlayingSetup")
						?? GetTree().Root.FindChild("PlayingSetup", true, false) as Node3D;
		if (PlayingSetup != null)
		{
			_playingSetupBaseY = PlayingSetup.Position.Y;
			PlayingSetup.Position = PlayingSetup.Position with { Y = _playingSetupBaseY - PlayingSetupHideOffset };
		}

		if (LightingNode != null)
		{
			CollectLights(LightingNode);
			// Start all lights off — they flicker on when the player enters.
			foreach (var (light, _) in _lights)
				if (IsInstanceValid(light)) light.LightEnergy = 0f;
		}

		if (RoomArea != null)
		{
			RoomArea.CollisionMask = 0xFFFFFFFF;
			RoomArea.Monitoring    = true;
		}

		// Auto-find: parent subtree first (co-located in same room node), then whole tree by RoomId.
		if (RoomJoinerUi == null)
			RoomJoinerUi = FindRoomJoinerUI(GetParent() ?? GetTree().Root, RoomId);
		if (RoomJoinerUi == null)
			RoomJoinerUi = FindRoomJoinerUI(GetTree().Root, RoomId);
		if (RoomJoinerUi == null)
			GD.PrintErr($"[RoomJoin] Room {RoomId}: could not find a RoomJoinerUI with RoomId={RoomId}. Wire it in the editor or ensure the RoomId exports match.");

		if (RoomJoinerUi != null)
		{
			RoomJoinerUi.JoinRequested    += OnJoinRequested;
			RoomJoinerUi.ReadyRequested   += OnReadyRequested;
			RoomJoinerUi.UnreadyRequested += OnUnreadyRequested;
			RoomJoinerUi.SetJoinEnabled(false);
		}

		if (RoomNpc != null)
		{
			RoomNpc.WelcomeCompleted += OnNpcWelcomeCompleted;
			RoomNpc.ApparelOpened   += OnApparelOpened;
		}

		if (_leftDoor  != null) _leftDoor.ExitRoomRequested  += OnExitRequested;
		if (_rightDoor != null) _rightDoor.ExitRoomRequested += OnExitRequested;

		TableManager.LocalPlayerEliminated += EjectPlayer;
	}

	public override void _ExitTree()
	{
		TableManager.LocalPlayerEliminated -= EjectPlayer;
	}

	// ── Per-frame checks ──────────────────────────────────────────────────────

	public override void _Process(double delta)
	{
		// Retry finding RoomJoinerUI every frame until resolved (handles scene-load order races).
		if (RoomJoinerUi == null)
		{
			RoomJoinerUi = FindRoomJoinerUI(GetTree().Root, RoomId);
			if (RoomJoinerUi != null)
			{
				RoomJoinerUi.JoinRequested    += OnJoinRequested;
				RoomJoinerUi.ReadyRequested   += OnReadyRequested;
				RoomJoinerUi.UnreadyRequested += OnUnreadyRequested;
				RoomJoinerUi.SetJoinEnabled(false);
			}
		}

		// Poll room state — background thread writes RoomPlayers, main thread reads here.
		// Reference reads are atomic in .NET; no lock needed.
		var current = LiveConnectionManager.RoomPlayers[RoomId];
		if (current != _lastSeenPlayers)
		{
			_lastSeenPlayers = current;
			ApplyRoomUpdate(RoomId, current ?? System.Array.Empty<string>());
		}

		// Poll countdown ticks for our room.
		if (_inRoomOfficially && LiveConnectionManager.CountdownRoom == RoomId)
		{
			int secs = LiveConnectionManager.CountdownSeconds;
			if (secs >= 0 && secs != _lastCountdownSecs)
			{
				_lastCountdownSecs = secs;
				RoomJoinerUi?.SetCountdown(secs);
				PlayCountdownClack(secs);
			}
		}

		// Poll game start for our room.
		if (_inRoomOfficially
			&& LiveConnectionManager.GameStartPending
			&& LiveConnectionManager.GameStartRoom == RoomId)
		{
			LiveConnectionManager.ClearGameStart();
			_lastCountdownSecs = -1;
			OnGameStart();
		}

		if (Player == null) return;

		// NPC proximity
		if (RoomNpc != null)
		{
			float dist = RoomNpc.GlobalPosition.DistanceTo(Player.GlobalPosition);
			if (!RoomNpc.HasAppeared && dist <= NpcProximityDistance)
				RoomNpc.Appear();
			else if (RoomNpc.HasAppeared && dist > NpcProximityDistance + NpcHysteresis)
				RoomNpc.Disappear();
		}

		// Polled area check — avoids physics layer mismatch issues with signals.
		// Sitting disables the collision shape; Unsit() re-enables it in a tween callback
		// but the physics engine needs several frames to re-register the overlap.
		// Hold off the check for 20 frames after any sit/transition ends.
		var pcc = Player as PlayerCameraController;
		if (pcc != null && (pcc.IsSitting || pcc.IsTransitioning))
			_transitionGrace = 20;
		else if (_transitionGrace > 0)
			_transitionGrace--;

		if (RoomArea != null && _transitionGrace == 0)
		{
			bool inside = RoomArea.OverlapsBody(Player);
			if (inside && !_playerInside)
			{
				_playerInside = true;
				StartFlickerOn();
			}
			else if (!inside && _playerInside)
			{
				_playerInside = false;
				StartFlickerOff();
			}
		}
	}

	// ── Flicker orchestration ─────────────────────────────────────────────────

	private void StartFlickerOn()
	{
		// Record where the player was standing outside so EjectPlayer can return them there.
		if (Player != null) _preRoomPosition = Player.GlobalPosition;

		if (_lights.Count == 0) return;
		_flickerAudio?.Play();
		foreach (var (light, origEnergy) in _lights)
			FlickerLightOn(light, origEnergy);
	}

	private void StartFlickerOff()
	{
		_localGameActive = false;
		if (_inRoomOfficially)
		{
			_inRoomOfficially = false;
			LiveConnectionManager.SendLeaveRoom();
			SetDoorsExitMode(false);
		}

		_flickerAudio?.Play();
		foreach (var (light, origEnergy) in _lights)
			FlickerLightOff(light, origEnergy);
		RoomJoinerUi?.SetJoined(false);
		RoomJoinerUi?.SetJoinEnabled(false);
		RoomJoinerUi?.FadeOut();
		CloseDoorAfterDelay();
		ResetApparel();
		(PlayingSetup as TableManager)?.ResetGame();
		RoomNpc?.ResetWelcome();
	}

	/// <summary>
	/// Called after the local player's elimination animation finishes.
	/// Resets their tension, teleports them to the original spawn position, and
	/// plays the look-up camera sweep so they respawn just like the game's intro.
	/// </summary>
	public void EjectPlayer()
	{
		if (!_localGameActive) return;
		_playerInside = false;

		// Room-state reset: lights, doors, music, chairs, tablero.
		// Player position/animation is handled by TableManager.OnPlayerEliminated directly.
		StartFlickerOff();

		// Show the winner message after the screen has faded back in (~1.5 s).
		GetTree().CreateTimer(1.5f).Timeout += () =>
		{
			string msg = TableManager.PendingWinnerMessage;
			if (!string.IsNullOrEmpty(msg))
			{
				TableManager.PendingWinnerMessage = "";
				ChatManager.AddLog(msg);
			}
		};
	}

	// ── Room networking ───────────────────────────────────────────────────────

	private void ApplyRoomUpdate(int roomId, string[] players)
	{
		if (RoomJoinerUi != null)
		{
			var status = players.Length >= 4
				? RoomJoinerUI.RoomStatus.Full
				: RoomJoinerUI.RoomStatus.Available;
			RoomJoinerUi.SetStatus(status);
			RoomJoinerUi.SetPlayers(players);
		}

		string localName = AuthManager.Username;
		bool nowIn = System.Array.IndexOf(players, localName) >= 0;

		if (!_inRoomOfficially && nowIn)
		{
			_inRoomOfficially = true;
			SetDoorsExitMode(true);
			RoomJoinerUi?.SetJoined(true);
			RoomNpc?.SetPlayerJoined(true);
			_leftDoor?.Close();
			_rightDoor?.Close();
		}
		else if (_inRoomOfficially && !nowIn)
		{
			_inRoomOfficially = false;
			SetDoorsExitMode(false);
			RoomJoinerUi?.SetJoined(false);
			RoomJoinerUi?.SetJoinEnabled(false);
			RoomNpc?.SetPlayerJoined(false);
		}
	}

	private void OnNpcWelcomeCompleted()
	{
		RoomJoinerUi?.SetJoinEnabled(true);
		RoomJoinerUi?.FadeIn();
	}

	private void OnApparelOpened()
	{
		if (PlayingSetup == null) return;
		_stoneScrapeAudio?.Play();
		var tween = CreateTween();
		tween.TweenProperty(PlayingSetup, "position:y", _playingSetupBaseY, 1.2f)
			 .SetTrans(Tween.TransitionType.Quad).SetEase(Tween.EaseType.Out);
	}

	private void OnJoinRequested()
	{
		if (!AuthManager.IsLoggedIn || !LiveConnectionManager.IsConnected) return;
		LiveConnectionManager.SendJoinRoom(RoomId);
	}

	private void OnReadyRequested()
	{
		if (!_inRoomOfficially || !LiveConnectionManager.IsConnected) return;
		LiveConnectionManager.SendReady();
	}

	private void OnUnreadyRequested()
	{
		if (!_inRoomOfficially || !LiveConnectionManager.IsConnected) return;
		LiveConnectionManager.SendUnready();
	}

	private void OnGameStart()
	{
		_localGameActive = true;
		RoomJoinerUi?.SetStatus(RoomJoinerUI.RoomStatus.InGame);
		RoomJoinerUi?.FadeOut();
		ChatManager.AddLog("[color=#c04040]> Game started! Choose your chair.[/color]");
		(PlayingSetup as TableManager)?.StartGame(LiveConnectionManager.CurrentPlayerCount);
		GD.Print($"[RoomJoin] Room {RoomId}: game starting!");
	}

	private void OnExitRequested()
	{
		if (!_inRoomOfficially) return;
		_inRoomOfficially = false;
		LiveConnectionManager.SendLeaveRoom();
		SetDoorsExitMode(false);
		RoomJoinerUi?.SetJoined(false);
		RoomJoinerUi?.SetJoinEnabled(false);
		// Open both doors so the player can walk out.
		if (_leftDoor  != null && !_leftDoor.IsOpen)  _leftDoor.Interact(playSound: true);
		if (_rightDoor != null && !_rightDoor.IsOpen) _rightDoor.Interact(playSound: true);
	}

	private void SetDoorsExitMode(bool exit)
	{
		_leftDoor?.SetExitMode(exit);
		_rightDoor?.SetExitMode(exit);
	}

	private void ResetApparel()
	{
		if (LeftApparel == null && RightApparel == null && PlayingSetup == null) return;
		var tween = CreateTween();
		tween.SetParallel(true);
		if (LeftApparel != null)
			tween.TweenProperty(LeftApparel,  "position:z", _leftApparelBaseZ,  1.0f)
				 .SetTrans(Tween.TransitionType.Quad).SetEase(Tween.EaseType.InOut);
		if (RightApparel != null)
			tween.TweenProperty(RightApparel, "position:z", _rightApparelBaseZ, 1.0f)
				 .SetTrans(Tween.TransitionType.Quad).SetEase(Tween.EaseType.InOut);
		if (PlayingSetup != null)
			tween.TweenProperty(PlayingSetup, "position:y", _playingSetupBaseY - PlayingSetupHideOffset, 1.0f)
				 .SetTrans(Tween.TransitionType.Quad).SetEase(Tween.EaseType.InOut);
	}

	// ── Per-light flicker coroutines ──────────────────────────────────────────

	/// <summary>
	/// Flickers a single light on with a random start delay and independent timing,
	/// simulating a fluorescent tube struggling to start.
	/// </summary>
	private async void FlickerLightOn(Light3D light, float origEnergy)
	{
		var rng = new Random();

		// Stagger start so lights don't all trigger at the same moment.
		float startDelay = (float)rng.NextDouble() * 0.45f;
		await ToSignal(GetTree().CreateTimer(startDelay), SceneTreeTimer.SignalName.Timeout);
		if (!IsInsideTree() || !IsInstanceValid(light)) return;

		int cycles = 2 + rng.Next(5); // 2–6 individual flicker pulses
		for (int i = 0; i < cycles; i++)
		{
			if (!IsInsideTree() || !IsInstanceValid(light)) return;
			// Partial brightness flicker — not always full power.
			float partial = 0.35f + (float)rng.NextDouble() * 0.65f;
			light.LightEnergy = (i % 2 == 0) ? 0f : origEnergy * partial;
			float wait = 0.03f + (float)rng.NextDouble() * 0.13f;
			await ToSignal(GetTree().CreateTimer(wait), SceneTreeTimer.SignalName.Timeout);
		}

		if (!IsInsideTree() || !IsInstanceValid(light)) return;
		light.LightEnergy = origEnergy;
	}

	/// <summary>
	/// Flickers a single light off with random stagger and independent timing,
	/// then leaves it fully off.
	/// </summary>
	private async void FlickerLightOff(Light3D light, float origEnergy)
	{
		var rng = new Random();

		float startDelay = (float)rng.NextDouble() * 0.45f;
		await ToSignal(GetTree().CreateTimer(startDelay), SceneTreeTimer.SignalName.Timeout);
		if (!IsInsideTree() || !IsInstanceValid(light)) return;

		int cycles = 2 + rng.Next(4); // 2–5 flicker pulses before dying
		for (int i = 0; i < cycles; i++)
		{
			if (!IsInsideTree() || !IsInstanceValid(light)) return;
			float partial = 0.35f + (float)rng.NextDouble() * 0.65f;
			light.LightEnergy = (i % 2 == 0) ? origEnergy * partial : 0f;
			float wait = 0.03f + (float)rng.NextDouble() * 0.13f;
			await ToSignal(GetTree().CreateTimer(wait), SceneTreeTimer.SignalName.Timeout);
		}

		if (!IsInsideTree() || !IsInstanceValid(light)) return;
		light.LightEnergy = 0f;
	}

	private async void CloseDoorAfterDelay()
	{
		await ToSignal(GetTree().CreateTimer(DoorCloseDelay), SceneTreeTimer.SignalName.Timeout);
		if (!IsInsideTree()) return;
		_leftDoor?.Close();
		_rightDoor?.Close();
	}

	// ── Helpers ───────────────────────────────────────────────────────────────

	private void PlayCountdownClack(int secs)
	{
		if (_countdownClackAudio == null) return;
		// Volume rises from -14 dB at secs=10 to 0 dB at secs=1 (linear interpolation).
		float t = Mathf.Clamp(1f - (secs - 1f) / 9f, 0f, 1f);
		_countdownClackAudio.VolumeDb = Mathf.Lerp(-14f, 0f, t);
		_countdownClackAudio.Play();
	}

	private static RoomJoinerUI FindRoomJoinerUI(Node root, int targetRoomId)
	{
		if (root is RoomJoinerUI ui && ui.RoomId == targetRoomId) return ui;
		foreach (Node child in root.GetChildren())
		{
			var found = FindRoomJoinerUI(child, targetRoomId);
			if (found != null) return found;
		}
		return null;
	}

	private void CollectLights(Node node)
	{
		if (node is Light3D light)
			_lights.Add((light, light.LightEnergy));
		foreach (Node child in node.GetChildren())
			CollectLights(child);
	}
}
