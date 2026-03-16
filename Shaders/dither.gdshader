shader_type canvas_item;

uniform sampler2D screen_texture : hint_screen_texture, filter_nearest;
uniform float color_depth : hint_range(2.0, 64.0, 1.0) = 8.0;
uniform float dither_strength : hint_range(0.0, 1.0) = 0.8;
uniform float pixel_size : hint_range(1.0, 8.0, 1.0) = 1.0;
uniform float contrast : hint_range(0.5, 3.0) = 1.3;
uniform float saturation : hint_range(0.0, 1.0) = 0.7;
uniform float brightness : hint_range(-0.5, 0.5) = 0.05;
uniform vec3 tint_color : source_color = vec3(1.0, 0.95, 0.85);
uniform float tint_strength : hint_range(0.0, 1.0) = 0.3;

// 8x8 Bayer dithering matrix (bit-interleave formula)
float bayer8(vec2 pos) {
	int x = int(pos.x) & 7;
	int y = int(pos.y) & 7;
	int xor_val = x ^ y;
	int result = ((y >> 2) & 1) | (((xor_val >> 2) & 1) << 1) |
	             (((y >> 1) & 1) << 2) | (((xor_val >> 1) & 1) << 3) |
	             ((y & 1) << 4) | ((xor_val & 1) << 5);
	return float(result) / 64.0;
}

void fragment() {
	// Pixelation - snap to grid for chunky retro pixels
	vec2 snapped_frag = floor(FRAGCOORD.xy / pixel_size) * pixel_size;
	vec2 snapped_uv = snapped_frag * SCREEN_PIXEL_SIZE;

	vec3 color = texture(screen_texture, snapped_uv).rgb;

	// Contrast (centered on midpoint)
	color = (color - 0.5) * contrast + 0.5;

	// Brightness
	color += brightness;

	// Desaturation
	float luma = dot(color, vec3(0.299, 0.587, 0.114));
	color = mix(vec3(luma), color, saturation);

	// Warm color tint
	color = mix(color, color * tint_color, tint_strength);

	// Ordered dithering with Bayer 8x8
	vec2 dither_pos = floor(FRAGCOORD.xy / pixel_size);
	float threshold = bayer8(dither_pos);

	float levels = color_depth - 1.0;
	vec3 dithered = color + (threshold - 0.5) * dither_strength / levels;
	color = floor(dithered * levels + 0.5) / levels;

	COLOR = vec4(clamp(color, 0.0, 1.0), 1.0);
}
