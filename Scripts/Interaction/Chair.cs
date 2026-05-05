// ═══════════════════════════════════════════════════
// Chair.cs
// Makes a chair prop interactable: triggers the player's
// Sit() animation and camera transition when clicked.
// ═══════════════════════════════════════════════════
using Godot;
using System;

/// <summary>
/// Attached to chair props. On interaction, locates the
/// <see cref="PlayerCameraController"/> and calls <see cref="PlayerCameraController.Sit"/>.
/// Uses the "player" group for a fast lookup, with a recursive tree search as fallback.
/// </summary>
public partial class Chair : Node3D
{
	/// <summary>Local-space offset from the chair origin where the player body is placed.</summary>
	[Export] public Vector3 SitOffset = new Vector3(0, 0.45f, 0.15f);
	/// <summary>Camera field-of-view while the player is seated.</summary>
	[Export] public float   SitFOV    = 110f;

	private Interactable  _interactable;
	private PlayerItemSet _linkedItemSet;
	private string        _colorKey  = "";
	private string        _colorName = "";
	private Color         _slotColor;

	// Color key of the chair the local player chose ("" = not chosen yet).
	// Once set, only interactions with THIS chair are allowed (to re-sit after standing).
	private static string _localChosenKey = "";
	public  static string LocalChosenKey   => _localChosenKey;
	public  static void   ResetLocalChoice() => _localChosenKey = "";

	// ── Lifecycle ─────────────────────────────────────────────────────────────

	public override void _Ready()
	{
		AddToGroup("chairs");

		// Try the named child first, then fall back to any Interactable in children.
		_interactable = GetNodeOrNull<Interactable>("Interactable");
		if (_interactable == null)
		{
			foreach (var child in GetChildren())
			{
				if (child is Interactable i)
				{
					_interactable = i;
					break;
				}
			}
		}

		if (_interactable != null)
		{
			_interactable.Interacted   += OnInteracted;
			_interactable.UseLeftClick  = true;
			_interactable.PromptText    = "Sit";
		}
		else
		{
			GD.PrintErr($"[Chair] {Name} could not find an Interactable child!");
		}
	}

	// ── Public API ───────────────────────────────────────────────────────────

	/// <summary>
	/// Links the item set that belongs to this chair's slot.
	/// Called by TableManager after spawning. When the player sits,
	/// the linked set is enabled for interaction.
	/// </summary>
	public void LinkItemSet(PlayerItemSet itemSet) => _linkedItemSet = itemSet;

	/// <summary>
	/// Sets the chair's highlight and prompt colors for seat-selection.
	/// Call after AddChild so _interactable is resolved.
	/// </summary>
	public void SetSlotColor(Color color, string colorName)
	{
		if (_interactable == null) return;
		_slotColor = color;
		_colorName = colorName;
		_interactable.HighlightColor = color;
		_interactable.PromptColor    = color;
		_interactable.PromptText     = $"Pick \"{colorName}\"";
		_colorKey = colorName.ToLower();
	}

	/// <summary>
	/// Re-opens this chair for selection after its previous occupant left.
	/// </summary>
	public void SetVacated()
	{
		if (_interactable == null) return;
		_interactable.Enabled        = true;
		_interactable.HighlightColor = _slotColor;
		_interactable.PromptColor    = _slotColor;
		_interactable.PromptText     = string.IsNullOrEmpty(_colorName) ? "Sit" : $"Pick \"{_colorName}\"";
	}

	/// <summary>
	/// Marks this chair as taken by a remote player: disables interaction.
	/// </summary>
	public void SetTaken()
	{
		if (_interactable == null) return;
		_interactable.Enabled    = false;
		_interactable.PromptText = "(Taken)";
	}

	/// <summary>
	/// Called when the LOCAL player picks this chair.
	/// Resets the prompt to the normal "Sit" mode so they can still interact with it.
	/// </summary>
	public void SetPickedBySelf()
	{
		if (_interactable == null) return;
		_interactable.Enabled    = true;
		_interactable.PromptText = "Sit";
		_interactable.SetHighlightColor(Colors.Yellow);
		_interactable.PromptColor = Colors.White;
	}

	// ── Handler ───────────────────────────────────────────────────────────────

