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

		AddBurnFlash(GlobalPosition);
		AddEmbers(GlobalPosition);

		var tween = CreateTween();
		tween.TweenProperty(this, "scale", new Vector3(0.001f, 0.001f, 0.001f), 0.5f)
		     .SetTrans(Tween.TransitionType.Quad).SetEase(Tween.EaseType.In);
		tween.Finished += ResetToFresh;
	}

	private void AddBurnFlash(Vector3 worldPos)
	{
		var flash = new OmniLight3D
		{
			TopLevel    = true,
			LightColor  = new Color(1.0f, 0.5f, 0.2f),
			LightEnergy = 0.0f,
			OmniRange   = 3.0f,
		};
		GetParent().AddChild(flash);
		flash.GlobalPosition = worldPos + Vector3.Up * 0.1f;
		var t = CreateTween();
		t.TweenProperty(flash, "light_energy", 4.0f, 0.07f);
		t.TweenProperty(flash, "light_energy", 0.0f, 0.43f);
		t.Finished += () => flash.QueueFree();
	}

	private void AddEmbers(Vector3 worldPos)
	{
		var particles = new CpuParticles3D { TopLevel = true };
		GetParent().AddChild(particles);
		particles.GlobalPosition     = worldPos + Vector3.Up * 0.1f;
		particles.Amount             = 60;
		particles.Lifetime           = 0.65f;
		particles.OneShot            = true;
		particles.Explosiveness      = 0.85f;
		particles.EmissionShape      = CpuParticles3D.EmissionShapeEnum.Box;
		particles.EmissionBoxExtents = new Vector3(0.12f, 0.18f, 0.12f);
		particles.Direction          = new Vector3(0, 1, 0);
		particles.Spread             = 50.0f;
		particles.Gravity            = new Vector3(0, 2.0f, 0);
		particles.InitialVelocityMin = 0.5f;
		particles.InitialVelocityMax = 1.6f;
		particles.ScaleAmountMin     = 0.5f;
		particles.ScaleAmountMax     = 1.1f;
		var gradient = new Gradient();
		gradient.SetColor(0, new Color(1, 1, 0.5f, 1));
		gradient.AddPoint(0.3f, new Color(1, 0.5f, 0.1f, 0.9f));
		gradient.SetColor(gradient.GetPointCount() - 1, new Color(0.8f, 0.1f, 0, 0));
		particles.ColorRamp = gradient;
		particles.Mesh = new QuadMesh { Size = new Vector2(0.014f, 0.014f) };
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
