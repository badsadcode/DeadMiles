shader_type spatial;
render_mode cull_back, diffuse_burley, depth_draw_never, blend_mix;

// Lake water. Reads the depth buffer so the shallows are clear, the shoreline fades in softly
// instead of cutting a hard line, and a thin band of foam follows the bank.

global uniform vec3 sky_tint;
global uniform float wind_strength;
global uniform float wetness;

uniform sampler2D depth_tex : hint_depth_texture, filter_linear, repeat_disable;

varying vec3 wpos;

float hash(vec2 p) {
	vec3 p3 = fract(vec3(p.xyx) * 0.1031);
	p3 += dot(p3, p3.yzx + 33.33);
	return fract((p3.x + p3.y) * p3.z);
}

float vnoise(vec2 p) {
	vec2 i = floor(p);
	vec2 f = fract(p);
	f = f * f * (3.0 - 2.0 * f);
	return mix(mix(hash(i), hash(i + vec2(1.0, 0.0)), f.x),
			   mix(hash(i + vec2(0.0, 1.0)), hash(i + vec2(1.0, 1.0)), f.x), f.y);
}

void vertex() {
	wpos = (MODEL_MATRIX * vec4(VERTEX, 1.0)).xyz;
}

void fragment() {
	float sp = 0.22 + wind_strength * 0.7;
	vec2 p = wpos.xz;
	float a = vnoise(p * 0.8 + vec2(TIME * sp, TIME * sp * 0.6));
	float b = vnoise(p * 2.1 - vec2(TIME * sp * 0.8, TIME * sp * 1.1));
	float c = vnoise(p * 5.5 + vec2(-TIME * sp * 1.3, TIME * sp * 0.9));
	float chop = 0.12 + wind_strength * 0.3 + wetness * 0.15;
	vec3 n = normalize(vec3((a - 0.5) * chop + (c - 0.5) * chop * 0.5, 1.0, (b - 0.5) * chop + (c - 0.5) * chop * 0.5));
	NORMAL = normalize((VIEW_MATRIX * vec4(n, 0.0)).xyz);

	// how much water is between the surface and whatever is behind it
	float depth = texture(depth_tex, SCREEN_UV).r;
	vec4 upos = INV_PROJECTION_MATRIX * vec4(SCREEN_UV * 2.0 - 1.0, depth, 1.0);
	float scene_z = -upos.z / upos.w;
	float thickness = max(scene_z + VERTEX.z, 0.0);

	float fres = pow(1.0 - clamp(dot(NORMAL, VIEW), 0.0, 1.0), 3.0);
	vec3 deep = vec3(0.010, 0.030, 0.034);
	vec3 shallow = vec3(0.035, 0.075, 0.065);
	vec3 col = mix(shallow, deep, smoothstep(0.0, 1.6, thickness));

	float foam_band = smoothstep(0.14, 0.0, thickness);
	float foam = foam_band * smoothstep(0.35, 0.75, vnoise(p * 3.0 + vec2(TIME * 0.3, 0.0)) + foam_band * 0.5);
	col = mix(col, vec3(0.35, 0.40, 0.40), foam * 0.45);

	ALBEDO = col;
	ROUGHNESS = 0.06 + foam * 0.5;
	SPECULAR = 0.75;
	// reflect a deeper sky blue than the hazy horizon, and only strongly at grazing angles
	EMISSION = sky_tint * vec3(0.45, 0.62, 0.85) * (0.03 + fres * fres * 0.55) * (1.0 - foam * 0.5);
	ALPHA = clamp(smoothstep(0.0, 0.5, thickness) * 0.86 + fres * 0.25 + foam * 0.3, 0.0, 0.97);
}
