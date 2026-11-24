"use strict";

const fs = require("node:fs");
const path = require("node:path");
const { createHash } = require("node:crypto");

const maximumInputBytes = 7 * 1024 * 1024;
const maximumPixelCount = 1_600_000;
const maximumRows = 48;
const maximumCandidates = 64;
const maximumSparseRows = 24;
const modelPath = path.join(__dirname, "models", "en_PP-OCRv5_rec_mobile.onnx");
const gapModelPath = path.join(__dirname, "models", "PP-OCRv6_rec_small.onnx");
const gapModelHash = "6F327246B50388F3C176AE304BD95767EA6DC0C9AE92153EF8CBE210B3C14884";
const gapDictionaryHash = "F7AA897CA828A4C7C9E2739C30F9161A33306D532F020BCDB91DCFB664A5507E";
const gapDictionarySize = 18708;
const unsupported = { kind: "unsupported", rows: [] };

async function readRequest() {
  const chunks = [];
  let bytes = 0;
  for await (const chunk of process.stdin) {
    bytes += chunk.length;
    if (bytes > maximumInputBytes) throw new Error("OCR input exceeds the request limit.");
    chunks.push(chunk);
  }
  const { width, height, gray, sparseColumn, sparseRows, gapRow, gapAnchor } = JSON.parse(Buffer.concat(chunks, bytes).toString("utf8"));
  if (!Number.isInteger(width) || !Number.isInteger(height) ||
      width < 1 || height < 1 || width > 1800 || height > 900 || width * height > maximumPixelCount ||
      typeof gray !== "string" || gray.length !== Math.ceil(width * height / 3) * 4 ||
      !/^[A-Za-z0-9+/]+={0,2}$/.test(gray)) {
    throw new Error("Invalid OCR image.");
  }
  const pixels = Buffer.from(gray, "base64");
  if (pixels.length !== width * height) throw new Error("Incorrect grayscale image length.");
  if ([gapRow, gapAnchor].some(flag => flag !== undefined && typeof flag !== "boolean") ||
      (gapRow && gapAnchor) ||
      ((gapRow || gapAnchor) && (height < 12 || height > 22 || sparseColumn != null || sparseRows != null)) ||
      (gapRow && (width < 310 || width > 806)) ||
      (gapAnchor && (width < 24 || width > 309))) {
    throw new Error("Invalid focused OCR row.");
  }
  const sparse = readSparseRegions(width, height, sparseColumn, sparseRows);
  return { width, height, pixels, sparse, gapRow, gapAnchor };
}

function readSparseRegions(width, height, column, rows) {
  if (column == null && rows == null) return null;
  const frame = { left: 0, top: 0, right: width, bottom: height };
  const panel = readRegion(column, frame);
  if (!Array.isArray(rows) || rows.length < 1 || rows.length > maximumSparseRows) {
    throw new Error("Invalid sparse OCR row count.");
  }
  const slots = rows.map(row => readRegion(row, panel)).sort((a, b) => a.top - b.top);
  if (slots.some((slot, i) => slot.bottom - slot.top > 96 || (i > 0 && slot.top < slots[i - 1].bottom))) {
    throw new Error("Invalid sparse OCR row positions.");
  }
  return { panel, slots };
}

function readRegion(region, container) {
  if (!region || ![region.x, region.y, region.width, region.height].every(Number.isInteger) ||
      region.width < 1 || region.height < 1 || region.x < container.left || region.y < container.top ||
      region.x + region.width > container.right || region.y + region.height > container.bottom) {
    throw new Error("Invalid sparse OCR region.");
  }
  return { left: region.x, top: region.y, right: region.x + region.width, bottom: region.y + region.height, dark: false };
}

