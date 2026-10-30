"""Rebuild the original vector-style app icon (authoring only; requires Pillow)."""
from pathlib import Path
from PIL import Image, ImageDraw
assets = Path(__file__).resolve().parents[1] / "src" / "LocalAccountManager" / "Assets"
assets.mkdir(parents=True, exist_ok=True)
scale = 4
image = Image.new("RGBA", (256 * scale, 256 * scale))
draw = ImageDraw.Draw(image)
def rect(box, radius, fill):
    draw.rounded_rectangle(tuple(int(v * scale) for v in box), int(radius * scale), fill=fill)
rect((4, 4, 252, 252), 52, "#101319")
rect((68, 35, 216, 189), 25, "#8798C2")
rect((40, 65, 198, 220), 25, "#C3F27D")
draw.ellipse(tuple(int(v * scale) for v in (97, 95, 141, 139)), fill="#17210E")
rect((74, 151, 164, 196), 22, "#17210E")
image = image.resize((256, 256), Image.Resampling.LANCZOS)
image.save(assets / "AppIcon.png")
image.save(assets / "AppIcon.ico", format="ICO", sizes=[(n, n) for n in (16, 24, 32, 48, 64, 128, 256)])
print("Generated original app icon: ICO + PNG")
