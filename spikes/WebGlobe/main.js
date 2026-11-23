// WebGL2 front end for the backend-neutral GPU scene (design 2026-11-23-gpu-map-rendering, phase 5).
// Loads the export written by `WorldEcon.Gui <db> --export-gpu <dir>` (served as ./scene/) and draws Globe View with the
// same shader files the desktop OpenGL backend compiles. The camera mirrors MapViewState (C#); keep the two in step.

const SCENE = './scene/';
const canvas = document.getElementById('map');
const status = document.getElementById('status');
const fpsLabel = document.getElementById('fps');
const layerSelect = document.getElementById('layer');
const gl = canvas.getContext('webgl2', { antialias: false });
if (!gl) throw new Error('WebGL2 is not available');

const fetchBin = async (name, Type) => new Type(await (await fetch(SCENE + name)).arrayBuffer());
const fetchText = async name => (await fetch(SCENE + name)).text();

// ---- camera: MapViewState mirror ----------------------------------------------------------------------------------
const dot = (a, b) => a[0] * b[0] + a[1] * b[1] + a[2] * b[2];
const scaleV = (a, s) => [a[0] * s, a[1] * s, a[2] * s];
const add = (a, b) => [a[0] + b[0], a[1] + b[1], a[2] + b[2]];
const sub = (a, b) => [a[0] - b[0], a[1] - b[1], a[2] - b[2]];
const norm = a => scaleV(a, 1 / Math.hypot(a[0], a[1], a[2]));
const cross = (a, b) => [a[1] * b[2] - a[2] * b[1], a[2] * b[0] - a[0] * b[2], a[0] * b[1] - a[1] * b[0]];

function maxAngleFor(f) {
  if (f >= 0.5) return Math.PI;
  const peak = Math.acos(-f / (1 - f));
  return Math.min(Math.min(Math.PI / 2 + Math.PI * f, Math.PI), peak);
}

function makeView(m, centre, scale, width, height) {
  const t = m.view.thresholds;
  const minScale = 0.45 * Math.min(width, height);
  const maxScale = 200 / m.view.tileAngle;
  scale = Math.min(Math.max(scale, minScale), Math.max(minScale, maxScale));
  const c = norm(centre);
  let u = sub([0, 1, 0], scaleV(c, dot([0, 1, 0], c)));
  if (Math.hypot(...u) < 1e-9) { const alt = Math.abs(c[0]) < 0.9 ? [1, 0, 0] : [0, 0, 1]; u = sub(alt, scaleV(c, dot(alt, c))); }
  u = norm(u);
  const right = cross(u, c);
  const regionPx = m.view.regionCellAngle * scale;
  const span = t.FlatFullPx - t.FlatStartPx;
  const tt = Math.min(Math.max((regionPx - t.FlatStartPx) / span, 0), 1);
  const flat = tt * tt * (3 - 2 * tt);
  const maxAngle = maxAngleFor(flat);
  const maxRadius = scale * ((1 - flat) * Math.sin(maxAngle) + flat * maxAngle);
  return { centre: c, up: u, right, scale, width, height, flat, maxAngle, maxRadius, minScale };
}

function pan(v, dx, dy) {
  const len = Math.hypot(dx, dy);
  if (len < 1e-12) return v.centre;
  const alpha = Math.min(len / v.scale, Math.PI);
  const dir = scaleV(add(scaleV(v.right, -dx), scaleV(v.up, dy)), 1 / len);
  return add(scaleV(v.centre, Math.cos(alpha)), scaleV(dir, Math.sin(alpha)));
}