// ONNX model metadata includes the exact classifier alphabet.
function protobufFields(buffer) {
  const fields = [];
  let offset = 0;
  function integer() {
    let value = 0;
    let scale = 1;
    for (let i = 0; i < 10 && offset < buffer.length; i++) {
      const byte = buffer[offset++];
      value += (byte & 127) * scale;
      if (byte < 128) return value;
      scale *= 128;
    }
    throw new Error("Invalid ONNX metadata.");
  }
  while (offset < buffer.length) {
    const tag = integer();
    const wire = tag & 7;
    switch (wire) {
      case 0: integer(); break;
      case 1: offset += 8; break;
      case 5: offset += 4; break;
      case 2: {
        const length = integer();
        if (length > buffer.length - offset) throw new Error("Truncated ONNX metadata.");
        fields.push([Math.floor(tag / 8), buffer.subarray(offset, offset + length)]);
        offset += length;
        break;
      }
      default: throw new Error("Unsupported ONNX metadata field.");
    }
  }
  return fields;
}

function characterMap(model, gapRow = false) {
  for (const [field, entry] of protobufFields(model)) {
    if (field !== 14) continue;
    const parts = new Map(protobufFields(entry));
    if (parts.get(1)?.toString("utf8") !== "character") continue;
    const serialized = parts.get(2)?.toString("utf8");
    if (gapRow && (serialized === undefined ||
        createHash("sha256").update(serialized).digest("hex").toUpperCase() !== gapDictionaryHash)) break;
    let characters;
    try { characters = JSON.parse(serialized); }
    catch { characters = serialized?.split(/\r?\n/); }
    if (!Array.isArray(characters) || characters.some(value => typeof value !== "string")) break;
    if (characters.at(-1) === "") characters.pop();
    if (gapRow ? characters.length !== gapDictionarySize : characters.length < 90 || characters.length > 1024) break;
    return ["", ...characters, " "];
  }
  throw new Error("OCR model character map is missing.");
}

function longestRun(length, included, maximumGap) {
  let bestStart = 0, bestEnd = 0, start = 0, previous = -Infinity;
  for (let index = 0; index < length; index++) {
    if (!included(index)) continue;
    if (index - previous - 1 > maximumGap) start = index;
    if (index + 1 - start > bestEnd - bestStart) {
      bestStart = start;
      bestEnd = index + 1;
    }
    previous = index;
  }
  return [bestStart, bestEnd];
}

function findPanel({ width, height, pixels }) {
  if (width < 72 || height < 24) return null;
  const darkByRow = new Uint32Array(height);
  const lightByColumn = new Uint32Array(width);
  let lightPixels = 0;
  for (let y = 0; y < height; y++) {
    for (let x = 0; x < width; x++) {
      const pixel = pixels[y * width + x];
      if (pixel <= 21) darkByRow[y]++;
      if (pixel >= 210) {
        lightByColumn[x]++;
        lightPixels++;
      }
    }
  }
  const [top, bottom] = longestRun(height, y => darkByRow[y] >= width * .65, 12);
  if (bottom - top >= height * .58) {
    const [left, right] = longestRun(width, x => {
      let darkPixels = 0;
      for (let y = top; y < bottom; y++) if (pixels[y * width + x] <= 21) darkPixels++;
      return darkPixels >= (bottom - top) * .72;
    }, 2);
    if (right - left >= width * .62) {
      return { dark: true, left, top, right, bottom };
    }
  }
  if (width <= 900 && lightPixels >= width * height * .68) {
    const [left, right] = longestRun(width, x => lightByColumn[x] >= height * .65, 2);
    // Thin capture borders can be visible on every row, obscuring the text's
    // leading edge. A light run must enclose most of the source to use it.
    if (right - left >= width * .85) {
      return { dark: false, left, top: 0, right, bottom: height };
    }
  }
  return null;
}

const foreground = (pixel, dark) => dark ? pixel > 23 : pixel < 150;

