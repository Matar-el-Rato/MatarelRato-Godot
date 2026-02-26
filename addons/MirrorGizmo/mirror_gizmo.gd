@tool
extends EditorPlugin

var _gizmo_plugin := MirrorGizmoPlugin.new()

func _enter_tree() -> void:
	add_node_3d_gizmo_plugin(_gizmo_plugin)

func _exit_tree() -> void:
	remove_node_3d_gizmo_plugin(_gizmo_plugin)


# ─── Gizmo plugin ─────────────────────────────────────────────────────────────

class MirrorGizmoPlugin extends EditorNode3DGizmoPlugin:

	func _init() -> void:
		# Light blue wire colour
		create_material("wire", Color(0.4, 0.8, 1.0))
		# Dashed line for the normal arrow
		create_material("normal", Color(0.4, 0.8, 1.0, 0.5))

	func _get_gizmo_name() -> String:
		return "Mirror"

	func _has_gizmo(node: Node3D) -> bool:
		# Match any node whose script path ends with Mirror.cs
		var s := node.get_script()
		return s != null and (s as Script).resource_path.ends_with("Mirror.cs")

	func _redraw(gizmo: EditorNode3DGizmo) -> void:
		gizmo.clear()
		var node := gizmo.get_node_3d()

		# Read export safely
		var size: Vector2 = Vector2(1, 2)
		if "MirrorSize" in node:
			size = node.MirrorSize

		var hw := size.x * 0.5
		var hh := size.y * 0.5

		# Wire rectangle outline
		var lines := PackedVector3Array([
			Vector3(-hw, -hh, 0), Vector3( hw, -hh, 0),
			Vector3( hw, -hh, 0), Vector3( hw,  hh, 0),
			Vector3( hw,  hh, 0), Vector3(-hw,  hh, 0),
			Vector3(-hw,  hh, 0), Vector3(-hw, -hh, 0),
			# diagonal cross so you can see the centre
			Vector3(-hw, -hh, 0), Vector3( hw,  hh, 0),
			Vector3( hw, -hh, 0), Vector3(-hw,  hh, 0),
		])
		gizmo.add_lines(lines, get_material("wire", gizmo), false)

		# Short normal arrow (shows which way the mirror faces)
		var arrow := PackedVector3Array([
			Vector3(0, 0, 0), Vector3(0, 0, 0.3),
		])
		gizmo.add_lines(arrow, get_material("normal", gizmo), false)