// ---- GL helpers ---------------------------------------------------------------------------------------------------
function program(m, vsName, fsName, sources) {
  const compile = (type, name) => {
    const s = gl.createShader(type);
    gl.shaderSource(s, m.esHeader + sources[name]);
    gl.compileShader(s);
    if (!gl.getShaderParameter(s, gl.COMPILE_STATUS)) throw new Error(`${name}: ${gl.getShaderInfoLog(s)}`);
    return s;
  };
  const p = gl.createProgram();
  gl.attachShader(p, compile(gl.VERTEX_SHADER, vsName));
  gl.attachShader(p, compile(gl.FRAGMENT_SHADER, fsName));
  gl.linkProgram(p);
  if (!gl.getProgramParameter(p, gl.LINK_STATUS)) throw new Error(`${vsName}+${fsName}: ${gl.getProgramInfoLog(p)}`);
  const cache = new Map();
  p.u = name => { if (!cache.has(name)) cache.set(name, gl.getUniformLocation(p, name)); return cache.get(name); };
  return p;
}

function buffer(target, data) {
  const b = gl.createBuffer();
  gl.bindBuffer(target, b);
  gl.bufferData(target, data, gl.STATIC_DRAW);
  return b;
}

function texture(target) {
  const t = gl.createTexture();
  gl.bindTexture(target, t);
  for (const [k, v] of [[gl.TEXTURE_MIN_FILTER, gl.NEAREST], [gl.TEXTURE_MAG_FILTER, gl.NEAREST], [gl.TEXTURE_WRAP_S, gl.CLAMP_TO_EDGE], [gl.TEXTURE_WRAP_T, gl.CLAMP_TO_EDGE]])
    gl.texParameteri(target, k, v);
  return t;
}

function bindTex(p, unit, target, tex, name) {
  gl.activeTexture(gl.TEXTURE0 + unit);
  gl.bindTexture(target, tex);
  gl.uniform1i(p.u(name), unit);
}

// ---- load and upload ----------------------------------------------------------------------------------------------
const m = await (await fetch(SCENE + 'manifest.json')).json();
const sources = Object.fromEntries(await Promise.all(m.shaders.map(async n => [n, await fetchText('shaders/' + n)])));
status.textContent = 'Loading buffers…';
const [gv, gc, gi, ac, av, pal, geom, rv, rc, ri, yv, yg, plates] = await Promise.all([
  fetchBin('globe.vertices.f32', Float32Array), fetchBin('globe.cells.u32', Uint32Array), fetchBin('globe.indices.u32', Uint32Array),
  fetchBin('atlas.class.u8', Uint8Array), fetchBin('atlas.value.i16', Int16Array), fetchBin('palette.u8', Uint8Array),
  fetchBin('cellgeom.f32', Float32Array), fetchBin('rivers.vertices.f32', Float32Array), fetchBin('rivers.cells.u32', Uint32Array),
  fetchBin('rivers.indices.u32', Uint32Array), fetchBin('glyphs.vertices.f32', Float32Array), fetchBin('glyphs.groups.u32', Uint32Array),
  fetchBin('cellcolour.plates.u8', Uint8Array)]);

const P = {
  background: program(m, 'background.vert.glsl', 'background.frag.glsl', sources),
  globe: program(m, 'globe.vert.glsl', 'globe.frag.glsl', sources),
  river: program(m, 'river.vert.glsl', 'river.frag.glsl', sources),
  glyph: program(m, 'glyph.vert.glsl', 'glyph.frag.glsl', sources),
};

gl.pixelStorei(gl.UNPACK_ALIGNMENT, 1);
const globeVao = gl.createVertexArray();
gl.bindVertexArray(globeVao);
buffer(gl.ARRAY_BUFFER, gv);
const gs = m.floatsPerVertex * 4;
gl.vertexAttribPointer(0, 3, gl.FLOAT, false, gs, 0);
gl.vertexAttribPointer(1, 3, gl.FLOAT, false, gs, 12);
gl.vertexAttribPointer(2, 2, gl.FLOAT, false, gs, 24);
[0, 1, 2].forEach(i => gl.enableVertexAttribArray(i));
buffer(gl.ARRAY_BUFFER, gc);
gl.vertexAttribIPointer(3, 1, gl.UNSIGNED_INT, 4, 0);
gl.enableVertexAttribArray(3);
buffer(gl.ELEMENT_ARRAY_BUFFER, gi);