function rowBands(image, panel) {
  const { width, pixels } = image;
  const { dark, left, right, top, bottom } = panel;
  const bands = [];
  for (let y = top; y < bottom; y++) {
    let count = 0;
    for (let x = left; x < right; x++) {
      if (foreground(pixels[y * width + x], dark)) count++;
    }
    if (count < 3 || count > (right - left) * .78) continue;
    const previous = bands.at(-1);
    if (previous && y - previous.bottom <= 2) {
      previous.bottom = y;
      previous.lastCount = count;
      previous.peakCount = Math.max(previous.peakCount, count);
    } else {
      bands.push({ top: y, bottom: y, firstCount: count, lastCount: count, peakCount: count });
    }
  }
  const heights = bands.filter(band => band.top > top && band.bottom < bottom - 1)
    .map(band => band.bottom - band.top + 1).filter(value => value >= 4 && value <= 22).sort((a, b) => a - b);
  const typicalHeight = heights[Math.floor(heights.length / 2)];
  const rows = [];
  for (const band of bands) {
    const height = band.bottom - band.top + 1;
    if (height < 4 || height > 22) continue;
    // Discard a strong ink boundary or partial-height fragment at a panel
    // edge. A full-height row with only faint ink on the last panel scanline
    // can still be complete when the surrounding capture border is visible.
    if (band.top === top && (!dark || top === 0 || height < typicalHeight * .78 ||
        !typicalHeight || band.firstCount > band.peakCount * .27)) continue;
    if (band.bottom === bottom - 1 && (!dark || bottom === image.height || height < typicalHeight * .78 ||
        !typicalHeight || band.lastCount > band.peakCount * .27)) continue;
    let first = right, last = left - 1;
    const columns = new Uint8Array(right - left);
    for (let y = band.top; y <= band.bottom; y++) {
      for (let x = left; x < right; x++) {
        if (!foreground(pixels[y * width + x], dark)) continue;
        columns[x - left] = 1;
        first = Math.min(first, x);
        last = Math.max(last, x);
      }
    }
    if (last - first + 1 < height * (dark ? 2 : 4)) continue;
    const gutters = [];
    let cursor = first;
    while (cursor < last) {
      if (columns[cursor - left]) {
        cursor++;
        continue;
      }
      const start = cursor;
      while (cursor < last && !columns[cursor - left]) cursor++;
      if (cursor - start >= Math.max(12, height * 1.5)) gutters.push([start, cursor]);
    }
    rows.push({ x: first, y: band.top, width: last - first + 1, height, gutters });
  }
  return rows;
}

function sparseRowBand(image, slot) {
  const { left, top, right, bottom } = slot;
  const occupied = new Uint8Array(right - left);
  let inkLeft = right, inkRight = left - 1, inkTop = bottom, inkBottom = top - 1;
  let light = 0, ink = 0;
  for (let y = top; y < bottom; y++) {
    for (let x = left; x < right; x++) {
      const pixel = image.pixels[y * image.width + x];
      if (pixel >= 210) light++;
      if (!foreground(pixel, false)) continue;
      if (ink > 0 && y - inkBottom > 4) return null;
      ink++;
      occupied[x - left] = 1;
      inkLeft = Math.min(inkLeft, x);
      inkRight = Math.max(inkRight, x);
      inkTop = Math.min(inkTop, y);
      inkBottom = y;
    }
  }
  const width = inkRight - inkLeft + 1, height = inkBottom - inkTop + 1;
  if (light < (right - left) * (bottom - top) * .68 || ink < 6 || width < 3 || height < 4 || height > 32 ||
      inkLeft === left || inkRight === right - 1 || inkTop === top || inkBottom === bottom - 1) return null;
  let gap = 0;
  for (let x = inkLeft; x <= inkRight; x++) {
    gap = occupied[x - left] ? 0 : gap + 1;
    if (gap >= Math.max(12, height * 1.5)) return null;
  }
  return { x: inkLeft, y: inkTop, width, height, clip: slot };
}

