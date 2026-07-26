"""Generates the Hearth application icon.

MARK: the mouth of a hearth, drawn as a thick arch STROKE, with a single ember
banked on the floor inside it.

Two rejected attempts, both instructive:

  * A flame. It is what every "fast / hot / energy" product already uses, and its
    silhouette is all thin tapering points, so it turns to mush below ~24px.

  * A SOLID arch. Read as a tombstone -- and adding a bright bar at its base for
    the hearth floor made it a headstone on a plinth. A hearth is an OPENING;
    drawing it as a filled shape says the opposite of the intended thing.

An arch stroke is a closed geometric form that survives 16 pixels, is not a
shape other browsers use, and says hearth rather than fire -- which is the
actual idea, since the product is about banking embers and returning to them.

Drawn at 16x and downsampled with LANCZOS: Windows' own icon scaler is poor, and
a 256px master resized to 16px turns to mud.
"""
import os
from PIL import Image, ImageDraw, ImageFilter

OUT_DIR = "src/Hearth/Assets"
SIZES = [16, 24, 32, 48, 64, 128, 256]
SS = 16  # supersample factor

# Warm near-black, so the tile reads as a dark object on both light and dark
# taskbars rather than dissolving into either.
TILE_TOP = (34, 27, 23)
TILE_BOTTOM = (19, 15, 13)

EMBER_TOP = (250, 196, 120)
EMBER_BOTTOM = (231, 126, 52)


def vertical_gradient(size, top, bottom):
    grad = Image.new("RGB", (1, size), top)
    for y in range(size):
        t = y / max(size - 1, 1)
        grad.putpixel((0, y), tuple(
            round(top[i] + (bottom[i] - top[i]) * t) for i in range(3)))
    return grad.resize((size, size), Image.NEAREST)


def build(px):
    n = px * SS
    tile_radius = int(n * 0.235)          # Windows 11 app-tile roundness

    tile_mask = Image.new("L", (n, n), 0)
    ImageDraw.Draw(tile_mask).rounded_rectangle(
        [0, 0, n - 1, n - 1], tile_radius, fill=255)

    icon = Image.new("RGBA", (n, n), (0, 0, 0, 0))
    icon.paste(vertical_gradient(n, TILE_TOP, TILE_BOTTOM), (0, 0), tile_mask)

    # --- the arch, as a stroke -------------------------------------------
    # Outer arch minus inner arch. The legs run to the floor line so the shape
    # is an opening rather than a closed capsule.
    aw = n * 0.52
    stroke = n * 0.105
    left = (n - aw) / 2
    right = left + aw
    top = n * 0.215
    floor = n * 0.775
    r_out = aw / 2

    outer = Image.new("L", (n, n), 0)
    od = ImageDraw.Draw(outer)
    od.ellipse([left, top, right, top + 2 * r_out], fill=255)
    od.rectangle([left, top + r_out, right, floor], fill=255)

    inner = Image.new("L", (n, n), 0)
    idr = ImageDraw.Draw(inner)
    il, ir = left + stroke, right - stroke
    it = top + stroke
    r_in = (ir - il) / 2
    idr.ellipse([il, it, ir, it + 2 * r_in], fill=255)
    idr.rectangle([il, it + r_in, ir, floor + 1], fill=255)

    arch = Image.composite(Image.new("L", (n, n), 0), outer, inner)

    ember = vertical_gradient(n, EMBER_TOP, EMBER_BOTTOM)
    icon.paste(ember, (0, 0),
               Image.composite(arch, Image.new("L", (n, n), 0), tile_mask))

    # --- the banked ember on the floor -----------------------------------
    # One dot, low and centred inside the opening. Small enough to vanish
    # gracefully at 16px rather than smearing into the arch.
    dot = Image.new("L", (n, n), 0)
    dd = ImageDraw.Draw(dot)
    cx = n / 2
    cy = floor - stroke * 0.95
    rad = aw * 0.145
    dd.ellipse([cx - rad, cy - rad, cx + rad, cy + rad], fill=255)

    halo = dot.filter(ImageFilter.GaussianBlur(n * 0.02))
    icon.paste(Image.new("RGB", (n, n), (255, 176, 96)), (0, 0),
               Image.composite(halo.point(lambda v: int(v * 0.55)),
                               Image.new("L", (n, n), 0), tile_mask))
    icon.paste(Image.new("RGB", (n, n), (255, 226, 178)), (0, 0),
               Image.composite(dot, Image.new("L", (n, n), 0), tile_mask))

    return icon.resize((px, px), Image.LANCZOS)


os.makedirs(OUT_DIR, exist_ok=True)
frames = [build(s) for s in SIZES]

ico_path = os.path.join(OUT_DIR, "hearth.ico")
frames[-1].save(ico_path, format="ICO",
                sizes=[(s, s) for s in SIZES],
                append_images=frames[:-1])
frames[-1].save(os.path.join(OUT_DIR, "hearth-256.png"))

# Contact sheet, on both a light and a dark strip, so the small sizes can be
# judged the way they will actually be seen.
sheet_sizes = [16, 20, 24, 32, 48, 64, 128]
pad = 18
width = sum(s + pad for s in sheet_sizes) + pad
row = 128 + pad * 2
sheet = Image.new("RGBA", (width, row * 2), (0, 0, 0, 255))
for band, bg in enumerate([(32, 32, 36, 255), (238, 238, 240, 255)]):
    ImageDraw.Draw(sheet).rectangle([0, band * row, width, (band + 1) * row], fill=bg)
    x = pad
    for s in sheet_sizes:
        img = build(s)
        sheet.paste(img, (x, band * row + pad + (128 - s) // 2), img)
        x += s + pad
sheet.resize((width * 2, row * 4), Image.NEAREST).save("docs/images/icon.png")

print("wrote", ico_path, os.path.getsize(ico_path), "bytes; sizes:", SIZES)