const riverVao = gl.createVertexArray();
gl.bindVertexArray(riverVao);
buffer(gl.ARRAY_BUFFER, rv);
const rs = m.rivers.floatsPerVertex * 4;
[[0, 3, 0], [1, 3, 12], [2, 2, 24], [3, 1, 32], [4, 1, 36], [6, 2, 40]].forEach(([loc, size, off]) => {
  gl.vertexAttribPointer(loc, size, gl.FLOAT, false, rs, off);
  gl.enableVertexAttribArray(loc);
});
buffer(gl.ARRAY_BUFFER, rc);
gl.vertexAttribIPointer(5, 2, gl.UNSIGNED_INT, 8, 0);
gl.enableVertexAttribArray(5);
buffer(gl.ELEMENT_ARRAY_BUFFER, ri);

const glyphVao = gl.createVertexArray();
gl.bindVertexArray(glyphVao);
buffer(gl.ARRAY_BUFFER, yv);
gl.vertexAttribPointer(0, 3, gl.FLOAT, false, 24, 0);
gl.vertexAttribPointer(1, 3, gl.FLOAT, false, 24, 12);
gl.enableVertexAttribArray(0);
gl.enableVertexAttribArray(1);
buffer(gl.ARRAY_BUFFER, yg);
gl.vertexAttribIPointer(2, 1, gl.UNSIGNED_INT, 4, 0);
gl.enableVertexAttribArray(2);
const emptyVao = gl.createVertexArray();
gl.bindVertexArray(null);

const texClass = texture(gl.TEXTURE_2D_ARRAY);
gl.texImage3D(gl.TEXTURE_2D_ARRAY, 0, gl.RGBA8UI, m.atlasSize, m.atlasSize, m.atlasLayers, 0, gl.RGBA_INTEGER, gl.UNSIGNED_BYTE, ac);
const texValue = texture(gl.TEXTURE_2D_ARRAY);
gl.texImage3D(gl.TEXTURE_2D_ARRAY, 0, gl.RGBA16I, m.atlasSize, m.atlasSize, m.atlasLayers, 0, gl.RGBA_INTEGER, gl.SHORT, av);
const texPalette = texture(gl.TEXTURE_2D);
gl.texImage2D(gl.TEXTURE_2D, 0, gl.RGBA8, m.palette.width, m.palette.rows, 0, gl.RGBA, gl.UNSIGNED_BYTE, pal);
const texCellColour = texture(gl.TEXTURE_2D);
gl.texImage2D(gl.TEXTURE_2D, 0, gl.RGBA8, m.cellTextureWidth, m.cellTextureRows, 0, gl.RGBA, gl.UNSIGNED_BYTE, plates);
const texFlags = texture(gl.TEXTURE_2D);
gl.texImage2D(gl.TEXTURE_2D, 0, gl.R8UI, m.cellTextureWidth, m.cellTextureRows, 0, gl.RED_INTEGER, gl.UNSIGNED_BYTE, new Uint8Array(m.cellTextureWidth * m.cellTextureRows));
const texGeom = texture(gl.TEXTURE_2D);
gl.texImage2D(gl.TEXTURE_2D, 0, gl.RGBA32F, m.cellTextureWidth, m.cellGeometryRows, 0, gl.RGBA, gl.FLOAT, geom);

for (const l of m.layers) layerSelect.add(new Option(l.name, String(l.mode)));
layerSelect.value = '1'; // Biome

// ---- frame --------------------------------------------------------------------------------------------------------
let centre = [0, 0, 1];
let scale = 0;
let dirty = true;

