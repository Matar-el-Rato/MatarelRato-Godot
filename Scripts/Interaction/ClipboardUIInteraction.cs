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
		}
	}

	public override void _Input(InputEvent @event)
	{
		if (Viewport == null || Mesh == null) return;
		if (FocusController.Instance == null || !FocusController.Instance.IsFocusedOn(GetParent<Node3D>())) return;
		
		if (@event is InputEventMouse mouseEvent)
		{
			HandleMouseInput(mouseEvent);
			// We don't mark mouse as handled here because we want to allow mouse movement/unfocus etc.
			// HandleMouseInput inside will push mapped mouse events.
		}
		else if (@event is InputEventKey || @event is InputEventAction)
		{
			Viewport.PushInput(@event);
			GetViewport().SetInputAsHandled();
		}
	}

	private bool _wasHittingLastFrame = false;

	private void HandleMouseInput(InputEventMouse @event)
	{
		var camera = GetViewport().GetCamera3D();
		if (camera == null) return;

		// Use the current mouse position from the viewport for better consistency during transitions
		Vector2 mousePos = GetViewport().GetMousePosition();
		var from = camera.ProjectRayOrigin(mousePos);
		var to = from + camera.ProjectRayNormal(mousePos) * 10.0f;

		var spaceState = GetWorld3D().DirectSpaceState;
		var query = PhysicsRayQueryParameters3D.Create(from, to, 1);
		query.CollideWithAreas = true;
		var result = spaceState.IntersectRay(query);
		
		Node hitNode = result.Count > 0 ? result["collider"].As<Node>() : null;

		// Check if we hit the collision object associated with this clipboard
		// The collider is usually a StaticBody3D under Interactable/Collision
		bool isHitting = false;
		if (hitNode != null)
		{
			// Check if hitNode or its parents belong to this clipboard instance
			Node current = hitNode;
			while (current != null && current != GetTree().Root)
			{
				if (current == this || current == GetParent())
				{
					isHitting = true;
					break;
				}
				current = current.GetParent();
			}
		}

		if (isHitting)
		{
			Vector3 hitPos = (Vector3)result["position"];
			Vector2 uv = GetUVAtHit(hitPos);
			
			Vector2 viewportSize = Viewport.Size;
			Vector2 mappedPos = new Vector2(uv.X * viewportSize.X, uv.Y * viewportSize.Y);

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
		
		float width = 0.28f;
		float height = 0.4f;

		if (Mesh.Mesh is QuadMesh quad)
		{
			width = quad.Size.X;
			height = quad.Size.Y;
		}
		else
		{
			GD.PrintErr($"[ClipboardUIInteraction] Mesh.Mesh is NOT QuadMesh! It is {Mesh.Mesh?.GetType().Name}. Falling back to defaults.");
		}
		
		// Map local X [-width/2, width/2] to U [0, 1]
		float x = (localPos.X / width) + 0.5f;
		// Map local Y [height/2, -height/2] to V [0, 1] (V is top-to-bottom)
		float y = 0.5f - (localPos.Y / height);
		
		return new Vector2(Mathf.Clamp(x, 0, 1), Mathf.Clamp(y, 0, 1));
	}
}
