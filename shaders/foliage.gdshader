shader_type spatial;
render_mode cull_disabled, depth_draw_opaque, diffuse_burley, specular_disabled;

// Leaves, grass and procedural canopies. Alpha-tested (never blended) so it sorts and shadows correctly,
// with a cheap height-weighted wind sway driven by the weather system.

uniform bool use_tex = true;
uniform sampler2D albedo_tex : source_color, filter_linear_mipmap, repeat_disable;
uniform vec3 tint : source_color = vec3(1.0);
uniform float sway = 1.0;
uniform float alpha_cut = 0.35;

#include "res://shaders/common.gdshaderinc"

global uniform float wind_strength;
global uniform float wetness;

varying vec2 world_xz;

void vertex() {
	vec3 wp = (MODEL_MATRIX * vec4(VERTEX, 1.0)).xyz;
	world_xz = wp.xz;
	float h = max(VERTEX.y, 0.0);
	float ph = wp.x * 0.31 + wp.z * 0.23;
	float gust = sin(TIME * 1.1 + ph) + 0.5 * sin(TIME * 2.7 + ph * 1.9) + 0.25 * sin(TIME * 5.3 + ph * 3.1);
	float w = gust * (0.15 + wind_strength) * sway * 0.035 * h;
	VERTEX.x += w;
	VERTEX.z += w * 0.6;
}

void fragment() {
	vec3 col = COLOR.rgb * tint;
	if (use_tex) {
		vec4 c = texture(albedo_tex, UV);
		col *= c.rgb;
		ALPHA = c.a;
		ALPHA_SCISSOR_THRESHOLD = alpha_cut;
	}
	ALBEDO = col * mix(1.0, 0.72, wetness) * cloud_shade(world_xz, TIME);
	ROUGHNESS = mix(0.9, 0.55, wetness);
	BACKLIGHT = col * 0.35;
	if (!FRONT_FACING) {
		NORMAL = -NORMAL;
	}
}
