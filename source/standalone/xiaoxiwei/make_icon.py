from pathlib import Path

from PIL import Image


PROJECT = Path(__file__).resolve().parent
WORKSPACE = PROJECT.parent.parent
ATLAS = WORKSPACE / "work" / "xiaoxiwei" / "realistic-run" / "final" / "spritesheet-extended.png"
OUTPUT = PROJECT / "xiaoxiwei.ico"


with Image.open(ATLAS) as source:
    atlas = source.convert("RGBA")

frame = atlas.crop((0, 0, 192, 208))
bounds = frame.getbbox()
if bounds is None:
    raise RuntimeError("idle frame is empty")

figure = frame.crop(bounds)
figure.thumbnail((224, 224), Image.Resampling.LANCZOS)
icon = Image.new("RGBA", (256, 256), (0, 0, 0, 0))
position = ((256 - figure.width) // 2, (256 - figure.height) // 2)
icon.alpha_composite(figure, position)
icon.save(
    OUTPUT,
    format="ICO",
    sizes=[(16, 16), (24, 24), (32, 32), (48, 48), (64, 64), (128, 128), (256, 256)],
)
print(OUTPUT)
