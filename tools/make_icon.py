"""Generate PkzExplorer.ico: a compact archive icon in the XT1 Studio visual language.

A rounded dark tile with the accent-blue archive/box glyph, drawn as vector shapes at 4x and
downsampled, so the small sizes stay crisp. Sizes match what Xt1Studio.ico ships so Explorer
picks a sensible image everywhere it is shown.
"""

from PIL import Image, ImageDraw
from pathlib import Path

# The palette's own values, so the icon sits beside the UI without clashing.
WINDOW = (27, 28, 31, 255)      # Brush.Window  #FF1B1C1F
CARD = (36, 38, 42, 255)        # Brush.Card    #FF24262A
ACCENT = (79, 141, 247, 255)    # Brush.Accent  #FF4F8DF7
ACCENT_SOFT = (79, 141, 247, 110)
TEXT = (230, 232, 236, 255)     # Brush.Text    #FFE6E8EC
MUTED = (154, 160, 170, 255)    # Brush.Muted   #FF9AA0AA

SIZES = [256, 128, 64, 48, 32, 16]
SS = 4  # supersample factor


def rounded(draw, box, radius, **kwargs):
    draw.rounded_rectangle(box, radius=radius, **kwargs)


def make(size):
    n = size * SS
    img = Image.new("RGBA", (n, n), (0, 0, 0, 0))
    d = ImageDraw.Draw(img)

    u = n / 32.0  # one design unit, so the drawing scales with the canvas

    # Tile with a border, matching the cards in the shell.
    rounded(d, (0.5 * u, 0.5 * u, n - 0.5 * u, n - 0.5 * u), radius=6 * u,
            fill=CARD, outline=(58, 61, 67, 255), width=max(1, round(0.6 * u)))

    # Archive body: a box with a lid, in the accent colour.
    body = (6 * u, 13 * u, 26 * u, 26 * u)
    rounded(d, body, radius=2.2 * u, fill=ACCENT)

    # The lid reads as the archive's separator band.
    lid = (4.6 * u, 9.5 * u, 27.4 * u, 14.6 * u)
    rounded(d, lid, radius=2.0 * u, fill=ACCENT, outline=(255, 255, 255, 60), width=max(1, round(0.35 * u)))

    # A lighter panel on the body makes the "container" read at small sizes.
    rounded(d, (8.2 * u, 16.6 * u, 23.8 * u, 23.4 * u), radius=1.3 * u, fill=(255, 255, 255, 38))

    # The latch: a short vertical accent on the lid's centre.
    rounded(d, (14.4 * u, 10.6 * u, 17.6 * u, 13.6 * u), radius=0.9 * u, fill=TEXT)

    # Two stacked "entries" inside the body, hinting at archived files.
    for i, y in enumerate((17.6, 20.6)):
        rounded(d, (10.4 * u, y * u, (10.4 + (9.0 if i == 0 else 6.0)) * u, (y + 1.5) * u),
                radius=0.7 * u, fill=(255, 255, 255, 175 if i == 0 else 120))

    # A small accent dot at the lower right: the "modified" marker the list also uses.
    d.ellipse((22.2 * u, 22.2 * u, 27.4 * u, 27.4 * u), fill=MUTED if size < 32 else TEXT)

    return img.resize((size, size), Image.LANCZOS)


images = [make(s) for s in SIZES]

# Resolved from this script's location, so the tool works wherever the repo lives.
target = Path(__file__).resolve().parent.parent / "src" / "PkzTool.App.Wpf" / "PkzExplorer.ico"
images[0].save(
    target,
    format="ICO",
    sizes=[(s, s) for s in SIZES],
    append_images=images[1:],
)
print("wrote", target, "with sizes:", SIZES)
