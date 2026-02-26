shader_type spatial;
render_mode unshaded, cull_disabled;

uniform sampler2D mirror_texture : source_color, filter_linear_mipmap, repeat_disable;

void vertex() {
	// Flip horizontally so the reflection appears un-mirrored
	UV.x = 1.0 - UV.x;
}

void fragment() {
	// Handle viewing from back face
	vec2 uv = FRONT_FACING ? UV : vec2(1.0 - UV.x, UV.y);

	// Discard pixels outside the mirror bounds (prevents tiling artifacts from frustum offset)
	if (uv.x < 0.0 || uv.x > 1.0 || uv.y < 0.0 || uv.y > 1.0) {
		discard;
	}

	ALBEDO = texture(mirror_texture, uv).rgb;
}
