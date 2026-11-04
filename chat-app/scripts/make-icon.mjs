// Draws build/icon.png (a speech bubble on a teal tile) with no image libraries.
import { mkdirSync, writeFileSync } from 'node:fs';
import { deflateSync } from 'node:zlib';

const SIZE = 512;
const TEAL = [15, 118, 110];
const WHITE = [255, 255, 255];

const roundedRect = (x, y, cx, cy, hw, hh, r) => {
  const dx = Math.abs(x - cx) - hw + r;
  const dy = Math.abs(y - cy) - hh + r;
  return Math.min(Math.max(dx, dy), 0) + Math.hypot(Math.max(dx, 0), Math.max(dy, 0)) - r;
};
const circle = (x, y, cx, cy, r) => Math.hypot(x - cx, y - cy) - r;
// Triangle for the bubble's tail, as the largest signed edge distance.
const tail = (x, y) => {
  const pts = [[150, 330], [250, 330], [140, 430]];
  let d = -Infinity;
  for (let i = 0; i < 3; i++) {
    const [ax, ay] = pts[i];
    const [bx, by] = pts[(i + 1) % 3];
    const len = Math.hypot(bx - ax, by - ay);
    d = Math.max(d, ((x - ax) * (by - ay) - (y - ay) * (bx - ax)) / len);
  }
  return d;
};
const cover = (d) => Math.min(1, Math.max(0, 0.5 - d));

const raw = Buffer.alloc((SIZE * 4 + 1) * SIZE);
let o = 0;
for (let py = 0; py < SIZE; py++) {
  raw[o++] = 0;
  for (let px = 0; px < SIZE; px++) {
    const x = px + 0.5;
    const y = py + 0.5;
    const tile = cover(roundedRect(x, y, 256, 256, 240, 240, 96));
    const bubble = cover(Math.min(roundedRect(x, y, 256, 228, 160, 112, 56), tail(x, y)));
    const dot = cover(Math.min(circle(x, y, 176, 228, 26), circle(x, y, 256, 228, 26), circle(x, y, 336, 228, 26)));
    const white = bubble * (1 - dot);
    for (let c = 0; c < 3; c++) raw[o++] = Math.round(TEAL[c] * (1 - white) + WHITE[c] * white);
    raw[o++] = Math.round(tile * 255);
  }
}

const crcTable = Array.from({ length: 256 }, (_, n) => {
  let c = n;
  for (let k = 0; k < 8; k++) c = c & 1 ? 0xedb88320 ^ (c >>> 1) : c >>> 1;
  return c >>> 0;
});
const crc32 = (buf) => {
  let c = 0xffffffff;
  for (const b of buf) c = crcTable[(c ^ b) & 255] ^ (c >>> 8);
  return (c ^ 0xffffffff) >>> 0;
};
const chunk = (type, data) => {
  const body = Buffer.concat([Buffer.from(type), data]);
  const out = Buffer.alloc(body.length + 8);
  out.writeUInt32BE(data.length, 0);
  body.copy(out, 4);
  out.writeUInt32BE(crc32(body), body.length + 4);
  return out;
};
const header = Buffer.alloc(13);
header.writeUInt32BE(SIZE, 0);
header.writeUInt32BE(SIZE, 4);
header[8] = 8; // bit depth
header[9] = 6; // RGBA

mkdirSync('build', { recursive: true });
writeFileSync(
  'build/icon.png',
  Buffer.concat([
    Buffer.from([0x89, 0x50, 0x4e, 0x47, 0x0d, 0x0a, 0x1a, 0x0a]),
    chunk('IHDR', header),
    chunk('IDAT', deflateSync(raw, { level: 9 })),
    chunk('IEND', Buffer.alloc(0)),
  ]),
);
