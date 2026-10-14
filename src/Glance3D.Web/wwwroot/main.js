// Glance3D in the browser: the JS half of the web host.
//
// This file only owns what the browser has and .NET doesn't: the WebGL2 context, DOM events
// and the frame loop. Every decision (loading, camera, input mapping, culling, draw order,
// picking, even the shader source) is made by Glance3D.Core running as WebAssembly, the same C#
// the desktop app runs. See src/Glance3D.Web/WebHost.cs for the exported surface.

import { dotnet } from './_framework/dotnet.js';

const $ = id => document.getElementById(id);
const overlay = $('overlay');
const say = (text, error = false) => {
  overlay.textContent = text;
  overlay.classList.toggle('error', error);
  overlay.hidden = !text;
};

// Shown in an iframe on spei.dev/glance3d: offer the full-page version.
if (window.self !== window.top) $('full').hidden = false;

// ----- runtime -------------------------------------------------------------

const started = performance.now();
const { getAssemblyExports, getConfig } = await dotnet.create();
const exports = await getAssemblyExports(getConfig().mainAssemblyName);
const G = exports.Glance3D.WebHost;
console.info(`[glance3d] runtime ready in ${Math.round(performance.now() - started)} ms`);

// Copy a MemoryView out of .NET memory and release it (the view pins the managed array).
const take = view => { const bytes = view.slice(); view.dispose(); return bytes; };

// ----- WebGL2 --------------------------------------------------------------

const canvas = $('gl');
const gl = canvas.getContext('webgl2', { antialias: true, alpha: false });
if (!gl) { say('This browser has no WebGL2.', true); throw new Error('no webgl2'); }

function compile(type, src) {
  const sh = gl.createShader(type);
  gl.shaderSource(sh, src);
  gl.compileShader(sh);
  if (!gl.getShaderParameter(sh, gl.COMPILE_STATUS)) throw new Error(gl.getShaderInfoLog(sh));
  return sh;
}
const prog = gl.createProgram();
gl.attachShader(prog, compile(gl.VERTEX_SHADER, G.Shader(true)));
gl.attachShader(prog, compile(gl.FRAGMENT_SHADER, G.Shader(false)));
gl.linkProgram(prog);
if (!gl.getProgramParameter(prog, gl.LINK_STATUS)) throw new Error(gl.getProgramInfoLog(prog));
const U = {};
for (const n of ['uView', 'uHalfTanY', 'uHalfTanZ', 'uNear', 'uFar', 'uTex', 'uBaseColor', 'uAlphaMode', 'uAlphaCutoff', 'uUnlit'])
  U[n] = gl.getUniformLocation(prog, n);

// Same state as the desktop host's OnLoad.
gl.enable(gl.DEPTH_TEST);
gl.cullFace(gl.BACK);
gl.frontFace(gl.CCW); // see MeshLoader.ToWorld: the Y/Z swap keeps glTF's CCW front faces
gl.blendFunc(gl.SRC_ALPHA, gl.ONE_MINUS_SRC_ALPHA);
gl.clearColor(0.12, 0.13, 0.15, 1);

const white = gl.createTexture();
gl.bindTexture(gl.TEXTURE_2D, white);
gl.texImage2D(gl.TEXTURE_2D, 0, gl.RGBA, 1, 1, 0, gl.RGBA, gl.UNSIGNED_BYTE, new Uint8Array([255, 255, 255, 255]));

let meshes = [], grid = null, textures = [];

function uploadMesh(i) {
  const vao = gl.createVertexArray();
  gl.bindVertexArray(vao);
  const vbo = gl.createBuffer();
  gl.bindBuffer(gl.ARRAY_BUFFER, vbo);
  gl.bufferData(gl.ARRAY_BUFFER, take(G.MeshVertices(i)), gl.STATIC_DRAW);
  const ebo = gl.createBuffer();
  const indices = take(G.MeshIndices(i));
  gl.bindBuffer(gl.ELEMENT_ARRAY_BUFFER, ebo);
  gl.bufferData(gl.ELEMENT_ARRAY_BUFFER, indices, gl.STATIC_DRAW);
  // RenderPlan.VertexFloats layout: position 3, uv 2, normal 3, color 4.
  const stride = 12 * 4;
  [[0, 3, 0], [1, 2, 3], [2, 3, 5], [3, 4, 8]].forEach(([loc, size, off]) => {
    gl.vertexAttribPointer(loc, size, gl.FLOAT, false, stride, off * 4);
    gl.enableVertexAttribArray(loc);
  });
  gl.bindVertexArray(null);
  return { vao, vbo, ebo, count: indices.byteLength / 4, edges: null, mat: i >= 0 ? G.MeshMaterial(i) : null };
}

