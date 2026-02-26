using Godot;

public partial class Mirror : Node3D
{
	[Export] public Vector2 MirrorSize = new Vector2(1.0f, 2.0f);
	[Export] public int PixelsPerUnit = 200;
	[Export] public float FreezeDistance = 50.0f;
	[Export] public float CullNear = 0.05f;
	[Export] public float CullFar  = 50.0f;

	private Camera3D        _mirrorCamera;
	private SubViewport     _viewport;
	private MeshInstance3D  _mirrorMesh;

	// ─── Setup ────────────────────────────────────────────────────────────────

	public override void _Ready()
	{
		// Auto-create children if they don't already exist in the scene.
		// This means you never have to add child nodes manually — just place
		// a Mirror Node3D with this script attached and set the exports.
		EnsureViewport();
		EnsureCamera();
		EnsureMesh();

		// Share the main world so the mirror camera sees the real scene
		_viewport.World3D = GetViewport().World3D;

		// Size quad + viewport from exports
		ApplySize();

		// Wire shader material
		var shader = GD.Load<Shader>("res://Shaders/mirror.gdshader");
		var mat    = new ShaderMaterial();
		mat.Shader = shader;
		mat.SetShaderParameter("mirror_texture", _viewport.GetTexture());
		_mirrorMesh.SetSurfaceOverrideMaterial(0, mat);
	}

	private void EnsureViewport()
	{
		_viewport = GetNodeOrNull<SubViewport>("MirrorViewport");
		if (_viewport != null) return;

		_viewport = new SubViewport { Name = "MirrorViewport" };
		_viewport.RenderTargetUpdateMode = SubViewport.UpdateMode.WhenVisible;
		AddChild(_viewport);
	}

	private void EnsureCamera()
	{
		_mirrorCamera = GetNodeOrNull<Camera3D>("MirrorViewport/MirrorCamera");
		if (_mirrorCamera == null)
		{
			_mirrorCamera = new Camera3D { Name = "MirrorCamera" };
			_viewport.AddChild(_mirrorCamera);
		}
		_mirrorCamera.KeepAspect = Camera3D.KeepAspectEnum.Width;
	}

	private void EnsureMesh()
	{
		_mirrorMesh = GetNodeOrNull<MeshInstance3D>("MirrorMesh");
		if (_mirrorMesh == null)
		{
			_mirrorMesh = new MeshInstance3D { Name = "MirrorMesh", Mesh = new QuadMesh() };
			AddChild(_mirrorMesh);
		}
		else if (_mirrorMesh.Mesh is not QuadMesh)
		{
			_mirrorMesh.Mesh = new QuadMesh();
		}
	}

	private void ApplySize()
	{
		if (_mirrorMesh?.Mesh is QuadMesh q)
			q.Size = MirrorSize;

		if (_viewport != null)
			_viewport.Size = new Vector2I(
				Mathf.Max(1, (int)(MirrorSize.X * PixelsPerUnit)),
				Mathf.Max(1, (int)(MirrorSize.Y * PixelsPerUnit)));
	}

	// ─── Per-frame reflection ─────────────────────────────────────────────────

	public override void _Process(double delta)
	{
		if (_mirrorCamera == null || _mirrorMesh == null) return;

		Camera3D playerCamera = GetViewport().GetCamera3D();
		if (playerCamera == null) return;

		// Freeze when player is far away
		if (GlobalPosition.DistanceTo(playerCamera.GlobalPosition) >= FreezeDistance)
		{
			_viewport.RenderTargetUpdateMode = SubViewport.UpdateMode.Disabled;
			return;
		}
		_viewport.RenderTargetUpdateMode = SubViewport.UpdateMode.WhenVisible;

		// Mirror normal = quad +Z axis
		Vector3 mirrorNormal = _mirrorMesh.GlobalBasis.Z;

		// Step 1: reflect player camera transform through the mirror plane
		Transform3D reflectT = GetReflectionTransform(mirrorNormal, _mirrorMesh.GlobalPosition);
		_mirrorCamera.GlobalTransform = reflectT * playerCamera.GlobalTransform;

		// Step 2: re-orient to face the mirror perpendicularly
		// (must happen before SetFrustum so the offset is in the right local space)
		Vector3 lookTarget = (_mirrorCamera.GlobalPosition / 2f) + (playerCamera.GlobalPosition / 2f);
		if (lookTarget.DistanceTo(_mirrorCamera.GlobalPosition) > 0.001f)
			_mirrorCamera.GlobalTransform = _mirrorCamera.GlobalTransform
				.LookingAt(lookTarget, _mirrorMesh.GlobalBasis.Y);

		// Step 3: set frustum that exactly frames the mirror quad
		Vector3 camToMirror = _mirrorMesh.GlobalPosition - _mirrorCamera.GlobalPosition;
		float near = Mathf.Abs(camToMirror.Dot(mirrorNormal)) + CullNear;
		float far  = camToMirror.Length() + CullFar;

		Vector3 offsetLocal  = _mirrorCamera.GlobalBasis.Inverse() * camToMirror;
		Vector2 frustumOffset = new Vector2(offsetLocal.X, offsetLocal.Y);
		_mirrorCamera.SetFrustum(MirrorSize.X, frustumOffset, near, far);
	}

	// ─── Reflection matrix (I − 2·n⊗n) ──────────────────────────────────────

	private static Transform3D GetReflectionTransform(Vector3 n, Vector3 offset)
	{
		Vector3 bx = new Vector3(1, 0, 0) - 2f * new Vector3(n.X*n.X, n.X*n.Y, n.X*n.Z);
		Vector3 by = new Vector3(0, 1, 0) - 2f * new Vector3(n.Y*n.X, n.Y*n.Y, n.Y*n.Z);
		Vector3 bz = new Vector3(0, 0, 1) - 2f * new Vector3(n.Z*n.X, n.Z*n.Y, n.Z*n.Z);
		return new Transform3D(new Basis(bx, by, bz), 2f * n.Dot(offset) * n);
	}
}