function viewUniforms(p, v) {
  gl.uniform3fv(p.u('uRight'), v.right);
  gl.uniform3fv(p.u('uUp'), v.up);
  gl.uniform3fv(p.u('uCentre'), v.centre);
  gl.uniform1f(p.u('uScale'), v.scale);
  gl.uniform1f(p.u('uFlat'), v.flat);
  gl.uniform1f(p.u('uMaxAngle'), v.maxAngle);
  gl.uniform2f(p.u('uViewport'), v.width, v.height);
}

function draw() {
  const ratio = window.devicePixelRatio || 1;
  const w = canvas.clientWidth, h = canvas.clientHeight;
  if (canvas.width !== Math.round(w * ratio) || canvas.height !== Math.round(h * ratio)) {
    canvas.width = Math.round(w * ratio);
    canvas.height = Math.round(h * ratio);
  }

  if (scale === 0) scale = 0.45 * Math.min(w, h);
  const v = makeView(m, centre, scale, w, h);
  scale = v.scale;
  gl.viewport(0, 0, canvas.width, canvas.height);
  gl.disable(gl.BLEND);

  let p = P.background;
  gl.useProgram(p);
  gl.uniform2f(p.u('uViewport'), w, h);
  gl.uniform1f(p.u('uPixelRatio'), ratio);
  gl.uniform1f(p.u('uMaxRadius'), v.maxRadius);
  gl.uniform3fv(p.u('uDiscColour'), m.view.discColour);
  gl.uniform3fv(p.u('uOuterColour'), m.view.outerColour);
  gl.bindVertexArray(emptyVao);
  gl.drawArrays(gl.TRIANGLES, 0, 3);

  p = P.globe;
  gl.useProgram(p);
  viewUniforms(p, v);
  gl.uniform1f(p.u('uEdgeInset'), 0); // borders off, as the desktop app's default
  gl.uniform1i(p.u('uShaded'), 0);
  gl.uniform3fv(p.u('uLight'), m.view.light);
  gl.uniform1i(p.u('uMode'), Number(layerSelect.value));
  gl.uniform1i(p.u('uHasAtlas'), m.atlasLayers > 0 ? 1 : 0);
  gl.uniform1i(p.u('uElevMin'), m.palette.elevationMin);
  gl.uniform1i(p.u('uElevMax'), m.palette.elevationMax);
  bindTex(p, 0, gl.TEXTURE_2D_ARRAY, texClass, 'uClass');
  bindTex(p, 1, gl.TEXTURE_2D_ARRAY, texValue, 'uValue');
  bindTex(p, 2, gl.TEXTURE_2D, texPalette, 'uPalette');
  bindTex(p, 3, gl.TEXTURE_2D, texCellColour, 'uCellColour');
  bindTex(p, 4, gl.TEXTURE_2D, texFlags, 'uCellFlags');
  gl.bindVertexArray(globeVao);
  gl.drawElements(gl.TRIANGLES, m.indexCount, gl.UNSIGNED_INT, 0);

  p = P.glyph;
  gl.useProgram(p);
  viewUniforms(p, v);
  gl.uniform1f(p.u('uEdgeInset'), 0);
  gl.uniform1i(p.u('uGroup'), 0);
  gl.uniform1f(p.u('uMinPx'), m.glyphs.minPx);
  const gcol = m.glyphs.colour;
  gl.uniform3f(p.u('uColour'), ((gcol >>> 16) & 255) / 255, ((gcol >>> 8) & 255) / 255, (gcol & 255) / 255);
  bindTex(p, 0, gl.TEXTURE_2D, texGeom, 'uCellGeom');
  gl.bindVertexArray(glyphVao);
  gl.drawArrays(gl.TRIANGLES, 0, m.glyphs.vertexCount);

  // Rivers: the coarsest course level whose tolerance is at most 1.5 px (MapRenderer.RiverCourseLevelFor).
  const levels = m.rivers.levels;
  let li = 0;
  for (let i = levels.length - 1; i >= 0; i--) if (levels[i].toleranceRad * v.scale <= m.rivers.courseTolerancePx) { li = i; break; }
  if (levels.length > 0 && levels[li].count > 0) {
    p = P.river;
    gl.useProgram(p);
    viewUniforms(p, v);
    gl.uniform1f(p.u('uPixelRatio'), ratio);
    gl.uniform1f(p.u('uUnitPx'), m.view.regionCellAngle * v.scale);
    gl.uniform1f(p.u('uMinFrac'), m.rivers.minFrac);
    gl.uniform1f(p.u('uAddFrac'), m.rivers.addFrac);
    gl.uniform1f(p.u('uMaxPx'), m.rivers.maxPx);
    gl.uniform1f(p.u('uMinLengthPx'), levels[li].minLengthPx);
    gl.uniform1i(p.u('uGroup'), 0);
    gl.uniform1f(p.u('uMinLogFlow'), -1e30);
    gl.uniform1f(p.u('uMaxLogFlow'), 1e30);
    bindTex(p, 0, gl.TEXTURE_2D, texPalette, 'uPalette');
    bindTex(p, 1, gl.TEXTURE_2D, texGeom, 'uCellGeom');
    gl.enable(gl.BLEND);
    gl.blendFunc(gl.ONE, gl.ONE_MINUS_SRC_ALPHA);
    gl.bindVertexArray(riverVao);
    gl.drawElements(gl.TRIANGLES, levels[li].count, gl.UNSIGNED_INT, levels[li].first * 4);
  }

  gl.bindVertexArray(null);
  return v;
}

