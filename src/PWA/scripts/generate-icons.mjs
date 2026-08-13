/**
 * Draws the home-screen icons.
 *
 * iOS will not use an SVG for a home-screen icon, so a PNG has to exist somewhere.
 * Rather than commit four opaque binaries nobody can review or re-derive, the
 * pixels are generated here: the encoder is about sixty lines of zlib and CRC,
 * and the artwork is a few signed-distance shapes. Changing the colour of the
 * icon is then a diff you can read, not a binary you have to trust.
 *
 * Run with `npm run icons`.
 */
import { deflateSync } from 'node:zlib'
import { writeFileSync, mkdirSync } from 'node:fs'
import { dirname, join } from 'node:path'
import { fileURLToPath } from 'node:url'

const here = dirname(fileURLToPath(import.meta.url))
const publicDir = join(here, '..', 'public')

// Slate-950, violet-400, sky-400: the palette the app already uses.
const Background = [2, 6, 23]
const Chevron = [167, 139, 250]
const Underscore = [56, 189, 248]

/** Coverage is sampled on a grid this many times per axis, then averaged. */
const Supersample = 4

function clamp01(value) {
  return value < 0 ? 0 : value > 1 ? 1 : value
}

/** Distance from a point to a line segment, which is all a round-capped stroke is. */
function distanceToSegment(px, py, ax, ay, bx, by) {
  const dx = bx - ax
  const dy = by - ay
  const lengthSquared = dx * dx + dy * dy
  const t = lengthSquared === 0 ? 0 : clamp01(((px - ax) * dx + (py - ay) * dy) / lengthSquared)
  const cx = ax + t * dx
  const cy = ay + t * dy
  return Math.hypot(px - cx, py - cy)
}

/** Signed distance to a rounded rectangle; negative inside. */
function roundedRectDistance(px, py, cx, cy, half, radius) {
  const qx = Math.abs(px - cx) - (half - radius)
  const qy = Math.abs(py - cy) - (half - radius)
  const outside = Math.hypot(Math.max(qx, 0), Math.max(qy, 0))
  return outside + Math.min(Math.max(qx, qy), 0) - radius
}

function blend(target, offset, colour, coverage) {
  if (coverage <= 0) return
  const alpha = Math.min(coverage, 1)
  for (let channel = 0; channel < 3; channel += 1) {
    const existing = target[offset + channel]
    target[offset + channel] = Math.round(existing * (1 - alpha) + colour[channel] * alpha)
  }
  target[offset + 3] = Math.round(target[offset + 3] * (1 - alpha) + 255 * alpha)
}

/**
 * A terminal prompt: a chevron and a caret, which is what the product is. The
 * geometry is expressed in fractions of the icon so every size is the same
 * drawing rather than four hand-tuned ones.
 *
 * `inset` shrinks the artwork for the maskable variant, whose corners a launcher
 * is entitled to crop away.
 */
function renderIcon(size, { cornerRadius, inset }) {
  const pixels = new Uint8Array(size * size * 4)
  const centre = size / 2
  const scale = 1 - inset * 2

  const strokeWidth = 0.08 * size * scale
  const chevronBackX = centre - 0.2 * size * scale
  const chevronApexX = centre + 0.02 * size * scale
  const chevronTopY = centre - 0.2 * size * scale
  const chevronMidY = centre
  const chevronBottomY = centre + 0.2 * size * scale
  const underscoreY = centre + 0.2 * size * scale
  const underscoreLeft = centre + 0.08 * size * scale
  const underscoreRight = centre + 0.24 * size * scale

  const radius = cornerRadius * size
  const step = 1 / Supersample
  const start = step / 2
  const samples = Supersample * Supersample

  for (let y = 0; y < size; y += 1) {
    for (let x = 0; x < size; x += 1) {
      let backgroundCoverage = 0
      let chevronCoverage = 0
      let underscoreCoverage = 0

      for (let sy = 0; sy < Supersample; sy += 1) {
        for (let sx = 0; sx < Supersample; sx += 1) {
          const px = x + start + sx * step
          const py = y + start + sy * step

          if (roundedRectDistance(px, py, centre, centre, size / 2, radius) < 0) {
            backgroundCoverage += 1
          }

          const chevron = Math.min(
            distanceToSegment(px, py, chevronBackX, chevronTopY, chevronApexX, chevronMidY),
            distanceToSegment(px, py, chevronApexX, chevronMidY, chevronBackX, chevronBottomY),
          )
          if (chevron < strokeWidth / 2) chevronCoverage += 1

          if (
            distanceToSegment(px, py, underscoreLeft, underscoreY, underscoreRight, underscoreY) <
            strokeWidth / 2
          ) {
            underscoreCoverage += 1
          }
        }
      }

      const offset = (y * size + x) * 4
      blend(pixels, offset, Background, backgroundCoverage / samples)
      blend(pixels, offset, Chevron, chevronCoverage / samples)
      blend(pixels, offset, Underscore, underscoreCoverage / samples)
    }
  }

  return pixels
}

