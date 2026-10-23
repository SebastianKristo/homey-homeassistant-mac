#!/usr/bin/env python3
"""Generates all raster icons/images for MacBridge (Mac app, DMG, Homey, Home Assistant).

Run from the repo root: python3 tools/generate_icons.py   (needs Pillow)
"""

from pathlib import Path

from PIL import Image, ImageDraw, ImageFilter, ImageFont

ROOT = Path(__file__).resolve().parent.parent
SS = 4  # supersampling factor

TOP = (43, 108, 255)
BOTTOM = (122, 60, 255)
WHITE = (255, 255, 255, 255)


def gradient(w, h, top, bottom):
    img = Image.new("RGBA", (w, h))
    d = ImageDraw.Draw(img)
    for y in range(h):
        t = y / max(1, h - 1)
        d.line([(0, y), (w, y)], fill=tuple(int(top[i] + (bottom[i] - top[i]) * t) for i in range(3)) + (255,))
    return img


def draw_glyph(d, cx, cy, s, color=WHITE):
    """Monitor with a house inside – the MacBridge mark. s = overall width."""
    lw = s * 0.07
    mw, mh = s, s * 0.66
    left, top = cx - mw / 2, cy - mh / 2 - s * 0.08
    d.rounded_rectangle([left, top, left + mw, top + mh], radius=s * 0.07, outline=color, width=int(lw))
    # stand
    d.rectangle([cx - s * 0.05, top + mh, cx + s * 0.05, top + mh + s * 0.13], fill=color)
    d.rounded_rectangle([cx - s * 0.22, top + mh + s * 0.12, cx + s * 0.22, top + mh + s * 0.18],
                        radius=s * 0.03, fill=color)
    # house
    hcx, hcy, hs = cx, top + mh / 2 + s * 0.02, s * 0.36
    d.polygon([(hcx - hs * 0.62, hcy - hs * 0.02), (hcx, hcy - hs * 0.58), (hcx + hs * 0.62, hcy - hs * 0.02)],
              fill=color)
    d.rectangle([hcx - hs * 0.42, hcy - hs * 0.06, hcx + hs * 0.42, hcy + hs * 0.42], fill=color)
    return hcx, hcy, hs


def app_icon(size):
    big = size * SS
    img = Image.new("RGBA", (big, big), (0, 0, 0, 0))
    # macOS squircle-ish plate with margin (Big Sur grid: 824/1024 plate)
    plate = int(big * 824 / 1024)
    off = (big - plate) // 2
    grad = gradient(plate, plate, TOP, BOTTOM)
    mask = Image.new("L", (plate, plate), 0)
    ImageDraw.Draw(mask).rounded_rectangle([0, 0, plate - 1, plate - 1], radius=int(plate * 0.225), fill=255)
    # soft shadow
    shadow = Image.new("RGBA", (big, big), (0, 0, 0, 0))
    ImageDraw.Draw(shadow).rounded_rectangle([off, off + big * 0.012, off + plate, off + plate + big * 0.012],
                                             radius=int(plate * 0.225), fill=(0, 0, 0, 90))
    img = Image.alpha_composite(img, shadow.filter(ImageFilter.GaussianBlur(big * 0.012)))
    img.paste(grad, (off, off), mask)
    d = ImageDraw.Draw(img)
    hcx, hcy, hs = draw_glyph(d, big / 2, big / 2, plate * 0.58)
    # door in plate colour
    door_c = tuple(int(TOP[i] + (BOTTOM[i] - TOP[i]) * 0.55) for i in range(3)) + (255,)
    d.rounded_rectangle([hcx - hs * 0.11, hcy + hs * 0.12, hcx + hs * 0.11, hcy + hs * 0.42],
                        radius=hs * 0.04, fill=door_c)
    return img.resize((size, size), Image.LANCZOS)


def mac_app_icons():
    iconset = ROOT / "mac-app/Resources/AppIcon.iconset"
    iconset.mkdir(parents=True, exist_ok=True)
    master = app_icon(1024)
    for base in (16, 32, 128, 256, 512):
        master.resize((base, base), Image.LANCZOS).save(iconset / f"icon_{base}x{base}.png")
        master.resize((base * 2, base * 2), Image.LANCZOS).save(iconset / f"icon_{base}x{base}@2x.png")
    return master


def font(size, bold=False):
    for name in ("DejaVuSans-Bold.ttf" if bold else "DejaVuSans.ttf",):
        for p in Path("/usr/share/fonts").rglob(name):
            return ImageFont.truetype(str(p), size)
    return ImageFont.load_default()


