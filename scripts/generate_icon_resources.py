"""Convert the pinned, unmodified Lucide SVG assets into WPF path geometries.

Usage: python scripts/generate_icon_resources.py
The source SVGs and Lucide/Feather license are committed alongside the result.
"""
from pathlib import Path
import xml.etree.ElementTree as ET
import re
from xml.sax.saxutils import escape

ROOT = Path(__file__).resolve().parents[1]
ASSETS = ROOT / "src" / "Roger" / "Assets" / "Icons"
ICONS = {
    "IconOverview": "layout-dashboard", "IconHardware": "cpu", "IconDisk": "hard-drive",
    "IconNetwork": "network", "IconSettings": "settings-2", "IconUpdates": "refresh-cw",
    "IconMinimize": "minus", "IconMaximize": "square", "IconRestore": "copy",
    "IconClose": "x", "IconSun": "sun", "IconMoon": "moon", "IconShield": "shield-check",
    "IconThermometer": "thermometer", "IconDownload": "arrow-down", "IconUpload": "arrow-up",
    "IconFolder": "folder-search", "IconScan": "scan-line",
    "IconExpand": "maximize-2", "IconCollapse": "minimize-2",
}


def number(value):
    return format(float(value), ".10g")


def geometry(element):
    kind = element.tag.rsplit("}", 1)[-1]
    attr = element.attrib
    if kind == "path":
        # Each SVG path starts at its own (0,0); concatenated WPF subpaths share a
        # current point, so the first relative moveto must become absolute.
        # Extra pairs after an initial 'm' are relative line segments, however.
        # Preserve those with an explicit 'l' instead of changing their meaning.
        data = attr["d"]
        numeric = r"[+-]?(?:\d*\.\d+|\d+\.?)(?:[eE][+-]?\d+)?"
        initial = re.match(rf"^\s*m\s*({numeric})\s*,?\s*({numeric})(.*)$", data)
        if initial:
            rest = initial.group(3).lstrip()
            command = " l" if rest and (rest[0].isdigit() or rest[0] in "+-.") else " "
            return f"M{initial.group(1)},{initial.group(2)}{command}{rest}"
        return data
    if kind == "line":
        return f'M{attr["x1"]},{attr["y1"]} L{attr["x2"]},{attr["y2"]}'
    if kind in ("polyline", "polygon"):
        return "M" + attr["points"] + (" Z" if kind == "polygon" else "")
    if kind in ("circle", "ellipse"):
        cx, cy = float(attr["cx"]), float(attr["cy"])
        rx = float(attr.get("r", attr.get("rx", "0")))
        ry = float(attr.get("r", attr.get("ry", "0")))
        return f'M{number(cx-rx)},{number(cy)} A{number(rx)},{number(ry)} 0 1 0 {number(cx+rx)},{number(cy)} A{number(rx)},{number(ry)} 0 1 0 {number(cx-rx)},{number(cy)} Z'
    if kind == "rect":
        x, y, w, h = (float(attr.get(key, "0")) for key in ("x", "y", "width", "height"))
        rx = min(float(attr.get("rx", attr.get("ry", "0"))), w / 2)
        ry = min(float(attr.get("ry", attr.get("rx", "0"))), h / 2)
        if rx == 0 or ry == 0:
            return f'M{number(x)},{number(y)} H{number(x+w)} V{number(y+h)} H{number(x)} Z'
        return (f'M{number(x+rx)},{number(y)} H{number(x+w-rx)} A{number(rx)},{number(ry)} 0 0 1 {number(x+w)},{number(y+ry)} '
                f'V{number(y+h-ry)} A{number(rx)},{number(ry)} 0 0 1 {number(x+w-rx)},{number(y+h)} '
                f'H{number(x+rx)} A{number(rx)},{number(ry)} 0 0 1 {number(x)},{number(y+h-ry)} '
                f'V{number(y+ry)} A{number(rx)},{number(ry)} 0 0 1 {number(x+rx)},{number(y)} Z')
    raise ValueError(f"Unsupported SVG shape: {kind}")


lines = ['<ResourceDictionary xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation" xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml">',
         '  <!-- Lucide SVG derivatives, 24x24 coordinates. Preserve viewport; use StrokeThickness=2 and round caps/joins. -->']
for key, filename in ICONS.items():
    svg = ET.parse(ASSETS / (filename + ".svg")).getroot()
    data = " ".join(geometry(shape) for shape in svg)
    lines.append(f'  <Geometry x:Key="{key}">{escape(data)}</Geometry>')
lines.append("</ResourceDictionary>")
(ASSETS / "Icons.xaml").write_text("\n".join(lines) + "\n", encoding="utf-8")
print(f"Generated {len(ICONS)} WPF geometries from pinned Lucide SVGs.")
