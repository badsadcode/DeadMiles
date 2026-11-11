shader_type canvas_item;

// Final look, applied over the 3D view and under the HUD: soft vignette, fine film grain,
// a little lens fringing towards the corners, and grading that follows time, weather and health.

uniform sampler2D screen_tex : hint_screen_texture, filter_linear, repeat_disable;
uniform float vignette = 0.35;
uniform float grain = 0.03;
uniform float fringe = 0.0005;
uniform float saturation = 1.0;
uniform vec3 tint : source_color = vec3(1.0);
uniform float flash = 0.0;

float hash(vec2 p) {
	p = fract(p * vec2(443.897, 441.423));
	p += dot(p, p.yx + 19.19);
	return fract((p.x + p.y) * p.x);
}

void fragment() {
	vec2 uv = SCREEN_UV;
	vec2 c = uv - 0.5;
	float r2 = dot(c, c);
	vec2 off = c * r2 * fringe * 8.0;
	vec3 col;
	col.r = texture(screen_tex, uv + off).r;
	col.g = texture(screen_tex, uv).g;
	col.b = texture(screen_tex, uv - off).b;

	float luma = dot(col, vec3(0.299, 0.587, 0.114));
	col = mix(vec3(luma), col, saturation) * tint;
	// lift the shadows a touch towards blue, keep highlights warm
	col += vec3(-0.004, 0.0, 0.012) * (1.0 - smoothstep(0.0, 0.35, luma));

	float v = smoothstep(0.18, 0.85, r2 * 2.2);
	col *= 1.0 - v * vignette;
	float n = hash(uv * vec2(1920.0, 1080.0) + fract(TIME) * 61.0) - 0.5;
	col += n * grain * (1.0 - luma * 0.6);
	col += vec3(0.8, 0.85, 1.0) * flash;
	COLOR = vec4(col, 1.0);
}