const crcTable = (() => {
  const table = new Uint32Array(256)
  for (let n = 0; n < 256; n += 1) {
    let c = n
    for (let k = 0; k < 8; k += 1) c = c & 1 ? 0xedb88320 ^ (c >>> 1) : c >>> 1
    table[n] = c >>> 0
  }
  return table
})()

function crc32(buffer) {
  let c = 0xffffffff
  for (const byte of buffer) c = crcTable[(c ^ byte) & 0xff] ^ (c >>> 8)
  return (c ^ 0xffffffff) >>> 0
}

function chunk(type, data) {
  const length = Buffer.alloc(4)
  length.writeUInt32BE(data.length)
  const body = Buffer.concat([Buffer.from(type, 'latin1'), data])
  const crc = Buffer.alloc(4)
  crc.writeUInt32BE(crc32(body))
  return Buffer.concat([length, body, crc])
}

function encodePng(pixels, size) {
  const header = Buffer.alloc(13)
  header.writeUInt32BE(size, 0)
  header.writeUInt32BE(size, 4)
  header[8] = 8 // bit depth
  header[9] = 6 // truecolour with alpha
  header[10] = 0 // deflate
  header[11] = 0 // adaptive filtering
  header[12] = 0 // no interlace

  // One filter byte per scanline. Filter 0 (none) keeps this readable; the
  // images are flat enough that a smarter filter would save very little.
  const raw = Buffer.alloc(size * (size * 4 + 1))
  const source = Buffer.from(pixels.buffer, pixels.byteOffset, pixels.byteLength)
  for (let y = 0; y < size; y += 1) {
    const rowStart = y * (size * 4 + 1)
    raw[rowStart] = 0
    source.copy(raw, rowStart + 1, y * size * 4, (y + 1) * size * 4)
  }

  return Buffer.concat([
    Buffer.from([0x89, 0x50, 0x4e, 0x47, 0x0d, 0x0a, 0x1a, 0x0a]),
    chunk('IHDR', header),
    chunk('IDAT', deflateSync(raw, { level: 9 })),
    chunk('IEND', Buffer.alloc(0)),
  ])
}

const icons = [
  // Rounded, because Android draws an unmasked icon exactly as given.
  { file: 'icon-192.png', size: 192, cornerRadius: 0.22, inset: 0 },
  { file: 'icon-512.png', size: 512, cornerRadius: 0.22, inset: 0 },
  // Full-bleed with the artwork pulled into the safe circle: a launcher may crop
  // a maskable icon to anything inscribed in the square.
  { file: 'icon-maskable-512.png', size: 512, cornerRadius: 0, inset: 0.14 },
  // iOS rounds the corners itself, and a transparent one comes out black.
  { file: 'apple-touch-icon.png', size: 180, cornerRadius: 0, inset: 0 },
]

mkdirSync(publicDir, { recursive: true })
for (const icon of icons) {
  const pixels = renderIcon(icon.size, icon)
  writeFileSync(join(publicDir, icon.file), encodePng(pixels, icon.size))
  process.stdout.write(`wrote ${icon.file} (${icon.size}x${icon.size})\n`)
}
