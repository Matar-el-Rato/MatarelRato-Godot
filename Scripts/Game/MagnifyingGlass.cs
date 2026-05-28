// ═══════════════════════════════════════════════════
// MagnifyingGlass.cs
// Interactable magnifying glass prop. Highlights on hover.
// Clicking does nothing for now — peek logic will be
// added once server action support is implemented.
// ═══════════════════════════════════════════════════
using Godot;

public partial class MagnifyingGlass : Node3D
{
	[ExportGroup("Components")]
	[Export] public NodePath InteractablePath;

	private Interactable _interactable;
	private bool         _isDisappearing = false;

	// Initial transform/parent captured at _Ready so a consumed magnifier resets back to
	// the hidden "fresh" pose PlayerItemSet expects for a future SpawnItem grant.
	private Node    _initialParent;
	private Vector3 _initialLocalPos;
	private Vector3 _initialLocalRot;
	private Vector3 _initialLocalScale = Vector3.One;

	public override void _Ready()
	{
		_interactable = GetNodeOrNull<Interactable>(InteractablePath);
		// Interacted signal intentionally not connected — no action yet.

		_initialParent     = GetParent();
		_initialLocalPos   = Position;
		_initialLocalRot   = Rotation;
		_initialLocalScale = Scale;
	}

	/// <summary>
	/// Enable/disable interaction. TableManager gates this around the appropriate window
	/// once the peek logic is wired.
	/// </summary>
	public void SetInteractionEnabled(bool enabled)
	{
		if (_interactable != null)
			_interactable.Enabled = enabled;
	}

	/// <summary>
	/// Scales the item out and resets it back to its hidden "fresh" state so a future
	/// golden-square grant via PlayerItemSet.SpawnItem can re-spawn it. Crucially does NOT
	/// QueueFree — PlayerItemSet looks the node up by name ("MagnifyingGlass") in SpawnItem,
	/// so freeing it would make any subsequent grant fail with "not found in PlayerItemSet".
	/// </summary>
	public void BurnDisappear()
	{
		if (_isDisappearing) return;
		_isDisappearing = true;
		SetInteractionEnabled(false);

		var tween = CreateTween();
		tween.TweenProperty(this, "scale", new Vector3(0.001f, 0.001f, 0.001f), 0.5f)
		     .SetTrans(Tween.TransitionType.Quad).SetEase(Tween.EaseType.In);
		tween.Finished += ResetToFresh;
	}

	private void ResetToFresh()
	{
		if (!IsInsideTree()) return;

		// If the item was reparented (e.g. to a camera/hand) during use, return it under
		// the PlayerItemSet so SpawnItem's name lookup succeeds on the next grant.
		if (_initialParent != null && IsInstanceValid(_initialParent) && GetParent() != _initialParent)
			Reparent(_initialParent, true);

		Position = _initialLocalPos;
		Rotation = _initialLocalRot;
		Scale    = _initialLocalScale;

		Visible = false;
		if (_interactable != null) _interactable.Enabled = false;

		_isDisappearing = false;
	}
}
