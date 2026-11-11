shader_type spatial;
render_mode cull_back, diffuse_burley;

// Asphalt ribbon. UV.x = lateral offset in metres from the centre line, UV.y = distance along the road.
// Worn paint, patches, cracks, gravel shoulders and rain puddles are all procedural.

#include "res://shaders/common.gdshaderinc"

global uniform float wetness;
global uniform vec3 sky_tint;

uniform float half_width = 3.6;

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
	float u = UV.x;
	float v = UV.y;
	float au = abs(u);
	float dist = length(VERTEX);
	float fade = clamp(1.0 - dist / 120.0, 0.0, 1.0);

	float n_patch = vnoise(vec2(u * 0.35, v * 0.06));
	float n_mid = vnoise(wpos.xz * 0.7);
	float n_fine = mix(0.5, vnoise(wpos.xz * 11.0) * 0.6 + vnoise(wpos.zx * 23.0) * 0.4, fade * fade);

	vec3 asphalt = mix(vec3(0.030, 0.031, 0.034), vec3(0.060, 0.059, 0.057), n_patch);
	asphalt *= 0.8 + 0.25 * n_mid + 0.3 * (n_fine - 0.5);
	// tyre tracks slightly polished
	float track = exp(-pow((au - 1.75) / 0.55, 2.0));
	asphalt *= 1.0 - 0.18 * track;
	// cracks
	float crack = smoothstep(0.035, 0.0, abs(vnoise(vec2(u * 1.3, v * 0.45)) - 0.5)) * fade;
	asphalt *= 1.0 - 0.55 * crack * step(0.55, vnoise(vec2(v * 0.02, 3.0)));

	// paint: dashed centre line and solid edge lines, worn away in places
	float wear = smoothstep(0.25, 0.75, vnoise(vec2(u * 2.0, v * 0.35)) + 0.25 * vnoise(vec2(u * 9.0, v * 3.0)));
	float centre = smoothstep(0.085, 0.06, au) * step(fract(v / 9.0), 0.42);
	float edge = smoothstep(0.075, 0.05, abs(au - (half_width - 0.35)));
	float paint = max(centre, edge) * mix(0.25, 0.95, wear);
	vec3 paint_col = mix(vec3(0.42, 0.40, 0.34), vec3(0.50, 0.40, 0.10), centre);
	vec3 col = mix(asphalt, paint_col, paint);

	// gravel shoulder
	float shoulder = smoothstep(half_width - 0.15, half_width + 0.25, au + (n_mid - 0.5) * 0.5);
	vec3 gravel = mix(vec3(0.10, 0.085, 0.065), vec3(0.17, 0.15, 0.12), n_mid) * (0.75 + 0.5 * n_fine);
	col = mix(col, gravel, shoulder);

	// rain: darker, glossier, with standing water in the low patches
	float puddle = smoothstep(0.55, 0.75, vnoise(vec2(u * 0.5 + 3.0, v * 0.13))) * smoothstep(0.35, 1.0, wetness) * (1.0 - shoulder);
	float wet = clamp(wetness + puddle, 0.0, 1.0);
	ALBEDO = col * mix(1.0, 0.5, wet) * cloud_shade(wpos.xz, TIME);
	ROUGHNESS = mix(mix(0.9, 0.97, shoulder), mix(0.28, 0.6, shoulder), wet) - puddle * 0.2;
	SPECULAR = 0.4;
	// cheap sky reflection at grazing angles (no reflection probes are used)
	float fres = pow(1.0 - clamp(dot(NORMAL, VIEW), 0.0, 1.0), 4.0);
	EMISSION = sky_tint * fres * wet * 0.35 * (1.0 - shoulder * 0.7);
}
