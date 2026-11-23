// Globe cell mesh (GpuScene): projection, inset and shading per vertex. Mirrors GpuProjection (C#) step for step.
// Shared body: GpuShaders prepends "#version 300 es" + precision (OpenGL ES 3.0 / WebGL2) or "#version 330 core".

uniform vec3 uRight;
uniform vec3 uUp;
uniform vec3 uCentre;
uniform float uScale;      // pixels per radian at the centre
uniform float uFlat;       // 0 sphere .. 1 azimuthal equidistant
uniform float uMaxAngle;   // radians; beyond it a point is clamped onto the cap edge
uniform vec2 uViewport;    // device-independent pixels
uniform float uEdgeInset;  // pixels the cell edge moves in (gap / 2)
uniform int uShaded;
uniform vec3 uLight;
uniform highp usampler2D uCellFlags; // bit 1: drawn from its tile block mesh this frame (skip here)

layout(location = 0) in vec3 aPos;
layout(location = 1) in vec3 aCentre;
layout(location = 2) in vec2 aUv;
layout(location = 3) in uint aCell;

out vec2 vUv;
flat out uint vCell;
flat out float vShade;

// Screen position (pixels, y down) in xy; z = 1 when visible, 0 when clamped to the cap edge.
vec3 project(vec3 p)
{
    float x = dot(p, uRight);
    float y = dot(p, uUp);
    float z = dot(p, uCentre);
    float s = sqrt(x * x + y * y);
    float theta = atan(s, z);
    float visible = theta <= uMaxAngle ? 1.0 : 0.0;
    theta = min(theta, uMaxAngle);
    float r = uScale * ((1.0 - uFlat) * sin(theta) + uFlat * theta);
    vec2 dir = vec2(z > 0.0 ? 0.0 : 1.0, 0.0);
    if (s > 1e-20) dir = vec2(x, y) / s;
    else if (z <= 0.0) visible = 0.0;
    return vec3(uViewport * 0.5 + vec2(r * dir.x, -r * dir.y), visible);
}

void main()
{
    vCell = aCell;
    vUv = aUv;
    vec3 c = project(aCentre);
    int cell = int(aCell);
    bool tiled = (texelFetch(uCellFlags, ivec2(cell % 2048, cell / 2048), 0).r & 2u) != 0u;
    if (c.z < 0.5 || tiled)
    {
        // Cell centre hidden, or drawn from its tiles: drop the whole cell (all its vertices land outside the clip volume).
        gl_Position = vec4(2.0, 2.0, 2.0, 1.0);
        vShade = 1.0;
        return;
    }

    vec2 q = project(aPos).xy;
    vec2 d = q - c.xy;
    float radius = length(d);
    if (radius > 0.0 && uEdgeInset > 0.0) q = c.xy + d * clamp(1.0 - uEdgeInset / (0.866 * radius), 0.3, 1.0);
    gl_Position = vec4(q.x / uViewport.x * 2.0 - 1.0, 1.0 - q.y / uViewport.y * 2.0, 0.0, 1.0);

    float shade = 1.0;
    if (uShaded != 0)
    {
        vec3 n = vec3(dot(aCentre, uRight), dot(aCentre, uUp), dot(aCentre, uCentre));
        shade = 0.35 + 0.65 * max(0.0, dot(n, uLight));
        shade += (1.0 - shade) * uFlat;
        shade = floor(shade * 32.0 + 0.5) / 32.0;
    }

    vShade = shade;
}
