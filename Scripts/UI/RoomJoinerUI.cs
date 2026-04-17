// ═══════════════════════════════════════════════════
// RoomJoinerUI.cs
// 3D wall-mounted panel using Label3D + Interactable raycasting.
// Left:   JOIN button (Interactable).
// Middle: Room status dot + AVAILABLE / PLAYING label.
// Right:  Up to 4 horizontal player name slots.
// ═══════════════════════════════════════════════════
using Godot;

/// <summary>
/// Root script for the in-world RoomJoiner prop.
/// Call <see cref="SetStatus"/> and <see cref="SetPlayers"/> to update at runtime.
/// </summary>
public partial class RoomJoinerUI : Node3D
{
	/// <summary>Emitted when the player activates the Join button.</summary>
	[Signal] public delegate void JoinRequestedEventHandler();

	private static readonly Color ColorAvailable = new Color(0.12f, 0.85f, 0.22f, 1f);
	private static readonly Color ColorPlaying   = new Color(0.92f, 0.12f, 0.12f, 1f);
	private static readonly Color ColorAccent    = new Color(0.95f, 0.72f, 0.45f, 1f);
	private static readonly Color ColorDim       = new Color(0.5f,  0.4f,  0.3f,  0.4f);

	private Label3D         _statusLabel;
	private MeshInstance3D  _statusDot;
	private Label3D         _joinLabel;
	private Interactable    _joinInteractable;
	private readonly Label3D[] _playerSlots = new Label3D[4];
	private bool            _isPlaying;

	// ── Lifecycle ─────────────────────────────────────────────────────────────

	public override void _Ready()
	{
		_statusLabel      = GetNodeOrNull<Label3D>("StatusLabel");
		_statusDot        = GetNodeOrNull<MeshInstance3D>("StatusDot");
		_joinLabel        = GetNodeOrNull<Label3D>("JoinButton/Label3D");
		_joinInteractable = GetNodeOrNull<Interactable>("JoinButton/Interactable");

		for (int i = 0; i < _playerSlots.Length; i++)
			_playerSlots[i] = GetNodeOrNull<Label3D>($"PlayerSlot{i}");

		if (_joinInteractable != null)
			_joinInteractable.Interacted += OnJoinPressed;

		SetStatus(false);
	}

	// ── Public API ────────────────────────────────────────────────────────────

	/// <summary>
	/// Updates status dot, label, and join button availability.
	/// true = red PLAYING (join blocked), false = green AVAILABLE (join open).
	/// </summary>
	public void SetStatus(bool isPlaying)
	{
		_isPlaying = isPlaying;
		Color c = isPlaying ? ColorPlaying : ColorAvailable;

		if (_statusLabel != null)
		{
			_statusLabel.Text     = isPlaying ? "PLAYING" : "AVAILABLE";
			_statusLabel.Modulate = c;
		}

		if (_statusDot != null)
		{
			var mat = _statusDot.GetSurfaceOverrideMaterial(0) as StandardMaterial3D;
			if (mat != null) mat.AlbedoColor = c;
		}

		if (_joinLabel != null)
			_joinLabel.Modulate = isPlaying ? ColorDim : ColorAccent;
	}

	/// <summary>Updates the right-hand player slots with the given names (max 4).</summary>
	public void SetPlayers(string[] playerNames)
	{
		for (int i = 0; i < _playerSlots.Length; i++)
		{
			if (_playerSlots[i] == null) continue;
			if (i < playerNames.Length)
			{
				string n = playerNames[i];
				_playerSlots[i].Text     = n.Length > 5 ? n[..5] : n;
				_playerSlots[i].Modulate = ColorAccent;
			}
			else
			{
				_playerSlots[i].Text     = "---";
				_playerSlots[i].Modulate = new Color(0.3f, 0.25f, 0.2f, 0.3f);
			}
		}
	}

	// ── Handlers ──────────────────────────────────────────────────────────────

	private void OnJoinPressed()
	{
		if (_isPlaying) return;
		EmitSignal(SignalName.JoinRequested);
	}
}
