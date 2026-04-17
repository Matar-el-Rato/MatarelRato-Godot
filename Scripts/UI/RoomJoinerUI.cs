// ═══════════════════════════════════════════════════
// RoomJoinerUI.cs
// 3D wall-mounted panel. Left quarter: JOIN button.
// Middle quarter: room status label.
// Right half: up to 4 horizontal player name slots.
// ═══════════════════════════════════════════════════
using Godot;
using System;

public partial class RoomJoinerUI : Node3D
{
	public enum RoomStatus { Available, Full, InGame }

	[Signal] public delegate void JoinRequestedEventHandler();

	private static readonly Color ColorAvailable = new Color(0.12f, 0.85f, 0.22f, 1f);
	private static readonly Color ColorFull      = new Color(0.92f, 0.12f, 0.12f, 1f);
	private static readonly Color ColorInGame    = new Color(0.95f, 0.55f, 0.05f, 1f);
	private static readonly Color ColorDim       = new Color(0.5f,  0.4f,  0.3f,  0.4f);

	private Label3D            _statusLabel;
	private Label3D            _joinLabel;
	private Interactable       _joinInteractable;
	private readonly Label3D[] _playerSlots = new Label3D[4];
	private readonly bool[]    _slotActive  = new bool[4];
	private readonly Vector3[] _slotBase    = new Vector3[4];
	private Vector3            _joinLabelBase;
	private RoomStatus         _status = RoomStatus.Available;
	private bool               _joinHovered;
	private readonly Random    _rng = new Random();

	// ── Lifecycle ─────────────────────────────────────────────────────────────

	public override void _Ready()
	{
		_statusLabel      = GetNodeOrNull<Label3D>("StatusLabel");
		_joinLabel        = GetNodeOrNull<Label3D>("JoinButton/Label3D");
		_joinInteractable = GetNodeOrNull<Interactable>("JoinButton/Interactable");

		for (int i = 0; i < _playerSlots.Length; i++)
		{
			_playerSlots[i] = GetNodeOrNull<Label3D>($"PlayerSlot{i}");
			if (_playerSlots[i] != null)
				_slotBase[i] = _playerSlots[i].Position;
		}

		if (_joinLabel != null)
			_joinLabelBase = _joinLabel.Position;

		if (_joinInteractable != null)
		{
			_joinInteractable.Interacted += OnJoinPressed;
			_joinInteractable.Focused    += () => _joinHovered = true;
			_joinInteractable.Unfocused  += () => _joinHovered = false;
		}

		SetStatus(RoomStatus.Available);
		StartFlicker();
	}

	// ── Public API ────────────────────────────────────────────────────────────

	public void SetStatus(RoomStatus status)
	{
		_status = status;

		if (_statusLabel != null)
		{
			_statusLabel.Text = status switch
			{
				RoomStatus.Available => "AVAILABLE",
				RoomStatus.Full      => "FULL",
				RoomStatus.InGame    => "IN GAME",
				_                    => "AVAILABLE"
			};
			_statusLabel.Modulate = Colors.White;
		}
	}

	public void SetPlayers(string[] playerNames)
	{
		for (int i = 0; i < _playerSlots.Length; i++)
		{
			if (_playerSlots[i] == null) continue;
			if (i < playerNames.Length)
			{
				string n = playerNames[i];
				_playerSlots[i].Text = n.Length > 5 ? n[..5] : n;
				_slotActive[i] = true;
			}
			else
			{
				_playerSlots[i].Text = "---";
				_slotActive[i] = false;
			}
		}
	}

	// ── Flicker loop ──────────────────────────────────────────────────────────

	private async void StartFlicker()
	{
		while (IsInsideTree())
		{
			// Random brief blackout on the JOIN label when not hovered
			if (!_joinHovered && _joinLabel != null && _rng.NextDouble() < 0.10)
			{
				_joinLabel.Modulate = Colors.Transparent;
				await ToSignal(GetTree().CreateTimer(0.025f + (float)_rng.NextDouble() * 0.04f),
					SceneTreeTimer.SignalName.Timeout);
				if (!IsInsideTree()) return;
			}

			UpdateJoinLabel();
			UpdatePlayerSlots();

			float wait = 0.04f + (float)_rng.NextDouble() * 0.13f;
			await ToSignal(GetTree().CreateTimer(wait), SceneTreeTimer.SignalName.Timeout);
		}
	}

	private void UpdateJoinLabel()
	{
		if (_joinLabel == null) return;

		if (_status != RoomStatus.Available)
		{
			_joinLabel.Modulate = ColorDim;
			_joinLabel.Position = _joinLabelBase;
			return;
		}

		if (_joinHovered)
		{
			float w = 0.80f + (float)_rng.NextDouble() * 0.20f;
			_joinLabel.Modulate = new Color(w, w, w, 1f);
			float jx = (float)(_rng.NextDouble() * 0.012 - 0.006);
			float jy = (float)(_rng.NextDouble() * 0.008 - 0.004);
			_joinLabel.Position = _joinLabelBase + new Vector3(jx, jy, 0);
		}
		else
		{
			// Hellfire orange-red flicker (same palette as AuthChoiceUI)
			float b = 0.45f + (float)_rng.NextDouble() * 0.55f;
			_joinLabel.Modulate = new Color(
				b,
				b * (0.15f + (float)_rng.NextDouble() * 0.25f),
				b * (float)(_rng.NextDouble() * 0.05f)
			);
			if (_rng.NextDouble() < 0.25)
			{
				float jx = (float)(_rng.NextDouble() * 0.005 - 0.0025);
				float jy = (float)(_rng.NextDouble() * 0.002 - 0.001);
				_joinLabel.Position = _joinLabelBase + new Vector3(jx, jy, 0);
			}
			else
			{
				_joinLabel.Position = _joinLabelBase;
			}
		}
	}

	private void UpdatePlayerSlots()
	{
		for (int i = 0; i < _playerSlots.Length; i++)
		{
			var slot = _playerSlots[i];
			if (slot == null) continue;

			if (!_slotActive[i])
			{
				slot.Modulate = new Color(0.3f, 0.25f, 0.2f, 0.3f);
				slot.Position = _slotBase[i];
				continue;
			}

			// Filled slot: constant hellfire flicker + jitter
			float b = 0.55f + (float)_rng.NextDouble() * 0.45f;
			slot.Modulate = new Color(
				b,
				b * (0.30f + (float)_rng.NextDouble() * 0.40f),
				b * (float)(_rng.NextDouble() * 0.08f)
			);
			float jx = (float)(_rng.NextDouble() * 0.008 - 0.004);
			float jy = (float)(_rng.NextDouble() * 0.005 - 0.0025);
			slot.Position = _slotBase[i] + new Vector3(jx, jy, 0);
		}
	}

	// ── Handlers ──────────────────────────────────────────────────────────────

	private void OnJoinPressed()
	{
		if (_status != RoomStatus.Available) return;
		EmitSignal(SignalName.JoinRequested);
	}
}
