shader_type spatial;
render_mode cull_front, unshaded, depth_draw_always;

uniform vec4 outline_color : source_color = vec4(1.0, 1.0, 0.0, 1.0);
uniform float thickness : hint_range(0.001, 0.1) = 0.005;

void vertex() {
	// Screen-space outline: expand the silhouette in 2D clip space.
	// This works correctly for flat/thin meshes where all normals are
	// perpendicular to the surface (world-space expansion leaves gaps at edges).
	vec4 clip_pos    = PROJECTION_MATRIX * (MODELVIEW_MATRIX * vec4(VERTEX, 1.0));
	vec4 clip_normal = PROJECTION_MATRIX * (MODELVIEW_MATRIX * vec4(VERTEX + NORMAL, 1.0));

	vec2 screen_normal = normalize(
		clip_normal.xy / clip_normal.w - clip_pos.xy / clip_pos.w
	);

	// Push the outline outward in screen space, depth-corrected by w.
	clip_pos.xy += screen_normal * thickness * clip_pos.w;
	POSITION = clip_pos;
}

void fragment() {
	ALBEDO = outline_color.rgb;
	ALPHA = outline_color.a;
}
