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

	/// <summary>Which room (1-3) this panel represents. Set in the editor.</summary>
	[Export] public int RoomId = 1;

	[Signal] public delegate void JoinRequestedEventHandler();

	private static readonly Color ColorDim = new Color(0.5f, 0.4f, 0.3f, 0.4f);

	private Label3D            _statusLabel;
	private Label3D            _joinLabel;
	private Label3D            _playersHeader;
	private MeshInstance3D     _leftDivider;
	private MeshInstance3D     _rightDivider;
	private Interactable       _joinInteractable;
	private readonly Label3D[] _playerSlots = new Label3D[4];
	private readonly bool[]    _slotActive  = new bool[4];
	private readonly Vector3[] _slotBase    = new Vector3[4];
	private Vector3            _joinLabelBase;
	private RoomStatus         _status      = RoomStatus.Available;
	private Color              _statusColor = Colors.White;
	private bool               _joinHovered;
	private bool               _joinEnabled = false;
	private float              _panelAlpha  = 0f;
	private readonly Random    _rng = new Random();

	// ── Lifecycle ─────────────────────────────────────────────────────────────

	public override void _Ready()
	{
		_statusLabel      = GetNodeOrNull<Label3D>("StatusLabel");
		_joinLabel        = GetNodeOrNull<Label3D>("JoinButton/Label3D");
		_joinInteractable = GetNodeOrNull<Interactable>("JoinButton/Interactable");
		_playersHeader    = GetNodeOrNull<Label3D>("PlayersHeader");
		_leftDivider      = GetNodeOrNull<MeshInstance3D>("LeftDivider");
		_rightDivider     = GetNodeOrNull<MeshInstance3D>("RightDivider");

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
			_joinInteractable.ProcessMode = ProcessModeEnum.Disabled;
		}

		// Start fully hidden — FadeIn() is called by RoomJoin when NPC welcome finishes.
		if (_leftDivider  != null) _leftDivider.Visible  = false;
		if (_rightDivider != null) _rightDivider.Visible = false;

		SetStatus(RoomStatus.Available);
		StartFlicker();
	}

	// ── Public API ────────────────────────────────────────────────────────────

	public void SetStatus(RoomStatus status)
	{
		_status      = status;
		_statusColor = Colors.White;

		if (_statusLabel != null)
			_statusLabel.Text = status switch
			{
				RoomStatus.Available => "AVAILABLE",
				RoomStatus.Full      => "FULL",
				RoomStatus.InGame    => "IN GAME",
				_                    => "AVAILABLE"
			};
	}

	public void SetJoinEnabled(bool enabled)
	{
		_joinEnabled = enabled;
	}

	public void SetJoined(bool joined)
	{
		_joinEnabled = !joined;
		if (_joinLabel != null)
			_joinLabel.Text = joined ? "JOINED" : "JOIN";
	}

	public void SetPlayers(string[] playerNames)
	{
		for (int i = 0; i < _playerSlots.Length; i++)
		{
			if (_playerSlots[i] == null) continue;
			if (i < playerNames.Length)
			{
				_playerSlots[i].Text = playerNames[i];
				_slotActive[i] = true;
			}
			else
			{
				_playerSlots[i].Text = "---";
				_slotActive[i] = false;
			}
		}
	}

	/// <summary>Fades the panel in over <paramref name="duration"/> seconds.</summary>
	public void FadeIn(float duration = 0.5f)
	{
		if (_leftDivider  != null) _leftDivider.Visible  = true;
		if (_rightDivider != null) _rightDivider.Visible = true;
		var tween = CreateTween();
		tween.TweenMethod(Callable.From((float v) => _panelAlpha = v), _panelAlpha, 1f, duration);
		tween.Finished += () => {
			if (_joinInteractable != null)
				_joinInteractable.ProcessMode = ProcessModeEnum.Inherit;
		};
	}

	/// <summary>Fades the panel out over <paramref name="duration"/> seconds.</summary>
	public void FadeOut(float duration = 0.5f)
	{
		if (_joinInteractable != null)
			_joinInteractable.ProcessMode = ProcessModeEnum.Disabled;
		var tween = CreateTween();
		tween.TweenMethod(Callable.From((float v) => _panelAlpha = v), _panelAlpha, 0f, duration);
		tween.Finished += () => {
			if (_leftDivider  != null) _leftDivider.Visible  = false;
			if (_rightDivider != null) _rightDivider.Visible = false;
		};
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
			ApplyPanelAlpha();

			float wait = 0.04f + (float)_rng.NextDouble() * 0.13f;
			await ToSignal(GetTree().CreateTimer(wait), SceneTreeTimer.SignalName.Timeout);
		}
	}

	// UpdateJoinLabel and UpdatePlayerSlots set absolute modulate values each frame;
	// ApplyPanelAlpha scales their alpha by _panelAlpha afterwards.
	private void ApplyPanelAlpha()
	{
		if (_statusLabel != null)
			_statusLabel.Modulate = new Color(_statusColor.R, _statusColor.G, _statusColor.B, _statusColor.A * _panelAlpha);
		if (_playersHeader != null)
			_playersHeader.Modulate = new Color(0.95f, 0.72f, 0.45f, 0.6f * _panelAlpha);
		ScaleAlpha(_joinLabel);
		for (int i = 0; i < _playerSlots.Length; i++)
			ScaleAlpha(_playerSlots[i]);
	}

	private void ScaleAlpha(Label3D label)
	{
		if (label == null) return;
		var c = label.Modulate;
		c.A *= _panelAlpha;
		label.Modulate = c;
	}

	private void UpdateJoinLabel()
	{
		if (_joinLabel == null) return;

		if (!_joinEnabled || _status != RoomStatus.Available)
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
			// Hellfire orange-red flicker
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

			// Filled slot: hellfire flicker + jitter
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
		if (!_joinEnabled || _status != RoomStatus.Available) return;
		EmitSignal(SignalName.JoinRequested);
	}
}
