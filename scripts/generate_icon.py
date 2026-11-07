"""Rasterize Roger's code-defined heartbeat mark into a multi-size Windows icon.

Requires Pillow; the application and build do not require Python or Pillow.
The matching editable vector source is src/Roger/Assets/Roger.svg.
"""
from pathlib import Path

from PIL import Image, ImageDraw


root = Path(__file__).resolve().parent.parent
output = root / "src" / "Roger" / "Assets" / "Roger.ico"
scale = 4
image = Image.new("RGBA", (256 * scale, 256 * scale), (0, 0, 0, 0))
draw = ImageDraw.Draw(image)
box = tuple(value * scale for value in (5, 5, 251, 251))
draw.rounded_rectangle(box, radius=60 * scale, fill="#121c27", outline="#304239", width=5 * scale)
points = [(38, 132), (75, 132), (96, 78), (124, 185), (154, 99), (173, 132), (218, 132)]
scaled_points = [(x * scale, y * scale) for x, y in points]
stroke_width = 15 * scale
draw.line(scaled_points, fill="#82e5b8", width=stroke_width, joint="curve")
radius = stroke_width / 2
for x, y in scaled_points:
    draw.ellipse((x - radius, y - radius, x + radius, y + radius), fill="#82e5b8")
image = image.resize((256, 256), Image.Resampling.LANCZOS)
output.parent.mkdir(parents=True, exist_ok=True)
image.save(output, format="ICO", sizes=[(size, size) for size in (16, 20, 24, 32, 40, 48, 64, 128, 256)])
print(output)