// Wireframe needs a line list: WebGL has no PolygonMode. Built on first use per mesh.
//
// The ELEMENT_ARRAY_BUFFER binding is part of VAO state, so this must not run with the mesh's
// VAO bound: it once did, which silently replaced the mesh's triangle indices with line
// indices, and turning wireframe back off drew garbage triangles that looked like inverted
// culling. drawMesh binds the right index buffer explicitly on every draw instead.
function edgesOf(i, m) {
  if (m.edges) return m.edges;
  const data = take(G.MeshEdges(i));
  const ebo = gl.createBuffer();
  gl.bindVertexArray(null);
  gl.bindBuffer(gl.ELEMENT_ARRAY_BUFFER, ebo);
  gl.bufferData(gl.ELEMENT_ARRAY_BUFFER, data, gl.STATIC_DRAW);
  return (m.edges = { ebo, count: data.byteLength / 4 });
}

function uploadTexture(t) {
  const [w, h, nearest, wrapS, wrapT] = G.TextureInfo(t);
  const tex = gl.createTexture();
  gl.bindTexture(gl.TEXTURE_2D, tex);
  gl.pixelStorei(gl.UNPACK_ALIGNMENT, 1);
  gl.texImage2D(gl.TEXTURE_2D, 0, gl.RGBA, w, h, 0, gl.RGBA, gl.UNSIGNED_BYTE, take(G.TexturePixels(t)));
  gl.generateMipmap(gl.TEXTURE_2D);
  gl.texParameteri(gl.TEXTURE_2D, gl.TEXTURE_MIN_FILTER, nearest ? gl.NEAREST_MIPMAP_LINEAR : gl.LINEAR_MIPMAP_LINEAR);
  gl.texParameteri(gl.TEXTURE_2D, gl.TEXTURE_MAG_FILTER, nearest ? gl.NEAREST : gl.LINEAR);
  gl.texParameteri(gl.TEXTURE_2D, gl.TEXTURE_WRAP_S, wrapS || gl.REPEAT);
  gl.texParameteri(gl.TEXTURE_2D, gl.TEXTURE_WRAP_T, wrapT || gl.REPEAT);
  return tex;
}

function freeAll() {
  for (const m of [...meshes, grid].filter(Boolean)) {
    gl.deleteVertexArray(m.vao); gl.deleteBuffer(m.vbo); gl.deleteBuffer(m.ebo);
    if (m.edges) gl.deleteBuffer(m.edges.ebo);
  }
  textures.forEach(t => gl.deleteTexture(t));
  meshes = []; grid = null; textures = [];
}

async function loadBytes(bytes, name) {
  say(`Loading ${name}…`);
  await new Promise(requestAnimationFrame); // let the message paint before the synchronous load
  const t0 = performance.now();
  const err = G.Load(bytes, name, aspect());
  if (err) { say(err, true); return; }
  freeAll();
  for (let i = 0, n = G.MeshCount(); i < n; i++) meshes.push(uploadMesh(i));
  for (let t = 0, n = G.TextureCount(); t < n; t++) textures.push(uploadTexture(t));
  grid = uploadMesh(-1);
  console.info(`[glance3d] ${name} loaded in ${Math.round(performance.now() - t0)} ms`);
  say('');
  syncButtons();
}

// ----- input ---------------------------------------------------------------
// Translated into WebHost.Frame's input array; what each gesture *does* is decided in C#
// (Viewer.Update), exactly as on desktop.

const input = new Float64Array(21);
const keys = new Set();
const pointers = new Map(); // id -> {x, y}
let buttons = 0, dragX = 0, dragY = 0, wheel = 0;
let pressed = { frame: 0, reset: 0, mode: 0, wire: 0, grid: 0 };
let dbl = null, pinchDist = 0, pinchMid = null;

