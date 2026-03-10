using Godot;
using System;

[Tool]
public partial class ClipboardUIInteraction : Node3D
{
	[Export] public SubViewport Viewport;
	[Export] public MeshInstance3D Mesh;
	[Export] public int SurfaceIndex = 0;

	private bool _isMouseInside = false;
	private Vector2 _lastMousePos = Vector2.Zero;

	public override void _Ready()
	{
		if (Viewport != null && Mesh != null)
		{
			// Ensure the mesh has a material and it's linked to the viewport
			var mat = Mesh.GetSurfaceOverrideMaterial(SurfaceIndex) as StandardMaterial3D;
			if (mat == null)
			{
				mat = new StandardMaterial3D();
				mat.Transparency = StandardMaterial3D.TransparencyEnum.Alpha;
				mat.ShadingMode = StandardMaterial3D.ShadingModeEnum.Unshaded;
				Mesh.SetSurfaceOverrideMaterial(SurfaceIndex, mat);
			}
			mat.AlbedoTexture = Viewport.GetTexture();
			GD.Print($"[ClipboardUIInteraction] Linked {Mesh.Name} to {Viewport.Name}");
		}
	}

	public override void _Input(InputEvent @event)
	{
		if (Viewport == null || Mesh == null) return;
		if (FocusController.Instance == null || !FocusController.Instance.IsFocusedOn(GetParent<Node3D>())) return;
		
		if (@event is InputEventMouse mouseEvent)
		{
			HandleMouseInput(mouseEvent);
		}
		else if (@event is InputEventKey || @event is InputEventAction)
		{
			// Forward keyboard input to viewport as well
			Viewport.PushInput(@event);
		}
	}

	private bool _wasHittingLastFrame = false;

	private void HandleMouseInput(InputEventMouse @event)
	{
		var camera = GetViewport().GetCamera3D();
		if (camera == null) return;

		var from = camera.ProjectRayOrigin(@event.Position);
		var to = from + camera.ProjectRayNormal(@event.Position) * 10.0f;

		var spaceState = GetWorld3D().DirectSpaceState;
		var query = PhysicsRayQueryParameters3D.Create(from, to, 1);
		query.CollideWithAreas = true;
		
		var result = spaceState.IntersectRay(query);
		
		// The collider is usually a StaticBody3D child of the MeshInstance3D
		Node hitNode = result.Count > 0 ? result["collider"].As<Node>() : null;
		bool isHitting = hitNode != null && (hitNode == Mesh || hitNode.GetParent() == Mesh || hitNode.GetParent()?.GetParent() == GetParent());

		if (isHitting)
		{
			Vector3 hitPos = (Vector3)result["position"];
			Vector2 uv = GetUVAtHit(hitPos);
			
			Vector2 viewportSize = Viewport.Size;
			Vector2 mappedPos = new Vector2(uv.X * viewportSize.X, uv.Y * viewportSize.Y);

			if (@event is InputEventMouseButton mb && mb.Pressed)
			{
				GD.Print($"[ClipboardUIInteraction] Click on {GetParent().Name} at UV: {uv}, Mapped: {mappedPos}");
			}

			var localEvent = (InputEvent)@event.Duplicate();
			if (localEvent is InputEventMouse mouseLocal)
			{
				mouseLocal.Position = mappedPos;
				mouseLocal.GlobalPosition = mappedPos;
			}
			Viewport.PushInput(localEvent);
			_wasHittingLastFrame = true;
		}
		else if (_wasHittingLastFrame)
		{
			// Send a mouse exit event to clear hovers
			var exitEvent = new InputEventMouseMotion();
			exitEvent.Position = new Vector2(-100, -100); // Off-screen
			Viewport.PushInput(exitEvent);
			_wasHittingLastFrame = false;
		}
	}

	private Vector2 GetUVAtHit(Vector3 hitPos)
	{
		Vector3 localPos = Mesh.ToLocal(hitPos);
		
		float width = 0.35f;
		float height = 0.5f;

		if (Mesh.Mesh is QuadMesh quad)
		{
			width = quad.Size.X;
			height = quad.Size.Y;
		}
		
		// Map local X [-width/2, width/2] to U [0, 1]
		float x = (localPos.X / width) + 0.5f;
		// Map local Y [height/2, -height/2] to V [0, 1] (V is top-to-bottom)
		float y = 0.5f - (localPos.Y / height);
		
		return new Vector2(Mathf.Clamp(x, 0, 1), Mathf.Clamp(y, 0, 1));
	}
}
