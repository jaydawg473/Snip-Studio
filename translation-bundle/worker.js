'use strict';

// Snip Studio's local, text-only bridge. The bundled Bergamot module and models
// are loaded from this directory. No translation text is sent over a network.
const fs = require('node:fs');
const fsp = require('node:fs/promises');
const path = require('node:path');
const { fileURLToPath, pathToFileURL } = require('node:url');
const { Worker: NodeWorker } = require('node:worker_threads');

const ROOT = fs.realpathSync(__dirname);
const MAX_FRAME = 1024 * 1024;
const MAX_TEXT = 16000;
const MAX_FILE = 256 * 1024 * 1024;
const MAX_LOG_BYTES = 1024 * 1024;
const LANGUAGE = /^(en|de|es|fr)$/;
const FILE_NAME = /^[A-Za-z0-9][A-Za-z0-9._-]{0,179}$/;
const output = process.stdout;
let translator;
let engineFailed = false;
let failActiveEngine = null;
let currentId = 0;
let ending = false;
let failed = false;
let protocolFailed = false;
let processing = false;
const queue = [];
const liveWorkers = new Set();

// Upstream diagnostics can contain requests. Keep stdout exclusively framed and
// silence diagnostics in the bridge; worker-thread streams are drained below.
for (const name of ['log', 'info', 'warn', 'error', 'debug', 'trace']) console[name] = () => {};
globalThis.fetch = async () => { throw new Error('Network access is disabled.'); };

function withinRoot(value) {
  const relative = path.relative(ROOT, value);
  return relative !== '..' && !relative.startsWith('..' + path.sep) && !path.isAbsolute(relative);
}

function localFile(directory, name) {
  if (!FILE_NAME.test(name || '')) throw new Error('Invalid model file.');
  const resolved = fs.realpathSync(path.join(directory, name));
  if (!withinRoot(resolved) || path.dirname(resolved) !== directory) throw new Error('Invalid model location.');
  const stat = fs.statSync(resolved);
  if (!stat.isFile() || stat.size < 1 || stat.size > MAX_FILE) throw new Error('Invalid model size.');
  return resolved;
}

function markEngineFailed() {
  engineFailed = true;
  if (failActiveEngine) failActiveEngine(new Error('Local engine failed.'));
}

// Supply the WebWorker surface expected by Bergamot, while ensuring native
// worker stdout can never be confused with the bridge's binary protocol.
class QuietWorker {
  constructor(url) {
    if (!(url instanceof URL) || url.protocol !== 'file:') throw new Error('Invalid worker location.');
    const filename = fs.realpathSync(fileURLToPath(url));
    const expected = fs.realpathSync(path.join(ROOT, 'bergamot', 'worker', 'translator-worker.js'));
    if (filename !== expected || !withinRoot(filename)) throw new Error('Invalid worker location.');
    this.worker = new NodeWorker(filename, { stdout: true, stderr: true });
    liveWorkers.add(this.worker);
    let logBytes = 0;
    for (const stream of [this.worker.stdout, this.worker.stderr]) {
      stream.on('data', chunk => {
        logBytes += chunk.length;
        if (logBytes > MAX_LOG_BYTES) { markEngineFailed(); void this.worker.terminate(); }
      });
      stream.on('error', markEngineFailed);
    }
    this.worker.on('error', markEngineFailed);
    this.worker.on('exit', () => { liveWorkers.delete(this.worker); if (!failed) markEngineFailed(); });
  }
  addEventListener(event, callback) {
    if (event === 'message' || event === 'error') this.worker.on(event, data => callback({ data }));
  }
  postMessage(value) { this.worker.postMessage(value); }
  terminate() { return this.worker.terminate(); }
}
globalThis.Worker = QuietWorker;
globalThis.window = Object.freeze({ Worker: QuietWorker });

function safeConfig(value) {
  if (value === undefined) return {};
  if (!value || typeof value !== 'object' || Array.isArray(value) || Object.keys(value).length > 64) throw new Error('Invalid model configuration.');
  const result = {};
  for (const [key, item] of Object.entries(value)) {
    if (!/^[a-z][a-z0-9-]{0,63}$/.test(key)) throw new Error('Invalid model configuration.');
    // Model assets are supplied as buffers, never filenames in native options.
    if (['models', 'vocabs', 'shortlist', 'pretrained-model'].includes(key)) continue;
    const valid = item => (typeof item === 'string' && item.length <= 256 && !/[\x00-\x1f\x7f]/.test(item)) ||
      typeof item === 'boolean' || (typeof item === 'number' && Number.isFinite(item) && Math.abs(item) <= 1000000);
    if (Array.isArray(item)) {
      if (item.length > 16 || !item.every(valid)) throw new Error('Invalid model configuration.');
      result[key] = item.slice();
    } else {
      if (!valid(item)) throw new Error('Invalid model configuration.');
      result[key] = item;
    }
  }
  return result;
}