canvas.addEventListener('contextmenu', e => e.preventDefault());
canvas.addEventListener('pointerdown', e => {
  canvas.focus();
  try { canvas.setPointerCapture(e.pointerId); } catch { /* synthetic or already-released pointer */ }
  pointers.set(e.pointerId, { x: e.clientX, y: e.clientY });
  buttons = e.buttons;
  pinchDist = 0; pinchMid = null;
});
canvas.addEventListener('pointermove', e => {
  const p = pointers.get(e.pointerId);
  if (!p) return;
  if (pointers.size === 1) { dragX += e.clientX - p.x; dragY += e.clientY - p.y; buttons = e.buttons; }
  p.x = e.clientX; p.y = e.clientY;
  if (pointers.size === 2) {
    // Two fingers: midpoint movement pans (sent as a right-button drag), spread zooms (sent as
    // wheel notches, using Camera.DollyFactor's 0.85 per notch so a pinch tracks the fingers).
    const [a, b] = [...pointers.values()];
    const mid = { x: (a.x + b.x) / 2, y: (a.y + b.y) / 2 }, dist = Math.hypot(a.x - b.x, a.y - b.y);
    if (pinchMid) { dragX += mid.x - pinchMid.x; dragY += mid.y - pinchMid.y; buttons = 2; }
    if (pinchDist) wheel += Math.log(dist / pinchDist) / -Math.log(0.85);
    pinchMid = mid; pinchDist = dist;
  }
});
const up = e => { pointers.delete(e.pointerId); buttons = pointers.size ? buttons : 0; pinchDist = 0; pinchMid = null; };
canvas.addEventListener('pointerup', up);
canvas.addEventListener('pointercancel', up);
canvas.addEventListener('wheel', e => {
  e.preventDefault();
  // Pixel-mode wheels (trackpads) report many small deltas; ~100 px is one mouse notch.
  wheel += -e.deltaY / (e.deltaMode === 0 ? 100 : e.deltaMode === 1 ? 3 : 1);
}, { passive: false });
canvas.addEventListener('dblclick', e => {
  const r = canvas.getBoundingClientRect();
  dbl = [((e.clientX - r.left) / r.width) * 2 - 1, 1 - ((e.clientY - r.top) / r.height) * 2];
});

const bindings = { KeyF: 'frame', KeyR: 'reset', Tab: 'mode', KeyZ: 'wire', KeyG: 'grid' };
canvas.addEventListener('keydown', e => {
  if (e.ctrlKey || e.metaKey || e.altKey) return;
  if (bindings[e.code] && !e.repeat) pressed[bindings[e.code]] = 1;
  if (e.code === 'Tab' || e.code.startsWith('Arrow') || e.code === 'Space') e.preventDefault();
  keys.add(e.code);
});
canvas.addEventListener('keyup', e => keys.delete(e.code));
canvas.addEventListener('blur', () => keys.clear());
const axis = (p, n) => (keys.has(p) ? 1 : 0) - (keys.has(n) ? 1 : 0);

$('mode').onclick  = () => { pressed.mode = 1; canvas.focus(); };
$('wire').onclick  = () => { pressed.wire = 1; canvas.focus(); };
$('grid').onclick  = () => { pressed.grid = 1; canvas.focus(); };
$('frame').onclick = () => { pressed.frame = 1; canvas.focus(); };
$('open').onclick  = () => $('file').click();
$('file').onchange = async e => {
  const f = e.target.files[0];
  if (f) await loadBytes(new Uint8Array(await f.arrayBuffer()), f.name);
  e.target.value = '';
};

// Drop a .glb anywhere on the page.
let dragDepth = 0;
addEventListener('dragenter', e => { e.preventDefault(); dragDepth++; document.body.classList.add('dragging'); });
addEventListener('dragleave', () => { if (--dragDepth <= 0) { dragDepth = 0; document.body.classList.remove('dragging'); } });
addEventListener('dragover', e => e.preventDefault());
addEventListener('drop', async e => {
  e.preventDefault(); dragDepth = 0; document.body.classList.remove('dragging');
  const f = [...e.dataTransfer.files][0];
  if (f) await loadBytes(new Uint8Array(await f.arrayBuffer()), f.name);
});

let wireOn = false, gridOn = true;
function syncButtons() {
  $('status').textContent = G.Status();
  $('mode').textContent = G.Status().endsWith('fly') ? 'Fly' : 'Orbit';
  $('wire').setAttribute('aria-pressed', String(wireOn));
  $('grid').setAttribute('aria-pressed', String(gridOn));
}

// ----- frame loop ----------------------------------------------------------

const aspect = () => (canvas.clientHeight ? canvas.clientWidth / canvas.clientHeight : 1);

function resize() {
  const dpr = Math.min(window.devicePixelRatio || 1, 2);
  const w = Math.round(canvas.clientWidth * dpr), h = Math.round(canvas.clientHeight * dpr);
  if (canvas.width !== w || canvas.height !== h) { canvas.width = w; canvas.height = h; }
}

