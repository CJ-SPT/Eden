"""Export the Eden SVG family. Requires pillow==13.0.0 and resvg-py==0.5.0."""

from io import BytesIO
from pathlib import Path

from PIL import Image, ImageDraw, ImageFont
import resvg_py


ROOT = Path(__file__).resolve().parents[1]
ICONS = ROOT / "assets" / "icons"
SIZES = (16, 20, 24, 32, 40, 48, 64, 128, 256)
FAMILY = (
    ("server", "SERVER", "Eden Server", "#67E8C5"),
    ("launcher", "LAUNCHER", "Eden Launcher", "#70C7FF"),
    ("sdk", "SDK", "Eden Modding SDK", "#B9A0FF"),
)


def font(size):
    try:
        return ImageFont.truetype("segoeui.ttf", size)
    except OSError:
        return ImageFont.load_default(size=size)


def main():
    preview = Image.new("RGB", (1200, 620), "#080E16")
    draw = ImageDraw.Draw(preview)
    draw.text((60, 35), "EDEN / APPLICATION ICONS", font=font(23), fill="#E4EDF7")
    draw.text((60, 73), "One family. Three distinct signals.", font=font(17), fill="#8EA1B8")
    for index, (name, label, title, accent) in enumerate(FAMILY):
        svg = ICONS / f"eden-{name}.svg"
        image = Image.open(BytesIO(resvg_py.svg_to_bytes(svg_path=str(svg), width=1024, height=1024)))
        image.save(ICONS / f"eden-{name}.png")
        image.save(ICONS / f"eden-{name}.ico", sizes=[(size, size) for size in SIZES])
        left = 60 + index * 380
        preview.paste(image.resize((224, 224), Image.Resampling.LANCZOS), (left + 48, 135), mask=image.resize((224, 224)))
        draw.text((left + 160, 382), label, font=font(20), fill=accent, anchor="mt")
        draw.text((left + 160, 417), title, font=font(17), fill="#8EA1B8", anchor="mt")
        for offset, size in enumerate((16, 24, 32, 48)):
            small = image.resize((size, size), Image.Resampling.LANCZOS)
            x = left + 39 + offset * 65
            preview.paste(small, (x + (48 - size) // 2, 481 + (48 - size) // 2), small)
            draw.text((x + 24, 546), str(size), font=font(13), fill="#8EA1B8", anchor="mt")
    preview.save(ICONS / "preview.png")


if __name__ == "__main__":
    main()
