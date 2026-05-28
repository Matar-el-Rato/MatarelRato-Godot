using Godot;

public partial class MagnifyingGlass : Node3D
{
	[ExportGroup("Components")]
	[Export] public NodePath InteractablePath;

	[Signal] public delegate void UsedEventHandler();

	private Interactable _interactable;
	private bool         _isDisappearing = false;

	private Node    _initialParent;
	private Vector3 _initialLocalPos;
	private Vector3 _initialLocalRot;
	private Vector3 _initialLocalScale = Vector3.One;

	public override void _Ready()
	{
		_interactable = GetNodeOrNull<Interactable>(InteractablePath);
		if (_interactable != null)
			_interactable.Interacted += OnInteracted;

		_initialParent     = GetParent();
		_initialLocalPos   = Position;
		_initialLocalRot   = Rotation;
		_initialLocalScale = Scale;
	}

	public void SetInteractionEnabled(bool enabled)
	{
		if (_interactable != null)
			_interactable.Enabled = enabled;
	}

	private void OnInteracted()
	{
		SetInteractionEnabled(false); // prevent double-use
		EmitSignal(SignalName.Used);
	}

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
