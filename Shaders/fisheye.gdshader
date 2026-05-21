shader_type canvas_item;

// Positive = pincushion/concave (edges curve inward). Start at 0.35, push higher for more.
uniform float k1 : hint_range(0.0, 1.0) = 0.35;
// Second-order term — leave at 0 unless you want to fine-tune the falloff curve.
uniform float k2 : hint_range(-0.5, 0.5) = 0.0;
// Zoom in to crop the black border left by pincushion. Match this to k1 strength.
uniform float zoom : hint_range(1.0, 2.0) = 1.15;

uniform sampler2D screen_texture : hint_screen_texture, filter_linear_mipmap;

void fragment() {
	vec2 uv = (SCREEN_UV - 0.5) / zoom;

	// SCREEN_PIXEL_SIZE = 1/viewport_size, so SCREEN_PIXEL_SIZE.y/SCREEN_PIXEL_SIZE.x = width/height.
	// Scaling uv.x by aspect ratio makes the radius metric circular rather than oval.
	float aspect = SCREEN_PIXEL_SIZE.y / SCREEN_PIXEL_SIZE.x;
	uv.x *= aspect;

	float r2 = dot(uv, uv);
	float r4 = r2 * r2;
	vec2 distorted = uv * (1.0 + k1 * r2 + k2 * r4);

	distorted.x /= aspect;
	distorted += 0.5;

	COLOR = texture(screen_texture, clamp(distorted, 0.001, 0.999));
}
