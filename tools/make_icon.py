"""Renders src/Onus/Assets/Onus.ico: accent-gradient squircle (#A855F7 -> #EC4899) with white waveform bars.
Run: python tools/make_icon.py   (needs Pillow)"""
from pathlib import Path
from PIL import Image, ImageDraw

A, B = (0xA8, 0x55, 0xF7), (0xEC, 0x48, 0x99)  # AccentColor, Accent2Color (Themes/Styles.xaml)
SS = 4  # supersampling


def render(size: int) -> Image.Image:
    s = size * SS
    # diagonal gradient, top-left -> bottom-right, like AccentGradient
    grad = Image.new("RGB", (s, s))
    px = grad.load()
    for y in range(s):
        for x in range(s):
            t = (x + y) / (2 * (s - 1))
            px[x, y] = tuple(round(a + (b - a) * t) for a, b in zip(A, B))
    pad = round(s * (0.0 if size <= 24 else 0.04))
    mask = Image.new("L", (s, s), 0)
    ImageDraw.Draw(mask).rounded_rectangle((pad, pad, s - 1 - pad, s - 1 - pad), radius=round(s * 0.24), fill=255)
    img = Image.new("RGBA", (s, s), (0, 0, 0, 0))
    img.paste(grad, (0, 0), mask)

    # bars: fewer and fatter at small sizes so they stay readable
    heights = [0.34, 0.62, 1.0, 0.62, 0.34] if size >= 48 else [0.5, 1.0, 0.5]
    n = len(heights)
    inner = s - 2 * pad
    span = inner * (0.58 if size >= 48 else 0.62)
    bar = span / (n * 1.6 - 0.6)  # bar width; gap = 0.6 bar
    max_h = inner * 0.56
    x0 = (s - span) / 2
    d = ImageDraw.Draw(img)
    for i, h in enumerate(heights):
        x = x0 + i * bar * 1.6
        hh = max(bar, max_h * h)
        y = (s - hh) / 2
        d.rounded_rectangle((x, y, x + bar, y + hh), radius=bar / 2, fill=(255, 255, 255, 255))
    return img.resize((size, size), Image.LANCZOS)


sizes = [16, 20, 24, 32, 40, 48, 64, 128, 256]
frames = [render(n) for n in sizes]
out = Path(__file__).resolve().parent.parent / "src" / "Onus" / "Assets" / "Onus.ico"
frames[-1].save(out, format="ICO", sizes=[(n, n) for n in sizes], append_images=frames[:-1])
print(out)