// ---- input and loop -----------------------------------------------------------------------------------------------
let lastView = null;
let dragging = null;
canvas.addEventListener('pointerdown', e => { dragging = [e.clientX, e.clientY]; canvas.setPointerCapture(e.pointerId); });
canvas.addEventListener('pointerup', () => { dragging = null; });
canvas.addEventListener('pointermove', e => {
  if (!dragging || !lastView) return;
  centre = pan(lastView, e.clientX - dragging[0], e.clientY - dragging[1]);
  dragging = [e.clientX, e.clientY];
  dirty = true;
});
canvas.addEventListener('wheel', e => { e.preventDefault(); scale *= Math.pow(1.2, -Math.sign(e.deltaY)); dirty = true; }, { passive: false });
layerSelect.addEventListener('change', () => { dirty = true; });
window.addEventListener('resize', () => { dirty = true; });

// Diagnostic for scripted checks: ?spin=N pans 4 px per frame for N frames and reports frame intervals.
const spin = Number(new URLSearchParams(location.search).get('spin') || 0);
const intervals = [];
let lastT = 0;
let frames = 0;
let fpsT = performance.now();
function loop(t) {
  if (spin > 0 && intervals.length < spin && lastView) { centre = pan(lastView, 4, 0); dirty = true; }
  if (dirty) { lastView = draw(); dirty = false; frames++; }
  if (lastT && spin > 0 && intervals.length < spin) intervals.push(t - lastT);
  if (spin > 0 && intervals.length === spin && !window.spinResult) {
    const s = [...intervals].sort((a, b) => a - b);
    window.spinResult = { median: s[s.length >> 1], p95: s[Math.floor(s.length * 0.95)], max: s[s.length - 1], frames: s.length };
    status.textContent = `spin ${JSON.stringify(window.spinResult)}`;
  }
  lastT = t;
  if (t - fpsT > 1000) { fpsLabel.textContent = ` ${frames} draws/s`; frames = 0; fpsT = t; }
  requestAnimationFrame(loop);
}

const err = gl.getError();
status.textContent = err === gl.NO_ERROR
  ? `${m.cellCount.toLocaleString()} cells, ${m.rivers.pieceCount.toLocaleString()} river pieces — drag to pan, wheel to zoom`
  : `GL error 0x${err.toString(16)}`;
window.sceneReady = true;
requestAnimationFrame(loop);
