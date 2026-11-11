shader_type canvas_item;

// Main menu backdrop: a dusk sky, layered hills and a road running to the horizon.

float hash(float x) { return fract(sin(x * 127.1) * 43758.5453); }

float noise1(float x) {
	float i = floor(x);
	float f = fract(x);
	f = f * f * (3.0 - 2.0 * f);
	return mix(hash(i), hash(i + 1.0), f);
}

float ridge(float x, float seed) {
	return noise1(x * 1.3 + seed) * 0.6 + noise1(x * 3.7 + seed * 2.0) * 0.28 + noise1(x * 9.0 + seed * 3.0) * 0.12;
}

void fragment() {
	vec2 uv = UV;
	float t = TIME * 0.01;
	vec3 top = vec3(0.035, 0.06, 0.12);
	vec3 mid = vec3(0.32, 0.22, 0.24);
	vec3 low = vec3(0.86, 0.47, 0.22);
	vec3 col = mix(top, mid, smoothstep(0.0, 0.5, uv.y));
	col = mix(col, low, smoothstep(0.38, 0.66, uv.y));
	// sun glow sitting on the horizon
	float sun = length((uv - vec2(0.62, 0.64)) * vec2(1.78, 1.0));
	col += vec3(1.0, 0.62, 0.3) * exp(-sun * 9.0) * 0.9;
	col += vec3(1.0, 0.85, 0.6) * smoothstep(0.03, 0.022, sun);
	// stars up high
	vec2 sg = floor(uv * vec2(260.0, 150.0));
	float star = step(0.9975, fract(sin(dot(sg, vec2(12.9898, 78.233))) * 43758.5453));
	col += vec3(0.8, 0.85, 1.0) * star * smoothstep(0.4, 0.0, uv.y) * 0.8;
	// three hill layers, darker towards the viewer
	for (int i = 0; i < 3; i++) {
		float fi = float(i);
		float h = 0.60 + fi * 0.07 - ridge(uv.x * (2.0 + fi * 1.4) + t * (1.0 + fi), fi * 11.0) * (0.12 - fi * 0.02);
		// pine tops on the ridge line
		float trees = abs(fract(uv.x * (70.0 + fi * 40.0) + fi * 0.37) - 0.5) * (0.035 - fi * 0.006);
		float m = smoothstep(h - trees, h - trees + 0.003, uv.y);
		vec3 hill = mix(vec3(0.20, 0.14, 0.16), vec3(0.015, 0.02, 0.025), (fi + 1.0) / 3.0);
		col = mix(col, hill, m);
	}
	// the road
	float ground = smoothstep(0.80, 0.81, uv.y);
	float depth = clamp((uv.y - 0.80) / 0.20, 0.0, 1.0);
	float cx = 0.5 + (1.0 - depth) * 0.10;
	float half_w = mix(0.004, 0.36, depth * depth);
	float road = smoothstep(half_w, half_w * 0.96, abs(uv.x - cx)) * ground;
	col = mix(col, vec3(0.012, 0.014, 0.018), ground);
	col = mix(col, vec3(0.05, 0.05, 0.055), road);
	float dash = step(0.5, fract(1.6 / (depth + 0.04) + TIME * 0.35));
	float line = smoothstep(half_w * 0.035, half_w * 0.02, abs(uv.x - cx)) * dash * ground;
	col = mix(col, vec3(0.55, 0.42, 0.14), line * 0.8);
	// vignette
	col *= 1.0 - 0.45 * pow(length(uv - 0.5) * 1.2, 2.0);
	COLOR = vec4(col, 1.0);
}
