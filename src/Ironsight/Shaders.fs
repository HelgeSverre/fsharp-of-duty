namespace Ironsight.Shell

[<RequireQualifiedAccess>]
module Shaders =
    let skyVertex = """
#version 410 core
out vec2 vUv;
void main() {
    vec2 position = vec2((gl_VertexID << 1) & 2, gl_VertexID & 2);
    vUv = position;
    gl_Position = vec4(position * 2.0 - 1.0, 0.9999, 1.0);
}
"""

    let skyFragment = """
#version 410 core
in vec2 vUv;
uniform float uYaw;
uniform float uPitch;
uniform float uAspect;
uniform float uTanHalfFov;
// Per-level sky. The defaults reproduce the overcast Normandy palette every
// map used when this was hardcoded, so a level that says nothing is unchanged.
uniform vec3 uSkyLow;
uniform vec3 uSkyHigh;
uniform vec3 uSkyCloud;
uniform vec3 uSkyRidge;
uniform float uSkyCloudAmount;
uniform float uSkyHaze;
out vec4 outColor;

float hash(vec2 p) { return fract(sin(dot(p, vec2(41.31, 289.17))) * 43758.5453); }
float noise(vec2 p) {
    vec2 i = floor(p), f = fract(p);
    f = f*f*(3.0-2.0*f);
    return mix(mix(hash(i), hash(i+vec2(1,0)), f.x),
               mix(hash(i+vec2(0,1)), hash(i+vec2(1,1)), f.x), f.y);
}
float fbm(vec2 p) {
    float value = 0.0, amplitude = 0.55;
    for (int i = 0; i < 4; ++i) {
        value += noise(p) * amplitude;
        p = p * 2.03 + vec2(7.1, 3.7);
        amplitude *= 0.48;
    }
    return value;
}

float ridgeProfile(float azimuth, float frequency, float phase) {
    float broad = sin(azimuth * frequency + phase) * 0.5 + 0.5;
    float detail = sin(azimuth * frequency * 2.0 - phase * 1.7) * 0.5 + 0.5;
    float peaks = pow(max(0.0, sin(azimuth * (frequency + 1.0) + phase * 2.1)), 5.0);
    return broad * 0.045 + detail * 0.022 + peaks * 0.105;
}

void main() {
    vec2 screen = vUv * 2.0 - 1.0;
    vec3 forward = normalize(vec3(sin(uYaw) * cos(uPitch),
                                  sin(uPitch),
                                  -cos(uYaw) * cos(uPitch)));
    vec3 right = normalize(cross(forward, vec3(0.0, 1.0, 0.0)));
    vec3 cameraUp = normalize(cross(right, forward));
    vec3 ray = normalize(forward
                       + right * screen.x * uAspect * uTanHalfFov
                       + cameraUp * screen.y * uTanHalfFov);

    float horizon = smoothstep(-0.08, 0.72, ray.y);
    vec3 color = mix(uSkyLow, uSkyHigh, horizon);

    // A broad overcast layer keeps the generated sky from reading as a flat
    // clear colour. Sampling from the world ray keeps it fixed while turning.
    vec2 cloudPosition = ray.xz * 3.8 + vec2(ray.y * 1.7, -ray.y * 2.1);
    float cloud = smoothstep(0.50, 0.78, fbm(cloudPosition));
    cloud *= (0.20 + 0.28 * (1.0 - horizon)) * uSkyCloudAmount;
    color = mix(color, uSkyCloud, cloud);

    // This is the same fixed world-space direction used by level lighting.
    vec3 sunDirection = normalize(vec3(-0.45, 0.82, 0.34));
    float towardSun = dot(ray, sunDirection);
    float sun = smoothstep(0.99955, 0.99992, towardSun);
    float glow = smoothstep(0.965, 0.9996, towardSun);
    color += vec3(0.32, 0.25, 0.14) * glow * 0.32;
    color = mix(color, vec3(1.0, 0.86, 0.58), sun * 0.88);

    // Two fixed azimuthal silhouettes add depth without geometry, draw calls,
    // or camera-position parallax that would expose the finite level bounds.
    float azimuth = atan(ray.x, -ray.z);
    float farRidge = 0.015 + ridgeProfile(azimuth, 3.0, 0.8);
    float nearRidge = 0.005 + ridgeProfile(azimuth, 5.0, 2.4) * 1.22;
    float farMask = 1.0 - smoothstep(farRidge, farRidge + 0.010, ray.y);
    float nearMask = 1.0 - smoothstep(nearRidge, nearRidge + 0.008, ray.y);
    color = mix(color, uSkyRidge, farMask * 0.82);
    color = mix(color, uSkyRidge * 0.52, nearMask * 0.88);
    float horizonMist = (1.0 - smoothstep(-0.01, 0.18, ray.y)) * (1.0 - nearMask * 0.65);
    color = mix(color, uSkyLow, horizonMist * uSkyHaze);

    color = pow(max(color, vec3(0.0)), vec3(1.0 / 2.2));
    outColor = vec4(color, 1.0);
}
"""

    let shadowVertex = """
#version 410 core
layout(location = 0) in vec3 aPosition;
uniform mat4 uLightViewProjection;
void main() { gl_Position = uLightViewProjection * vec4(aPosition, 1.0); }
"""

    let shadowFragment = """
#version 410 core
void main() { }
"""

    let hudVertex = """
#version 410 core
layout(location = 0) in vec2 aPosition;
layout(location = 1) in vec2 aUv;
layout(location = 2) in vec4 aColor;
uniform vec2 uResolution;
out vec2 vUv;
out vec4 vColor;
void main() {
    vec2 ndc = vec2(aPosition.x / uResolution.x * 2.0 - 1.0, 1.0 - aPosition.y / uResolution.y * 2.0);
    gl_Position = vec4(ndc, 0.0, 1.0);
    vUv = aUv;
    vColor = aColor;
}
"""

    let hudFragment = """
#version 410 core
in vec2 vUv;
in vec4 vColor;
uniform sampler2D uFont;
uniform vec2 uFontTexel;
out vec4 outColor;
void main() {
    float coverage = texture(uFont, vUv).r;
    float outline = 0.0;
    for (int x = -1; x <= 1; ++x)
        for (int y = -1; y <= 1; ++y)
            outline = max(outline, texture(uFont, vUv + vec2(x,y) * uFontTexel).r);
    float alpha = max(coverage, outline * 0.78) * vColor.a;
    vec3 rgb = mix(vec3(0.015), vColor.rgb, coverage);
    outColor = vec4(rgb, alpha);
}
"""

    let levelVertex = """
#version 410 core
layout(location = 0) in vec3 aPosition;
layout(location = 1) in vec3 aNormal;
layout(location = 2) in float aMaterial;
layout(location = 3) in vec2 aTexCoord;
uniform mat4 uViewProjection;
uniform mat4 uLightViewProjection;
out vec3 vWorld;
out vec3 vNormal;
out vec4 vLightPosition;
flat out int vMaterial;
out vec2 vTexCoord;
void main() {
    vWorld = aPosition;
    vNormal = aNormal;
    vLightPosition = uLightViewProjection * vec4(aPosition, 1.0);
    vMaterial = int(aMaterial + 0.5);
    vTexCoord = aTexCoord;
    gl_Position = uViewProjection * vec4(aPosition, 1.0);
}
"""

    let levelFragment = """
#version 410 core
in vec3 vWorld;
in vec3 vNormal;
in vec4 vLightPosition;
flat in int vMaterial;
in vec2 vTexCoord;
uniform vec3 uCamera;
uniform int uViewmodel;
uniform float uContrast;
uniform sampler2D uShadowMap;
uniform sampler2D uMaterialAtlas;
uniform int uAtlasColumns;
uniform int uAtlasRows;
uniform int uAtlasTileSize;
uniform int uAtlasFlipY;
uniform int uImpactCount;
uniform vec4 uImpacts[16];
out vec4 outColor;

float hash(vec2 p) { return fract(sin(dot(p, vec2(127.1, 311.7))) * 43758.5453); }
float noise(vec2 p) {
    vec2 i = floor(p), f = fract(p); f = f*f*(3.0-2.0*f);
    return mix(mix(hash(i), hash(i+vec2(1,0)), f.x), mix(hash(i+vec2(0,1)), hash(i+vec2(1,1)), f.x), f.y);
}
uniform float uHeatGlow;
vec3 materialColor() {
    if (vMaterial >= 100) {
        int layer = (vMaterial - 100) % 1000;
        vec2 cell = vec2(float(layer % uAtlasColumns), float(layer / uAtlasColumns));
        // Inset by one atlas texel so bilinear filtering never bleeds a
        // neighbouring authored material into this one at a repeat seam.
        vec2 repeated = fract(vTexCoord);
        if (uAtlasFlipY != 0) repeated.y = 1.0 - repeated.y;
        float tileSize = float(uAtlasTileSize);
        vec2 local = (repeated * (tileSize - 2.0) + 1.0) / tileSize;
        return texture(uMaterialAtlas, (cell + local) / vec2(float(uAtlasColumns), float(uAtlasRows))).rgb;
    }
    // Project the procedural pattern along the dominant face axis. Using xz
    // everywhere made vertical walls collapse into single-colour stripes.
    vec3 axis = abs(normalize(vNormal));
    vec2 p = axis.x > axis.y && axis.x > axis.z ? vWorld.zy
           : axis.y > axis.z ? vWorld.xz
           : vWorld.xy;
    if (vMaterial == 0) {
        vec2 cell = p * vec2(2.25, 4.0);
        cell.x += mod(floor(cell.y), 2.0) * 0.5;
        vec2 edge = min(fract(cell), 1.0-fract(cell));
        float brick = smoothstep(0.035, 0.085, edge.x) * smoothstep(0.045, 0.10, edge.y);
        vec3 mortar = vec3(0.30, 0.28, 0.25);
        vec3 clay = vec3(0.43,0.16,0.085) * (0.78 + 0.30*hash(floor(cell)));
        return mix(mortar, clay, brick) * (0.88 + 0.16*noise(p*3.1));
    }
    if (vMaterial == 1) return vec3(0.58,0.55,0.46) * (0.72 + 0.28*noise(p*0.7));
    if (vMaterial == 2) {
        float grain = noise(vec2(p.x * 0.34 + noise(p*0.2), p.y * 7.0));
        return mix(vec3(0.13,0.055,0.018), vec3(0.48,0.25,0.075), smoothstep(0.18,0.82,grain));
    }
    if (vMaterial == 3) return vec3(0.34,0.29,0.18) * (0.72 + 0.36*noise(p*1.4));
    if (vMaterial == 4) {
        float grit = noise(p*3.0);
        return mix(vec3(0.68,0.72,0.73), vec3(0.90,0.93,0.94), 0.55 + 0.45*grit);
    }
    if (vMaterial == 5) {
        float weave = 0.88 + 0.12*sin((p.x+p.y)*35.0);
        return vec3(0.42,0.36,0.22) * weave * (0.82 + 0.18*noise(p*5.0));
    }
    if (vMaterial == 6) return mix(vec3(0.075,0.085,0.09), vec3(0.20,0.22,0.22), noise(p*9.0));
    if (vMaterial == 7) return vec3(0.25,0.31,0.14) * (0.90 + 0.10*noise(p*5.0));
    if (vMaterial == 8) return vec3(0.27,0.30,0.26) * (0.90 + 0.10*noise(p*5.0));
    if (vMaterial == 9) return vec3(0.61,0.43,0.31) * (0.93 + 0.07*noise(p*4.0));
    if (vMaterial == 11) {
        // Desert sand: pale, with dune ripples and a scatter of darker grit.
        float ripple = sin(p.x * 1.6 + noise(p * 0.5) * 4.0) * 0.5 + 0.5;
        float grit = noise(p * 14.0);
        vec3 pale = vec3(0.80, 0.71, 0.53);
        vec3 shade = vec3(0.62, 0.53, 0.38);
        return mix(shade, pale, 0.45 + 0.35 * ripple + 0.20 * grit);
    }
    if (vMaterial == 12) {
        // Corroded steel: oxide blotches over dark metal, streaked downward.
        float blotch = noise(vec2(p.x * 2.2, p.y * 1.1));
        float streak = noise(vec2(p.x * 9.0, p.y * 0.6));
        vec3 oxide = mix(vec3(0.42, 0.20, 0.09), vec3(0.58, 0.31, 0.14), streak);
        return mix(vec3(0.19, 0.17, 0.16), oxide, smoothstep(0.35, 0.75, blotch * 0.7 + streak * 0.3));
    }
    if (vMaterial == 13) {
        // Poured concrete: flat grey, faint form lines, patchy staining.
        float stain = noise(p * 1.7) * 0.6 + noise(p * 6.0) * 0.4;
        float form = smoothstep(0.02, 0.06, abs(fract(p.y * 0.55) - 0.5));
        return vec3(0.52, 0.51, 0.48) * (0.80 + 0.24 * stain) * (0.90 + 0.10 * form);
    }
    if (vMaterial == 10) {
        // Sea: deep blue-green with a moving noise shimmer along the surface.
        float ripple = noise(p * 2.2) * 0.5 + noise(p * 7.0) * 0.5;
        return mix(vec3(0.10,0.20,0.24), vec3(0.22,0.36,0.38), ripple);
    }
    // Extended palette: paints, foams, tooling, and the flat fallback for
    // procedural glass. Numbered from 14 because Sand, RustedMetal and
    // Concrete took 11-13 (see Materials.all).
    if (vMaterial == 14) return vec3(0.95,0.06,0.08);
    if (vMaterial == 15) return vec3(0.04,0.30,0.98);
    if (vMaterial == 16) return vec3(0.08,0.90,0.22);
    if (vMaterial == 17) return vec3(1.00,0.82,0.04);
    if (vMaterial == 18) return vec3(0.68,0.08,0.92);
    if (vMaterial == 19) return vec3(1.00,0.28,0.03);
    if (vMaterial == 20) return vec3(0.04,0.22,0.72);
    if (vMaterial == 21) return vec3(1.00,0.32,0.02);
    if (vMaterial == 22) return vec3(0.035,0.040,0.045);
    if (vMaterial == 23) return vec3(0.05,0.52,0.88);
    if (vMaterial == 24) return vec3(0.075,0.10,0.12);
    if (vMaterial == 25) return vec3(0.55,0.72,0.78);
    return vec3(0.24,0.26,0.25);
}
void main() {
    vec3 normal = normalize(vNormal);
    vec3 sun = normalize(vec3(-0.45, 0.82, 0.34));
    float diffuse = max(dot(normal, sun), 0.0);
    float ambient = uViewmodel == 1 ? 0.62 : mix(0.24, 0.38, normal.y * 0.5 + 0.5);
    vec3 shadowCoord = vLightPosition.xyz / max(vLightPosition.w, 0.0001) * 0.5 + 0.5;
    float shadow = 1.0;
    if (uViewmodel == 0 && all(greaterThanEqual(shadowCoord, vec3(0.0))) && all(lessThanEqual(shadowCoord, vec3(1.0)))) {
        float bias = max(0.0012, 0.0045 * (1.0 - diffuse));
        vec2 texel = 1.0 / vec2(textureSize(uShadowMap, 0));
        float visibility = 0.0;
        for (int x = -1; x <= 1; ++x)
            for (int y = -1; y <= 1; ++y) {
                float closest = texture(uShadowMap, shadowCoord.xy + vec2(x,y)*texel).r;
                visibility += shadowCoord.z - bias > closest ? 0.0 : 1.0;
            }
        shadow = mix(0.34, 1.0, visibility / 9.0);
    }
    vec3 color = materialColor() * (ambient + diffuse * (1.0 - ambient) * shadow);
    // Barrel heat. Viewmodel metal only, and only forward of the receiver, so
    // the grips and the feed box stay cold while the barrels run orange.
    if (uViewmodel == 1 && uHeatGlow > 0.0 && vMaterial == 6) {
        float front = smoothstep(-0.45, -0.85, vWorld.z);
        float glow = uHeatGlow * uHeatGlow * front;
        color = mix(color, vec3(1.0, 0.28, 0.05), clamp(glow, 0.0, 0.92));
    }
    if (uViewmodel == 0) {
        float mark = 0.0;
        for (int i = 0; i < uImpactCount; ++i) {
            float distanceToMark = distance(vWorld, uImpacts[i].xyz);
            mark = max(mark, 1.0 - smoothstep(uImpacts[i].w * 0.25, uImpacts[i].w, distanceToMark));
        }
        color = mix(color, color * 0.16, mark * 0.82);
    }
    float distanceFog = uViewmodel == 1 ? 0.0 : smoothstep(43.0, 108.0, distance(uCamera, vWorld));
    color = mix(color, vec3(0.39,0.42,0.40), distanceFog);
    float luminance = dot(color, vec3(0.299, 0.587, 0.114));
    color = mix(vec3(luminance), color, uViewmodel == 1 ? 0.82 : 0.66);
    color *= vec3(1.01, 0.96, 0.84);
    color = (color - vec3(0.5)) * uContrast + vec3(0.5);
    color = pow(max(color, vec3(0.0)), vec3(1.0 / 2.2));
    outColor = vec4(color, 1.0);
}
"""