function readManifest() {
  const filename = localFile(ROOT, 'bundle.json');
  if (fs.statSync(filename).size > 256 * 1024) throw new Error('Invalid bundle manifest.');
  const data = JSON.parse(fs.readFileSync(filename, 'utf8'));
  if (!data || data.version !== 1 || !Array.isArray(data.models) || data.models.length < 1 || data.models.length > 12) throw new Error('Invalid bundle manifest.');
  const pairs = new Set();
  return data.models.map(entry => {
    if (!entry || typeof entry.source !== 'string' || typeof entry.target !== 'string' || !LANGUAGE.test(entry.source) || !LANGUAGE.test(entry.target) || entry.source === entry.target ||
        !['en'].includes(entry.source) && !['en'].includes(entry.target) ||
        entry.directory !== `${entry.source}-${entry.target}-tiny`) throw new Error('Invalid model entry.');
    const pair = `${entry.source}:${entry.target}`;
    if (pairs.has(pair)) throw new Error('Duplicate model entry.');
    pairs.add(pair);
    const directory = fs.realpathSync(path.join(ROOT, entry.directory));
    if (!withinRoot(directory) || path.dirname(directory) !== ROOT || !fs.statSync(directory).isDirectory()) throw new Error('Invalid model directory.');
    const files = entry.files;
    if (!files || typeof files !== 'object' || !Array.isArray(files.vocabs) || files.vocabs.length < 1 || files.vocabs.length > 2) throw new Error('Invalid model files.');
    return {
      from: entry.source,
      to: entry.target,
      files: {
        model: localFile(directory, files.model),
        shortlist: localFile(directory, files.shortlist),
        vocabs: files.vocabs.map(name => localFile(directory, name))
      },
      config: safeConfig(entry.config)
    };
  });
}

const manifestPromise = Promise.resolve().then(readManifest);
manifestPromise.catch(() => {});
let translatorPromise;

function getTranslator() {
  if (!translatorPromise) translatorPromise = (async () => {
    const registry = await manifestPromise;
    const { LatencyOptimisedTranslator, TranslatorBacking } = await import(pathToFileURL(path.join(ROOT, 'bergamot', 'translator.js')).href);
    const buffers = new Map();
    const load = filename => {
      if (!buffers.has(filename)) buffers.set(filename, fsp.readFile(filename).then(bytes => bytes.buffer.slice(bytes.byteOffset, bytes.byteOffset + bytes.byteLength)));
      return buffers.get(filename);
    };
    class LocalBacking extends TranslatorBacking {
      async loadModelRegistery() { return registry; }
      async loadTranslationModel({ from, to }) {
        const entry = registry.find(item => item.from === from && item.to === to);
        if (!entry) throw new Error('Language pair is not installed.');
        const [model, shortlist, ...vocabs] = await Promise.all([
          load(entry.files.model), load(entry.files.shortlist), ...entry.files.vocabs.map(load)
        ]);
        return { model, shortlist, vocabs, config: entry.config };
      }
    }
    // initialize() forwards options through structured clone into the worker.
    // Error callbacks must stay on the backing object, outside that payload.
    const options = { pivotLanguage: 'en', useNativeIntGemm: false, cacheSize: 0, downloadTimeout: 0 };
    const backing = new LocalBacking(options);
    backing.onerror = markEngineFailed;
    translator = new LatencyOptimisedTranslator(options, backing);
    return translator;
  })();
  return translatorPromise;
}

function validRequest(value) {
  if (!value || typeof value !== 'object' || Array.isArray(value) || !Number.isInteger(value.id) || value.id < 1 || value.id > 2147483647 || value.command !== 'Translate') return false;
  const data = value.data;
  return data && typeof data === 'object' && !Array.isArray(data) && typeof data.src === 'string' && typeof data.trg === 'string' && LANGUAGE.test(data.src) && LANGUAGE.test(data.trg) &&
    data.src !== data.trg && typeof data.text === 'string' && data.text.length > 0 && data.text.length <= MAX_TEXT &&
    data.text.trim().length > 0 && !data.text.includes('\0') && data.text.isWellFormed() && data.html === false;
}