	private void OnInteracted()
	{
		// Once a chair is chosen, only allow re-sitting in that same chair.
		if (_localChosenKey != "" && _colorKey != _localChosenKey)
		{
			GD.Print($"[Chair] Blocked pick of '{_colorKey}' — already chose '{_localChosenKey}'");
			return;
		}

		var player = GetTree().GetFirstNodeInGroup("player") as PlayerCameraController;
		if (player == null) player = FindPlayerRecursive(GetTree().Root);
		if (player == null) return;

		bool firstPick = (_localChosenKey == "");
		if (firstPick)
		{
			_localChosenKey = _colorKey;
			GD.Print($"[Chair] First pick: '{_colorKey}', notifying TableManager");
		}

		player.Sit(this);
		_linkedItemSet?.SetOwned(true);

		if (firstPick)
		{
			SetPickedBySelf();

			// Disable all other chairs directly via the scene group — no static events.
			foreach (Node node in GetTree().GetNodesInGroup("chairs"))
				if (node is Chair other && other != this)
					other.SetTaken();

			if (!string.IsNullOrEmpty(_colorKey) && LiveConnectionManager.IsConnected)
			{
				string json = $"{{\"action\":\"choose_chair\",\"color\":\"{_colorKey}\"}}";
				LiveConnectionManager.SendGameAction(
					LiveConnectionManager.CurrentMatchId,
					LiveConnectionManager.LocalUserId,
					json);
			}
		}
	}

	// ── Spawn VFX ─────────────────────────────────────────────────────────────

	/// <summary>Scale-up from nothing with a fire flash. Call right after AddChild.</summary>
	public void Appear()
	{
		Scale = new Vector3(0.001f, 0.001f, 0.001f);
		var tween = CreateTween();
		tween.TweenProperty(this, "scale", Vector3.One, 0.45f)
			 .SetTrans(Tween.TransitionType.Quad)
			 .SetEase(Tween.EaseType.Out);
		AddBurnFlash();
		AddEmbers();
	}

	private void AddBurnFlash()
	{
		var flash = new OmniLight3D
		{
			TopLevel    = true,
			LightColor  = new Color(1.0f, 0.5f, 0.2f),
			LightEnergy = 0.0f,
			OmniRange   = 6.0f,
		};
		GetParent().AddChild(flash);
		flash.GlobalPosition = GlobalPosition + Vector3.Up;
		var t = CreateTween();
		t.TweenProperty(flash, "light_energy", 5.0f, 0.45f * 0.15f);
		t.TweenProperty(flash, "light_energy", 0.0f, 0.45f * 0.85f);
		t.Finished += () => flash.QueueFree();
	}

	private void AddEmbers()
	{
		var particles = new CpuParticles3D { TopLevel = true };
		GetParent().AddChild(particles);
		particles.GlobalPosition     = GlobalPosition + Vector3.Up * 0.5f;
		particles.Amount             = 120;
		particles.Lifetime           = 0.45f * 1.5f;
		particles.OneShot            = true;
		particles.Explosiveness      = 0.85f;
		particles.EmissionShape      = CpuParticles3D.EmissionShapeEnum.Box;
		particles.EmissionBoxExtents = new Vector3(0.5f, 0.8f, 0.5f);
		particles.Direction          = new Vector3(0, 1, 0);
		particles.Spread             = 55.0f;
		particles.Gravity            = new Vector3(0, 2.0f, 0);
		particles.InitialVelocityMin = 1.0f;
		particles.InitialVelocityMax = 2.5f;
		particles.ScaleAmountMin     = 0.8f;
		particles.ScaleAmountMax     = 1.5f;

		var gradient = new Gradient();
		gradient.SetColor(0, new Color(1, 1, 0.5f, 1));
		gradient.AddPoint(0.3f, new Color(1, 0.5f, 0.1f, 0.9f));
		gradient.SetColor(gradient.GetPointCount() - 1, new Color(0.8f, 0.1f, 0, 0));
		particles.ColorRamp = gradient;
		particles.Mesh = new QuadMesh { Size = new Vector2(0.018f, 0.018f) };
		particles.MaterialOverride = new StandardMaterial3D
		{
			ShadingMode            = StandardMaterial3D.ShadingModeEnum.Unshaded,
			VertexColorUseAsAlbedo = true,
			BillboardMode          = StandardMaterial3D.BillboardModeEnum.Enabled,
			Transparency           = StandardMaterial3D.TransparencyEnum.Alpha,
		};
		particles.Emitting = true;
		GetTree().CreateTimer(particles.Lifetime + 0.5f).Timeout +=
			() => { if (IsInstanceValid(particles)) particles.QueueFree(); };
	}

	// ── Helpers ───────────────────────────────────────────────────────────────

	/// <summary>
	/// Depth-first recursive search for a <see cref="PlayerCameraController"/> anywhere in the tree.
	/// </summary>
	private PlayerCameraController FindPlayerRecursive(Node node)
	{
		if (node is PlayerCameraController p) return p;
		foreach (Node child in node.GetChildren())
		{
			var found = FindPlayerRecursive(child);
			if (found != null) return found;
		}
		return null;
	}
}
