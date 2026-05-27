"""Generate beta-badged variants of each Drover state icon.

Reads existing png/drover-{state}-{size}.png files, overlays a small
"BETA" badge on the bottom-right corner, writes png/drover-{state}-beta-{size}.png,
then bundles each state's sizes into a multi-res ICO at
../src/Drover.App/Resources/Icons/drover-{state}-beta.ico.
"""
from pathlib import Path
from PIL import Image, ImageDraw, ImageFont

ROOT = Path(__file__).resolve().parent
PNG_DIR = ROOT / "png"
ICO_OUT = ROOT.parent / "src" / "Drover.App" / "Resources" / "Icons"

STATES = ["idle", "working", "attention", "inactive"]
SIZES = [16, 20, 24, 32, 48, 64, 128, 256]
ICO_SIZES = [16, 24, 32, 48, 64, 128, 256]

# Badge colour: vivid red so it screams "not the release build"
BADGE_FILL = (224, 36, 36, 255)
BADGE_TEXT = (255, 255, 255, 255)


def load_font(px: int) -> ImageFont.FreeTypeFont | ImageFont.ImageFont:
    # Try a few common Windows fonts in order; fall back to default if none found.
    for name in ("arialbd.ttf", "seguisb.ttf", "segoeuib.ttf", "arial.ttf"):
        try:
            return ImageFont.truetype(name, px)
        except OSError:
            continue
    return ImageFont.load_default()


def make_badge(size: int) -> Image.Image:
    """Bottom-right badge sized proportionally to the icon."""
    img = Image.new("RGBA", (size, size), (0, 0, 0, 0))
    draw = ImageDraw.Draw(img)

    if size <= 20:
        # Too small for legible text — just a solid corner dot.
        r = max(3, size // 3)
        x0 = size - r - 1
        y0 = size - r - 1
        draw.ellipse([x0, y0, size - 1, size - 1], fill=BADGE_FILL)
        return img

    if size <= 32:
        text = "β"
    else:
        text = "BETA"

    # Pick a font size relative to icon size.
    if size <= 32:
        font_px = max(10, int(size * 0.55))
    elif size <= 64:
        font_px = max(12, int(size * 0.32))
    else:
        font_px = max(14, int(size * 0.22))
    font = load_font(font_px)

    # Measure text.
    bbox = draw.textbbox((0, 0), text, font=font)
    tw = bbox[2] - bbox[0]
    th = bbox[3] - bbox[1]

    pad_x = max(2, size // 24)
    pad_y = max(1, size // 32)
    badge_w = tw + pad_x * 2
    badge_h = th + pad_y * 2

    margin = max(1, size // 32)
    x1 = size - margin
    y1 = size - margin
    x0 = x1 - badge_w
    y0 = y1 - badge_h

    radius = max(2, badge_h // 3)
    draw.rounded_rectangle([x0, y0, x1, y1], radius=radius, fill=BADGE_FILL)

    tx = x0 + pad_x - bbox[0]
    ty = y0 + pad_y - bbox[1]
    draw.text((tx, ty), text, font=font, fill=BADGE_TEXT)

    return img


def overlay(base: Image.Image, badge: Image.Image) -> Image.Image:
    out = base.convert("RGBA").copy()
    out.alpha_composite(badge)
    return out


def main() -> None:
    ICO_OUT.mkdir(parents=True, exist_ok=True)

    for state in STATES:
        for s in SIZES:
            src = PNG_DIR / f"drover-{state}-{s}.png"
            if not src.exists():
                raise FileNotFoundError(src)
            base = Image.open(src)
            badge = make_badge(s)
            out = overlay(base, badge)
            out.save(PNG_DIR / f"drover-{state}-beta-{s}.png")

        # Bundle multi-res ICO.
        frames = [
            Image.open(PNG_DIR / f"drover-{state}-beta-{s}.png").convert("RGBA")
            for s in ICO_SIZES
        ]
        ico_path = ICO_OUT / f"drover-{state}-beta.ico"
        frames[0].save(
            ico_path,
            format="ICO",
            sizes=[(s, s) for s in ICO_SIZES],
            append_images=frames[1:],
        )
        print(f"wrote {ico_path}")


if __name__ == "__main__":
    main()