function writeFrame(value) {
  let body = Buffer.from(JSON.stringify(value), 'utf8');
  if (body.length > MAX_FRAME) body = Buffer.from(JSON.stringify({ id: value.id, success: false, error: 'The translation result is too large.' }), 'utf8');
  const header = Buffer.allocUnsafe(4);
  header.writeUInt32LE(body.length);
  return new Promise((resolve, reject) => {
    output.write(Buffer.concat([header, body]), error => error ? reject(error) : resolve());
  });
}

async function handle(request) {
  currentId = Number.isInteger(request?.id) && request.id > 0 && request.id <= 2147483647 ? request.id : 0;
  if (!validRequest(request)) {
    await writeFrame({ id: currentId, success: false, error: 'Choose installed source and target languages and at most 16,000 characters.' });
    return;
  }
  let fail;
  const failure = new Promise((resolve, reject) => { fail = reject; });
  failure.catch(() => {});
  failActiveEngine = fail;
  try {
    const local = await getTranslator();
    if (engineFailed) throw new Error('Local engine failed.');
    const result = await Promise.race([local.translate({ from: request.data.src, to: request.data.trg, text: request.data.text, html: false }), failure]);
    if (!result || !result.target || typeof result.target.text !== 'string') throw new Error('Invalid engine result.');
    if (!failed && !protocolFailed) await writeFrame({ id: request.id, success: true, data: { target: { text: result.target.text } } });
  } catch {
    if (!failed && !protocolFailed) await writeFrame({ id: request.id, success: false, error: 'Local translation could not finish. Reinstall the language files or try another installed language pair.' });
  } finally {
    failActiveEngine = null;
    currentId = 0;
  }
}

async function shutdown(code) {
  if (failed) return;
  failed = true;
  process.stdin.pause();
  process.stdin.destroy();
  queue.length = 0;
  try { if (translator) void translator.delete().catch(() => {}); } catch {}
  await Promise.allSettled(Array.from(liveWorkers, worker => worker.terminate()));
  process.exitCode = code;
}

async function pump() {
  if (processing || failed) return;
  processing = true;
  try { while (queue.length && !failed) await handle(queue.shift()); }
  catch { await shutdown(1); }
  finally {
    processing = false;
    if (ending && !queue.length) await shutdown(0);
  }
}

function rejectProtocol(message) {
  if (failed || protocolFailed) return;
  protocolFailed = true;
  process.stdin.pause();
  process.stdin.destroy();
  void writeFrame({ id: 0, success: false, error: message }).then(() => shutdown(1), () => shutdown(1));
}

let header = Buffer.alloc(4), headerBytes = 0, body = null, bodyBytes = 0;
const decoder = new TextDecoder('utf-8', { fatal: true });
process.stdin.on('data', chunk => {
  if (failed || protocolFailed) return;
  let offset = 0;
  try {
    while (offset < chunk.length) {
      if (body === null) {
        const count = Math.min(4 - headerBytes, chunk.length - offset);
        chunk.copy(header, headerBytes, offset, offset + count);
        headerBytes += count; offset += count;
        if (headerBytes < 4) continue;
        const length = header.readUInt32LE(0);
        if (length < 2 || length > MAX_FRAME) throw new Error('Invalid frame.');
        body = Buffer.allocUnsafe(length); bodyBytes = 0; headerBytes = 0;
      }
      const count = Math.min(body.length - bodyBytes, chunk.length - offset);
      chunk.copy(body, bodyBytes, offset, offset + count);
      bodyBytes += count; offset += count;
      if (bodyBytes === body.length) {
        if (queue.length >= 1) throw new Error('Too many requests.');
        queue.push(JSON.parse(decoder.decode(body)));
        body = null; bodyBytes = 0;
        void pump();
      }
    }
  } catch {
    rejectProtocol('The translation request is invalid.');
  }
});
process.stdin.on('end', () => {
  if (failed || protocolFailed) return;
  ending = true;
  if (headerBytes || body !== null) {
    rejectProtocol('The translation request is incomplete.');
  } else if (!processing && !queue.length) void shutdown(0);
});
process.stdin.on('error', () => { void shutdown(1); });
output.on('error', () => { void shutdown(1); });
process.on('uncaughtException', () => { void shutdown(1); });
process.on('unhandledRejection', () => { void shutdown(1); });