function simpleGeometry(rows, panel) {
  if (rows.length > maximumRows || rows.length < (panel.dark ? 2 : 3)) return false;
  if (!panel.dark) {
    const reference = rows.map(row => row.x).sort((a, b) => a - b)[Math.floor(rows.length / 2)];
    if (rows.filter(row => Math.abs(row.x - reference) <= Math.max(6, row.height)).length < rows.length * .8) {
      return false;
    }
    if (rows.filter(row => row.width >= (panel.right - panel.left) * .5).length < rows.length * .8) {
      return false;
    }
  }
  // Repeated horizontal gutters are likely real columns. Keep their layout
  // in the primary recognizer instead of collapsing each row to a string.
  for (const row of rows) {
    for (const [left, right] of row.gutters) {
      const center = (left + right) / 2;
      let matchingRows = 0;
      for (const other of rows) {
        if (other.gutters.some(([start, end]) => start <= center && center < end)) matchingRows++;
      }
      if (matchingRows >= Math.max(2, Math.ceil(rows.length * .34))) return false;
    }
  }
  return true;
}

function makeInput(ort, image, panel, row, variant) {
  const { padding, invert, normalize, contrast } = variant;
  const left = row.x - padding, top = row.y - padding;
  const sourceWidth = row.width + padding * 2, sourceHeight = row.height + padding * 2;
  const height = 48;
  const width = Math.min(2250, Math.ceil(sourceWidth * height / sourceHeight));
  const stride = Math.max(32, Math.ceil(width / 16) * 16);
  const values = new Float32Array(3 * height * stride);
  function read(x, y) {
    if (x < panel.left || x >= panel.right || y < panel.top || y >= panel.bottom) {
      return panel.dark ? 0 : 255;
    }
    return image.pixels[y * image.width + x];
  }
  for (let y = 0; y < height; y++) {
    const py = top + (y + .5) / height * sourceHeight - .5;
    const j = Math.floor(py), b = py - j;
    for (let x = 0; x < width; x++) {
      const px = left + (x + .5) / width * sourceWidth - .5;
      const i = Math.floor(px), a = px - i;
      let level = read(i, j) * (1 - a) * (1 - b) + read(i + 1, j) * a * (1 - b) +
                  read(i, j + 1) * (1 - a) * b + read(i + 1, j + 1) * a * b;
      if (normalize && panel.dark) level = Math.min(255, level * 1.4);
      if (contrast) level = Math.max(0, 255 - (255 - level) * 1.35);
      if (invert) level = 255 - level;
      const value = level / 127.5 - 1;
      for (let c = 0; c < 3; c++) values[(c * height + y) * stride + x] = value;
    }
  }
  return { tensor: new ort.Tensor("float32", values, [1, 3, height, stride]), left, sourceWidth, width, stride };
}

function decode(output, characters, expectedSteps) {
  const classes = output.dims.at(-1);
  const checkProbabilities = expectedSteps !== undefined;
  if (output.dims.length !== 3 || output.dims[0] !== 1 || classes !== characters.length ||
      (checkProbabilities && (output.type !== "float32" || output.dims[1] !== expectedSteps ||
        !(output.data instanceof Float32Array) || output.data.length !== expectedSteps * classes))) {
    throw new Error("Unexpected OCR output shape.");
  }
  const tokens = [];
  let previous = -1;
  for (let i = 0; i < output.data.length; i += classes) {
    let choice = -1, confidence = -Infinity;
    for (let c = 0; c < classes; c++) {
      const score = output.data[i + c];
      if (checkProbabilities && !(score >= 0 && score <= 1.001)) throw new Error("Invalid OCR confidence.");
      if (score <= confidence) continue;
      choice = c;
      confidence = score;
    }
    if (!Number.isFinite(confidence) || confidence < 0 || confidence > 1.001) {
      throw new Error("Invalid OCR confidence.");
    }
    const step = i / classes;
    if (choice > 0) {
      if (choice !== previous) {
        tokens.push({ text: characters[choice], first: step, last: step, confidence });
      } else {
        const token = tokens.at(-1);
        token.last = step;
        token.confidence = Math.max(token.confidence, confidence);
      }
    }
    previous = choice;
  }
  return tokens;
}

