/**
 * Client-side icon prep for project uploads.
 *
 * The hub only enforces content type and a byte cap (`ProjectStore.MaxIconBytes`),
 * not dimensions — someone could upload a 4000×3000 photo straight off their
 * phone. Downscaling here, before the upload, is what actually keeps every
 * project's icon a small square: cropped to a square from its center, then
 * scaled down to `size` if it is larger. A file already small enough is
 * returned unchanged rather than re-encoded, so a small deliberately-crafted
 * icon does not lose quality to a needless round trip through canvas.
 */
export async function downscaleToSquare(file: File, size = 256): Promise<File> {
  const bitmap = await createImageBitmap(file)

  try {
    const side = Math.min(bitmap.width, bitmap.height)
    if (side <= 0) return file

    // Cover-crop: take the largest centered square out of whatever shape was
    // uploaded, so a wide screenshot or a tall portrait both become a sane icon
    // instead of a squashed one.
    const sx = (bitmap.width - side) / 2
    const sy = (bitmap.height - side) / 2
    const target = Math.min(size, side)

    if (target === side && side === bitmap.width && side === bitmap.height) {
      // Already a square no bigger than the target — nothing to gain by redrawing.
      return file
    }

    const canvas = document.createElement('canvas')
    canvas.width = target
    canvas.height = target

    const ctx = canvas.getContext('2d')
    if (!ctx) return file

    ctx.drawImage(bitmap, sx, sy, side, side, 0, 0, target, target)

    const type = file.type === 'image/png' || file.type === 'image/webp' ? file.type : 'image/jpeg'
    const blob = await new Promise<Blob | null>((resolve) => canvas.toBlob(resolve, type, 0.9))
    if (!blob) return file

    return new File([blob], file.name, { type })
  } finally {
    bitmap.close()
  }
}