function drawMesh(m, i, wire) {
  const [r, g, b, a, alphaMode, cutoff, doubleSided, unlit, tex] = m.mat;
  if (doubleSided || wire) gl.disable(gl.CULL_FACE); else gl.enable(gl.CULL_FACE);
  gl.bindTexture(gl.TEXTURE_2D, tex >= 0 ? textures[tex] : white);
  gl.uniform4f(U.uBaseColor, r, g, b, a);
  gl.uniform1i(U.uAlphaMode, alphaMode);
  gl.uniform1f(U.uAlphaCutoff, cutoff);
  gl.uniform1i(U.uUnlit, unlit);
  // Build the edge list (if needed) before binding the VAO, then bind this draw's index buffer
  // into it explicitly: triangles and lines share one VAO but not one index buffer (see edgesOf).
  const e = wire ? edgesOf(i, m) : null;
  gl.bindVertexArray(m.vao);
  gl.bindBuffer(gl.ELEMENT_ARRAY_BUFFER, e ? e.ebo : m.ebo);
  gl.drawElements(e ? gl.LINES : gl.TRIANGLES, e ? e.count : m.count, gl.UNSIGNED_INT, 0);
}

let last = performance.now();
function frame(now) {
  requestAnimationFrame(frame);
  const dt = (now - last) / 1000; last = now;
  resize();

  input.set([
    dragX, dragY, buttons & 1, (buttons & 2) ? 1 : 0, (buttons & 4) ? 1 : 0,
    keys.has('ShiftLeft') || keys.has('ShiftRight') ? 1 : 0, wheel, canvas.clientHeight,
    axis('KeyW', 'KeyS'), axis('KeyD', 'KeyA'), axis('KeyE', 'KeyQ'),
    axis('ArrowRight', 'ArrowLeft'), axis('ArrowUp', 'ArrowDown'),
    pressed.frame, pressed.reset, pressed.mode, pressed.wire, pressed.grid,
    dbl ? 1 : 0, dbl ? dbl[0] : 0, dbl ? dbl[1] : 0,
  ]);
  const toggled = pressed.mode || pressed.wire || pressed.grid || dbl;
  dragX = dragY = wheel = 0; dbl = null;
  pressed = { frame: 0, reset: 0, mode: 0, wire: 0, grid: 0 };

  const f = G.Frame(Array.from(input), dt, aspect());

  gl.viewport(0, 0, canvas.width, canvas.height);
  gl.clear(gl.COLOR_BUFFER_BIT | gl.DEPTH_BUFFER_BIT);
  if (!f.length) return;

  wireOn = f[20] !== 0; gridOn = f[21] !== 0;
  if (toggled) syncButtons();

  gl.useProgram(prog);
  gl.uniformMatrix4fv(U.uView, false, f.slice(0, 16));
  gl.uniform1f(U.uHalfTanY, f[16]);
  gl.uniform1f(U.uHalfTanZ, f[17]);
  gl.uniform1f(U.uNear, f[18]);
  gl.uniform1f(U.uFar, f[19]);
  gl.uniform1i(U.uTex, 0);
  gl.activeTexture(gl.TEXTURE0);

  const nOpaque = f[22], nBlended = f[23];
  for (let k = 0; k < nOpaque; k++) { const i = f[24 + k]; drawMesh(meshes[i], i, wireOn); }

  // Grid and blended surfaces read depth but don't write it (same as the desktop host).
  gl.enable(gl.BLEND);
  gl.depthMask(false);
  if (gridOn && grid) {
    gl.disable(gl.CULL_FACE);
    gl.bindTexture(gl.TEXTURE_2D, white);
    gl.uniform4f(U.uBaseColor, 1, 1, 1, 1);
    gl.uniform1i(U.uAlphaMode, 2);
    gl.uniform1i(U.uUnlit, 1);
    gl.bindVertexArray(grid.vao);
    gl.drawElements(gl.LINES, grid.count, gl.UNSIGNED_INT, 0);
  }
  for (let k = 0; k < nBlended; k++) { const i = f[24 + nOpaque + k]; drawMesh(meshes[i], i, wireOn); }
  gl.depthMask(true);
  gl.disable(gl.BLEND);
}

// ----- start ---------------------------------------------------------------

// ?model=<url> opens that .glb instead of the sample (the host must allow CORS if it's
// another site), so a link can point straight at a model.
const requested = new URLSearchParams(location.search).get('model');
const url = requested || './cabin.glb';
const name = decodeURIComponent(url.split(/[?#]/)[0].split('/').pop() || 'model.glb');
say(`Loading ${name}…`);
try {
  const res = await fetch(url);
  if (!res.ok) throw new Error(`HTTP ${res.status}`);
  await loadBytes(new Uint8Array(await res.arrayBuffer()), name);
} catch (e) {
  say(`Could not load ${name} (${e.message}). Drop a .glb here.`, true);
}
requestAnimationFrame(frame);