def dmg_background(scale):
    w, h = 660 * scale, 400 * scale
    img = gradient(w, h, (248, 249, 252), (232, 236, 246)).convert("RGBA")
    d = ImageDraw.Draw(img)
    d.text((w / 2, 52 * scale), "MacBridge", font=font(30 * scale, True), fill=(30, 30, 40), anchor="mm")
    d.text((w / 2, 84 * scale), "Dra MacBridge til Programmer-mappen for å installere",
           font=font(14 * scale), fill=(90, 95, 110), anchor="mm")
    # arrow between icons at x=170 and x=490, y=205
    y = 205 * scale
    x0, x1 = 250 * scale, 410 * scale
    col = (122, 60, 255)
    d.line([x0, y, x1 - 18 * scale, y], fill=col, width=6 * scale)
    d.polygon([(x1, y), (x1 - 26 * scale, y - 16 * scale), (x1 - 26 * scale, y + 16 * scale)], fill=col)
    d.text((w / 2, 360 * scale), "Åpne deretter MacBridge fra menylinjen og velg «Copy token»",
           font=font(12 * scale), fill=(120, 125, 140), anchor="mm")
    out = ROOT / "mac-app/Resources"
    img.convert("RGB").save(out / ("dmg-background.png" if scale == 1 else "dmg-background@2x.png"))


def device_image(w, h, kind):
    """White-on-gradient product image for Homey drivers."""
    big_w, big_h = w * SS, h * SS
    img = gradient(big_w, big_h, TOP, BOTTOM)
    d = ImageDraw.Draw(img)
    s = min(big_w, big_h) * 0.62
    cx, cy = big_w / 2, big_h / 2
    if kind == "app":
        hcx, hcy, hs = draw_glyph(d, cx, cy + s * 0.05, s)
        d.rounded_rectangle([hcx - hs * 0.11, hcy + hs * 0.12, hcx + hs * 0.11, hcy + hs * 0.42],
                            radius=hs * 0.04, fill=TOP)
    elif kind == "mac_mini":
        bw, bh = s, s * 0.36
        top = cy - bh / 2
        d.rounded_rectangle([cx - bw / 2, top, cx + bw / 2, top + bh], radius=s * 0.08, fill=WHITE)
        # front: two USB-C ports, headphone jack and power LED
        port_y = top + bh * 0.55
        for i in range(2):
            px = cx - bw * 0.38 + i * bw * 0.08
            d.rounded_rectangle([px, port_y - bh * 0.07, px + bw * 0.045, port_y + bh * 0.07],
                                radius=bh * 0.05, fill=BOTTOM)
        d.ellipse([cx - bw * 0.15, port_y - bh * 0.06, cx - bw * 0.11, port_y + bh * 0.06], fill=BOTTOM)
        d.ellipse([cx + bw * 0.38, port_y - bh * 0.035, cx + bw * 0.41, port_y + bh * 0.035], fill=(120, 255, 170))
    elif kind == "macbook":
        sw, sh = s * 0.82, s * 0.52
        top = cy - sh / 2 - s * 0.06
        lw = s * 0.045
        d.rounded_rectangle([cx - sw / 2, top, cx + sw / 2, top + sh], radius=s * 0.04, outline=WHITE, width=int(lw))
        # notch
        d.rounded_rectangle([cx - s * 0.06, top, cx + s * 0.06, top + s * 0.045], radius=s * 0.015, fill=WHITE)
        # base
        base_y = top + sh + s * 0.01
        d.polygon([(cx - s / 2, base_y), (cx + s / 2, base_y), (cx + s * 0.47, base_y + s * 0.06),
                   (cx - s * 0.47, base_y + s * 0.06)], fill=WHITE)
        d.rounded_rectangle([cx - s * 0.08, base_y, cx + s * 0.08, base_y + s * 0.02], radius=s * 0.01, fill=TOP)
    return img.resize((w, h), Image.LANCZOS).convert("RGB")


def homey_images():
    for path, kind in (("homey/assets/images", "app"),):
        out = ROOT / path
        out.mkdir(parents=True, exist_ok=True)
        device_image(250, 175, kind).save(out / "small.png")
        device_image(500, 350, kind).save(out / "large.png")
        device_image(1000, 700, kind).save(out / "xlarge.png")
    for driver, kind in (("mac_mini", "mac_mini"), ("macbook_pro", "macbook")):
        out = ROOT / f"homey/drivers/{driver}/assets/images"
        out.mkdir(parents=True, exist_ok=True)
        device_image(75, 75, kind).save(out / "small.png")
        device_image(500, 500, kind).save(out / "large.png")
        device_image(1000, 1000, kind).save(out / "xlarge.png")


def ha_brand(master):
    out = ROOT / "custom_components/mac_bridge/brand"
    out.mkdir(parents=True, exist_ok=True)
    master.resize((256, 256), Image.LANCZOS).save(out / "icon.png")
    master.resize((512, 512), Image.LANCZOS).save(out / "icon@2x.png")
    master.resize((256, 256), Image.LANCZOS).save(out / "logo.png")
    master.resize((512, 512), Image.LANCZOS).save(out / "logo@2x.png")


if __name__ == "__main__":
    master = mac_app_icons()
    dmg_background(1)
    dmg_background(2)
    homey_images()
    ha_brand(master)
    master.resize((256, 256), Image.LANCZOS).save(ROOT / "icon.png")
    print("Icons generated")
