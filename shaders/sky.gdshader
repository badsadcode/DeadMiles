shader_type sky;

// Sky for the whole day/night cycle: gradient, sun and moon discs, stars and two layers of
// drifting procedural cloud. All colours are driven from DayNightCycle / WeatherDirector.

uniform vec3 zenith_color : source_color = vec3(0.1, 0.25, 0.55);
uniform vec3 horizon_color : source_color = vec3(0.55, 0.68, 0.8);
uniform vec3 ground_color : source_color = vec3(0.12, 0.12, 0.11);
uniform vec3 sun_color : source_color = vec3(1.0, 0.95, 0.85);
uniform vec3 cloud_lit : source_color = vec3(1.0, 1.0, 1.0);
uniform vec3 cloud_dark : source_color = vec3(0.35, 0.38, 0.42);
uniform vec3 sun_dir = vec3(0.0, 1.0, 0.0);
uniform vec3 moon_dir = vec3(0.0, -1.0, 0.0);
uniform float sun_disc = 1.0;
uniform float star_intensity = 0.0;
uniform float cloud_cover = 0.3;
uniform float cloud_speed = 0.006;
uniform float flash = 0.0;

float hash(vec2 p) {
	vec3 p3 = fract(vec3(p.xyx) * 0.1031);
	p3 += dot(p3, p3.yzx + 33.33);
	return fract((p3.x + p3.y) * p3.z);
}

float hash3(vec3 p) {
	p = fract(p * vec3(0.1031, 0.1030, 0.0973));
	p += dot(p, p.yxz + 33.33);
	return fract((p.x + p.y) * p.z);
}

float vnoise(vec2 p) {
	vec2 i = floor(p);
	vec2 f = fract(p);
	f = f * f * (3.0 - 2.0 * f);
	return mix(mix(hash(i), hash(i + vec2(1.0, 0.0)), f.x),
			   mix(hash(i + vec2(0.0, 1.0)), hash(i + vec2(1.0, 1.0)), f.x), f.y);
}

float fbm(vec2 p) {
	float s = 0.0;
	float a = 0.5;
	for (int i = 0; i < 5; i++) {
		s += vnoise(p) * a;
		p = p * 2.07 + vec2(13.1, 7.7);
		a *= 0.5;
	}
	return s;
}

void sky() {
	vec3 dir = normalize(EYEDIR);
	float up = clamp(dir.y, 0.0, 1.0);
	vec3 col = mix(horizon_color, zenith_color, pow(up, 0.38));

	// glow around the sun, strongest near the horizon
	float sd = clamp(dot(dir, sun_dir), 0.0, 1.0);
	col += sun_color * pow(sd, 6.0) * 0.22 * sun_disc;
	col += sun_color * pow(sd, 48.0) * 0.5 * sun_disc;

	// stars
	if (star_intensity > 0.01 && dir.y > -0.05) {
		vec3 q = dir * 190.0;
		vec3 cell = floor(q);
		float h = hash3(cell);
		vec3 centre = cell + 0.5 + (vec3(hash3(cell + 1.7), hash3(cell + 5.3), hash3(cell + 9.1)) - 0.5) * 0.7;
		float d = length(q - centre);
		float star = smoothstep(0.3, 0.0, d) * step(0.965, h);
		float twinkle = 0.75 + 0.25 * sin(TIME * (2.0 + h * 5.0) + h * 40.0);
		col += vec3(0.9, 0.95, 1.0) * star * twinkle * star_intensity * smoothstep(-0.05, 0.25, dir.y) * (0.4 + 1.6 * fract(h * 17.0));
	}

	// sun and moon discs
	float disc = smoothstep(0.99972, 0.99987, dot(dir, sun_dir));
	vec3 sun_layer = sun_color * disc * 12.0 * sun_disc;
	float md = dot(dir, moon_dir);
	float moon = smoothstep(0.99955, 0.99975, md);
	float crater = 0.8 + 0.2 * vnoise(dir.xz * 420.0 + dir.y * 200.0);
	vec3 moon_layer = vec3(0.85, 0.9, 1.0) * moon * crater * 1.1 + vec3(0.25, 0.32, 0.45) * pow(clamp(md, 0.0, 1.0), 220.0) * 0.5;
	moon_layer *= smoothstep(-0.08, 0.05, moon_dir.y);

	// clouds on a virtual plane overhead
	float cloud = 0.0;
	vec3 ccol = vec3(0.0);
	if (dir.y > 0.0) {
		vec2 uv = dir.xz / (dir.y + 0.12);
		vec2 drift = vec2(TIME * cloud_speed, TIME * cloud_speed * 0.45);
		float n = fbm(uv * 0.75 + drift);
		float n2 = fbm(uv * 1.9 + drift * 1.7 + 5.0);
		float density = n * 0.72 + n2 * 0.28;
		float edge0 = 1.0 - cloud_cover * 0.98;
		cloud = smoothstep(edge0 - 0.12, edge0 + 0.2, density + cloud_cover * 0.25);
		cloud *= smoothstep(0.0, 0.14, dir.y);
		float thick = smoothstep(edge0, edge0 + 0.45, density + cloud_cover * 0.3);
		ccol = mix(cloud_lit, cloud_dark, clamp(thick * (0.55 + cloud_cover * 0.6), 0.0, 1.0));
		ccol += sun_color * pow(sd, 10.0) * 0.35 * (1.0 - thick) * sun_disc;
		// distant cloud fades into the horizon haze
		ccol = mix(horizon_color, ccol, smoothstep(0.0, 0.3, dir.y));
	}
	col += (sun_layer + moon_layer) * (1.0 - cloud);
	col = mix(col, ccol, cloud);

	// two ranges of far hills: depth beyond the edge of the streamed terrain
	vec2 az = normalize(dir.xz + vec2(0.00001));
	float r_far = 0.030 + 0.050 * (vnoise(az * 2.3 + 11.0) * 0.6 + vnoise(az * 6.1 + 3.0) * 0.3 + vnoise(az * 15.0) * 0.1);
	float r_near = 0.012 + 0.030 * (vnoise(az * 3.7 + 40.0) * 0.6 + vnoise(az * 9.5 + 7.0) * 0.4);
	vec3 far_col = mix(horizon_color, ground_color, 0.28);
	vec3 near_col = mix(horizon_color, ground_color, 0.55);
	col = mix(col, far_col, smoothstep(r_far + 0.003, r_far - 0.003, dir.y));
	col = mix(col, near_col, smoothstep(r_near + 0.003, r_near - 0.003, dir.y));

	// below the horizon
	col = mix(col, ground_color, smoothstep(0.0, -0.12, dir.y));
	col += vec3(0.75, 0.8, 1.0) * flash;
	COLOR = col;
}
