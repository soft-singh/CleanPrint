"""
Builds the application icon and the in-app logo images from CleanPrint.App/Assets/CleanPrint-logo.svg.

    pip install cairosvg pillow
    python Scripts/make_icon.py

Writes, next to the SVG:
  CleanPrint.ico   16, 20, 24, 32, 40, 48, 64, 128 and 256 px. Every size is rendered from the SVG at EXACTLY that size (not one big
                   picture scaled down), so the small sizes stay crisp. Sizes up to 64 are stored as classic 32-bit BMP entries, which
                   every version of Windows reads; 128 and 256 are PNG entries, which is what Windows itself uses for large icons.
  logo-64.png      the title bar (shown at 18 units; 64 px stays sharp on high-DPI screens)
  logo-256.png     the About dialog (shown at 96 units)
"""
import io, os, struct, sys
import cairosvg
from PIL import Image

HERE = os.path.join(os.path.dirname(os.path.abspath(__file__)), '..', 'CleanPrint.App', 'Assets')
SVG = os.path.join(HERE, 'CleanPrint-logo.svg')
SIZES = [16, 20, 24, 32, 40, 48, 64, 128, 256]

def render(size: int) -> Image.Image:
    png = cairosvg.svg2png(url=SVG, output_width=size, output_height=size)
    return Image.open(io.BytesIO(png)).convert('RGBA')

def bmp_entry(img: Image.Image) -> bytes:
    """An ICO BMP entry: a BITMAPINFOHEADER (height doubled: it covers the colour image AND the 1-bit mask), the 32-bit BGRA pixels
    bottom-up, then an all-zero AND mask (the real transparency is the alpha channel)."""
    w, h = img.size
    px = img.load()
    rows = []
    for y in range(h - 1, -1, -1):
        row = bytearray()
        for x in range(w):
            r, g, b, a = px[x, y]
            row += bytes((b, g, r, a))
        rows.append(bytes(row))
    mask_row = ((w + 31) // 32) * 4
    header = struct.pack('<IiiHHIIiiII', 40, w, h * 2, 1, 32, 0, w * h * 4, 0, 0, 0, 0)
    return header + b''.join(rows) + bytes(mask_row * h)

def png_entry(img: Image.Image) -> bytes:
    buf = io.BytesIO(); img.save(buf, 'PNG', optimize=True); return buf.getvalue()

entries = []
for s in SIZES:
    img = render(s)
    entries.append((s, png_entry(img) if s >= 128 else bmp_entry(img)))

out = bytearray(struct.pack('<HHH', 0, 1, len(entries)))
offset = 6 + 16 * len(entries)
for s, data in entries:
    out += struct.pack('<BBBBHHII', 0 if s >= 256 else s, 0 if s >= 256 else s, 0, 0, 1, 32, len(data), offset)
    offset += len(data)
for _, data in entries:
    out += data
open(os.path.join(HERE, 'CleanPrint.ico'), 'wb').write(out)
render(64).save(os.path.join(HERE, 'logo-64.png'))
render(256).save(os.path.join(HERE, 'logo-256.png'))
print(f"CleanPrint.ico: {len(out):,} bytes, sizes {SIZES}")
