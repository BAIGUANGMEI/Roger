"""Rasterize Roger's R monogram into a crisp multi-size Windows icon.

Requires Pillow; the application and build do not require Python or Pillow.
The matching vector sources are Assets/Roger.svg and Assets/Brand.xaml.
All sources use the same 40x40 geometry and even-odd counter.
"""
from pathlib import Path
from PIL import Image, ImageDraw

ROOT = Path(__file__).resolve().parent.parent
OUTPUT = ROOT / "src" / "Roger" / "Assets" / "Roger.ico"
SIZES = (16, 20, 24, 32, 40, 48, 64, 128, 256)
MINT, INK = "#65DEB1", "#10352A"
RESOLUTION = 1024
SCALE = RESOLUTION / 40


def cubic(start, control1, control2, end, steps=64):
    """Sample the exact vector curve before supersampling and downscaling."""
    result = []
    for index in range(1, steps + 1):
        t = index / steps
        result.append(tuple((1 - t) ** 3 * start[axis]
                            + 3 * (1 - t) ** 2 * t * control1[axis]
                            + 3 * (1 - t) * t ** 2 * control2[axis]
                            + t ** 3 * end[axis] for axis in (0, 1)))
    return result


def scaled(points):
    return [(x * SCALE, y * SCALE) for x, y in points]


image = Image.new("RGBA", (RESOLUTION, RESOLUTION), (0, 0, 0, 0))
draw = ImageDraw.Draw(image)
draw.rounded_rectangle((0, 0, RESOLUTION - 1, RESOLUTION - 1), radius=10 * SCALE, fill=MINT)
# F0 M11,10 H22 C31,10 32,23 24,25 L31,32 H23 L17,25 V32 H11 Z
outer = [(11, 10), (22, 10)] + cubic((22, 10), (31, 10), (32, 23), (24, 25))
outer += [(31, 32), (23, 32), (17, 25), (17, 32), (11, 32)]
draw.polygon(scaled(outer), fill=INK)
# Even-odd counter: M17,15 V20 H22 C26,20 26,15 22,15 Z
counter = [(17, 15), (17, 20), (22, 20)] + cubic((22, 20), (26, 20), (26, 15), (22, 15))
draw.polygon(scaled(counter), fill=MINT)
image = image.resize((256, 256), Image.Resampling.LANCZOS)
OUTPUT.parent.mkdir(parents=True, exist_ok=True)
image.save(OUTPUT, format="ICO", sizes=[(size, size) for size in SIZES])

with Image.open(OUTPUT) as icon:
    assert icon.ico.sizes() == {(size, size) for size in SIZES}
    for size in SIZES:
        frame = icon.ico.getimage((size, size)).convert("RGBA")
        assert frame.getpixel((0, 0))[3] <= 3, "Rounded corner must stay transparent"
        assert frame.getpixel((size // 2, size // 2))[3] == 255, "Mark must remain opaque"
print(f"Generated and verified {len(SIZES)} Roger icon sizes: {OUTPUT}")