function describeWords(tokens, row, input, steps, image, panel, isInk = pixel => foreground(pixel, panel.dark)) {
  const groups = [];
  let group = null;
  for (const token of tokens) {
    if (!token.text.trim()) {
      group = null;
      continue;
    }
    if (!group) {
      group = { text: "", first: token.first, last: token.last };
      groups.push(group);
    }
    group.text += token.text;
    group.last = token.last;
  }
  const left = row.x;
  const right = row.x + row.width;
  const imageX = step => input.left + step * input.stride / steps * input.sourceWidth / input.width;
  const words = [];
  let beginning = left;
  for (let i = 0; i < groups.length; i++) {
    const group = groups[i];
    const next = groups[i + 1];
    const end = next
      ? Math.max(beginning + 1, Math.min(right - (groups.length - i - 1), Math.round(imageX((group.last + next.first + 1) / 2))))
      : right;
    if (end <= beginning || end > right) return [];
    let inkLeft = end, inkRight = beginning - 1;
    let inkTop = row.y + row.height, inkBottom = row.y - 1;
    for (let y = row.y; y < row.y + row.height; y++) {
      for (let x = beginning; x < end; x++) {
        if (!isInk(image.pixels[y * image.width + x])) continue;
        inkLeft = Math.min(inkLeft, x);
        inkRight = Math.max(inkRight, x);
        inkTop = Math.min(inkTop, y);
        inkBottom = Math.max(inkBottom, y);
      }
    }
    if (inkRight < inkLeft) return [];
    words.push({ text: group.text, bounds: {
      x: inkLeft, y: inkTop, width: inkRight - inkLeft + 1, height: inkBottom - inkTop + 1,
    } });
    beginning = end;
  }
  return words;
}

async function recognizeFocusedRow(image) {
  const { width, height, gapRow } = image;
  const panel = { left: 0, top: 0, right: width, bottom: height, dark: false };
  const response = { kind: gapRow ? "gap-row" : "gap-anchor", panel: { x: 0, y: 0, width, height }, rows: [] };
  const isInk = pixel => pixel < 225;
  if (!image.pixels.some(isInk)) return response;

  // Preserve two pixels of the source frame around the line, then reread
  // at the same paddings with and without local contrast enhancement.
  const band = { x: 2, y: 2, width: width - 4, height: height - 4 };
  const ort = require("./ort/dist/ort.node.min.js");
  ort.env.wasm.numThreads = 1;
  const model = fs.readFileSync(gapRow ? gapModelPath : modelPath);
  if (gapRow && createHash("sha256").update(model).digest("hex").toUpperCase() !== gapModelHash) {
    throw new Error("Invalid focused OCR model.");
  }
  const characters = characterMap(model, gapRow);
  const session = await ort.InferenceSession.create(model, { executionProviders: ["wasm"] });
  const candidates = new Map();
  try {
    for (const padding of gapRow ? [2, 3, 4] : [1, 2, 3, 4, 5]) {
      for (const contrast of [false, true]) {
        const input = makeInput(ort, image, panel, band, { padding, contrast });
        let output;
        try {
          const result = await session.run({ [session.inputNames[0]]: input.tensor });
          output = result[session.outputNames[0]];
          const steps = input.stride / 8;
          const tokens = decode(output, characters, steps);
          const text = tokens.map(token => token.text).join("");
          if (text.length > 512 || !text.trim() || /\p{Cc}/u.test(text)) continue;
          const confidence = Math.min(1, tokens.reduce((sum, token) => sum + token.confidence, 0) / tokens.length);
          if (confidence < .95) continue;
          const words = describeWords(tokens, band, input, steps, image, panel, isInk);
          if (words.length === 0 || words.length > 128) continue;
          const candidate = candidates.get(text) ?? { agreement: 0, paddings: new Set(), best: null };
          candidate.agreement++;
          candidate.paddings.add(padding);
          if (candidate.best === null || confidence > candidate.best.confidence) {
            candidate.best = { text, bounds: band, words, confidence };
          }
          candidates.set(text, candidate);
        } finally {
          output?.dispose();
          input.tensor.dispose();
        }
      }
    }
  } finally {
    await session.release();
  }
  const accepted = [...candidates.values()].filter(candidate => gapRow
    ? candidate.agreement >= 4 && candidate.paddings.size >= 2
    : candidate.agreement >= 2);
  accepted.sort((a, b) => b.agreement - a.agreement || b.best.confidence - a.best.confidence);
  response.rows = accepted.slice(0, gapRow ? 1 : 4).map(({ best, agreement }) => ({ ...best, agreement }));
  return response;
}

async function recognize(image) {
  if (image.gapRow || image.gapAnchor) return await recognizeFocusedRow(image);
  const { sparse } = image;
  const panel = sparse ? sparse.panel : findPanel(image);
  if (panel === null) return unsupported;
  const bands = sparse ? sparse.slots.map(slot => sparseRowBand(image, slot)).filter(Boolean) : rowBands(image, panel);
  if (bands.length === 0 || (!sparse && !simpleGeometry(bands, panel))) return unsupported;

  const ort = require("./ort/dist/ort.node.min.js");
  ort.env.wasm.numThreads = 1;
  const model = fs.readFileSync(modelPath);
  const characters = characterMap(model);
  const session = await ort.InferenceSession.create(model, { executionProviders: ["wasm"] });
  const variants = panel.dark ? [
    { invert: true, normalize: true, padding: 2 },
    { invert: false, normalize: false, padding: 2 },
    { invert: false, normalize: true, padding: 4 },
  ] : [
    { invert: false, normalize: false, padding: 2 },
    { invert: false, normalize: false, padding: 4 },
  ];
  if (sparse) variants.push({ contrast: true, padding: 3 });
  const rows = [];
  try {
    for (let index = 0; index < bands.length; index++) {
      const band = bands[index];
      const alternatives = new Map();
      const bounds = { x: band.x, y: band.y, width: band.width, height: band.height };
      for (const variant of variants) {
        const input = makeInput(ort, image, band.clip ?? panel, band, variant);
        const result = await session.run({ [session.inputNames[0]]: input.tensor });
        const output = result[session.outputNames[0]];
        const tokens = decode(output, characters);
        if (tokens.length === 0 || tokens.length > 1024) continue;
        const text = tokens.map(token => token.text).join("");
        const words = describeWords(tokens, band, input, output.dims[1], image, panel);
        if (words.length === 0) continue;
        const confidence = Math.min(1, tokens.reduce((sum, token) => sum + token.confidence, 0) / tokens.length);
        const existing = alternatives.get(text);
        const agreement = (existing?.agreement ?? 0) + 1;
        if (!existing || confidence > existing.confidence) {
          alternatives.set(text, { text, bounds, words, confidence, agreement });
        } else {
          existing.agreement = agreement;
        }
      }
      const limit = Math.floor((maximumCandidates - rows.length) / (bands.length - index));
      const ranked = [...alternatives.values()].sort((a, b) => b.agreement - a.agreement || b.confidence - a.confidence);
      rows.push(...(sparse ? ranked.filter(row => row.agreement >= 2) : ranked.slice(0, limit)));
    }
  } finally {
    await session.release();
  }
  return {
    kind: sparse ? "sparse-column" : panel.dark ? "dark" : "light",
    panel: { x: panel.left, y: panel.top, width: panel.right - panel.left, height: panel.bottom - panel.top },
    rows,
  };
}

async function main() {
  const result = await recognize(await readRequest());
  process.stdout.write(JSON.stringify(result));
}

main().catch(() => {
  process.stderr.write("Offline screenshot OCR failed.\n");
  process.exitCode = 1;
});
