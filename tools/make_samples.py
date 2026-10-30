"""Generates the fictitious sample documents in input-docs/ (no real person, no real document).

Usage: python3 tools/make_samples.py [input-docs]
Needs Pillow, qrcode and reportlab (pip install pillow qrcode reportlab).

Writes:
  td3/specimen-passport-scan.png       TD3 passport data page, flat scan
  td3/specimen-passport-photo.jpg      the same page photographed on a desk, turned a quarter turn
  td3/specimen-passport-2-pages.pdf    a page without MRZ followed by the data page
  aadhaar/specimen-qr-id-card.png      ID card front with a QR code, in the Aadhaar card layout

Passport data uses the ICAO 9303 specimen state "Utopia" (code UTO) and its fictitious holder.
The ID card number is not a valid number, and every document is marked SPECIMEN.
Fonts: Andale Mono (MRZ) and Arial from macOS (OCR-B is not shipped with macOS);
set SPECIMEN_FONT_DIR to a folder with the same .ttf files on other systems.
"""
import os
import random
import sys
from pathlib import Path

import qrcode
from PIL import Image, ImageDraw, ImageFilter, ImageFont
from reportlab.lib.utils import ImageReader
from reportlab.pdfgen import canvas as pdf_canvas

FONTS = Path(os.environ.get("SPECIMEN_FONT_DIR", "/System/Library/Fonts/Supplemental"))
MONO = str(FONTS / "Andale Mono.ttf")
SANS = str(FONTS / "Arial.ttf")
SANS_B = str(FONTS / "Arial Bold.ttf")

PX_PER_MM = 12  # about 300 dpi
MRZ = [
    "P<UTOERIKSSON<<ANNA<MARIA<<<<<<<<<<<<<<<<<<<",
    "L898902C36UTO7408122F1204159ZE184226B<<<<<10",
]
INK = (20, 20, 30)
LABEL = (85, 85, 95)
BLUE = (30, 50, 110)


def mm(v):
    return int(round(v * PX_PER_MM))


def font(path, size_mm):
    return ImageFont.truetype(path, mm(size_mm))


def guilloche(img, seed, color):
    rnd = random.Random(seed)
    d = ImageDraw.Draw(img)
    w, h = img.size
    for _ in range(120):
        x, y, r = rnd.randint(0, w), rnd.randint(0, h), rnd.randint(40, 260)
        d.arc((x - r, y - r, x + r, y + r), rnd.randint(0, 360), rnd.randint(0, 360), fill=color, width=2)


def portrait(d, box):
    """Grey silhouette placeholder: no face, no real person."""
    x0, y0, x1, y1 = box
    d.rounded_rectangle(box, radius=mm(1.5), fill=(190, 196, 205))
    cx, w = (x0 + x1) / 2, x1 - x0
    d.ellipse((cx - w * 0.25, y0 + (y1 - y0) * 0.14, cx + w * 0.25, y0 + (y1 - y0) * 0.52), fill=(150, 156, 168))
    d.rounded_rectangle((x0 + w * 0.12, y0 + (y1 - y0) * 0.56, x1 - w * 0.12, y1), radius=mm(5), fill=(150, 156, 168))


def passport_page():
    """TD3 data page, 125 x 88 mm, labels above values, MRZ at the ICAO position."""
    img = Image.new("RGB", (mm(125), mm(88)), (238, 241, 236))
    guilloche(img, 3, (218, 224, 214))
    d = ImageDraw.Draw(img)
    d.text((mm(6), mm(4)), "UTOPIA", font=font(SANS_B, 4.0), fill=BLUE)
    d.text((mm(44), mm(4)), "PASSPORT", font=font(SANS_B, 4.0), fill=BLUE)
    d.text((mm(92), mm(5)), "SPECIMEN", font=font(SANS_B, 3.0), fill=(170, 40, 40))
    portrait(d, (mm(6), mm(14), mm(37), mm(50)))
    d.text((mm(6), mm(53)), "Holder's signature", font=font(SANS, 2.0), fill=LABEL)
    d.line([(mm(8), mm(60)), (mm(14), mm(57)), (mm(19), mm(61)), (mm(25), mm(57)), (mm(32), mm(60))], fill=INK, width=3)
    label, value = font(SANS, 2.0), font(SANS_B, 3.0)
    fields = [
        ("Type", "P", 44, 13), ("Country Code", "UTO", 60, 13), ("Passport No.", "L898902C3", 90, 13),
        ("Surname", "ERIKSSON", 44, 21), ("Given Names", "ANNA MARIA", 44, 29),
        ("Nationality", "UTOPIAN", 44, 37), ("Sex", "F", 76, 37), ("Date of Birth", "12/08/1974", 90, 37),
        ("Place of Birth", "ZENITH", 44, 45), ("Place of Issue", "ZENITH", 44, 53),
        ("Date of Issue", "16/04/2007", 44, 61), ("Date of Expiry", "15/04/2012", 90, 61),
    ]
    for lab, val, x, y in fields:
        d.text((mm(x), mm(y)), lab, font=label, fill=LABEL)
        d.text((mm(x), mm(y + 2.6)), val, font=value, fill=INK)
    pitch = 2.54 * PX_PER_MM  # OCR-B character pitch
    mono = ImageFont.truetype(MONO, int(pitch / 0.6))
    for i, line in enumerate(MRZ):
        for j, c in enumerate(line):
            d.text((mm(6) + j * pitch, mm(72) + i * mm(4.8)), c, font=mono, fill=INK)
    return img


def phone_photo(page):
    """The page on a desk, turned a quarter turn and tilted, slightly blurred."""
    turned = page.rotate(86, expand=True, fillcolor=(96, 86, 74), resample=Image.BICUBIC)
    desk = Image.new("RGB", (turned.width + 240, turned.height + 240), (96, 86, 74))
    desk.paste(turned, (120, 120))
    return desk.filter(ImageFilter.GaussianBlur(0.8))


def other_page():
    """A passport page without MRZ (visa and stamps page)."""
    img = Image.new("RGB", (mm(125), mm(88)), (241, 238, 230))
    guilloche(img, 9, (224, 218, 206))
    d = ImageDraw.Draw(img)
    d.text((mm(6), mm(5)), "VISAS", font=font(SANS_B, 4.0), fill=BLUE)
    d.text((mm(92), mm(6)), "SPECIMEN", font=font(SANS_B, 3.0), fill=(170, 40, 40))
    d.ellipse((mm(20), mm(25), mm(55), mm(60)), outline=(120, 60, 140), width=6)
    d.text((mm(27), mm(40)), "ENTRY", font=font(SANS_B, 4.0), fill=(120, 60, 140))
    d.rectangle((mm(70), mm(30), mm(115), mm(55)), outline=(40, 110, 80), width=6)
    d.text((mm(76), mm(39)), "DEPARTURE", font=font(SANS_B, 3.6), fill=(40, 110, 80))
    return img


def qr_id_card():
    """ID-1 card front in the Aadhaar layout: photo left, data column, QR code right.

    The QR carries the legacy XML payload with fictitious values. Offsets match the
    QR-anchored layout in Services/PassportRedactionService.cs (unit q = QR side).
    """
    q = 330  # QR side in pixels
    w, h = int(q * 5.5), int(q * 3.4)
    img = Image.new("RGB", (w, h), (250, 250, 247))
    d = ImageDraw.Draw(img)
    d.rectangle((0, 0, w, int(q * 0.40)), fill=(232, 238, 246))
    d.text((int(q * 0.25), int(q * 0.10)), "SPECIMEN IDENTITY CARD", font=ImageFont.truetype(SANS_B, int(q * 0.17)), fill=BLUE)
    q_left, q_top = int(q * 4.25), int(q * 1.35)

    payload = ('<?xml version="1.0" encoding="UTF-8"?><PrintLetterBarcodeData uid="000012345678" '
               'name="Anna Maria Eriksson" gender="F" yob="1974" dob="12/08/1974" co="" house="1" '
               'street="Specimen Street" loc="" vtc="Zenith" po="" dist="Zenith" subdist="" state="Utopia" pc="000000"/>')
    code = qrcode.QRCode(error_correction=qrcode.constants.ERROR_CORRECT_M, border=0, box_size=10)
    code.add_data(payload)
    code.make(fit=True)
    qr = code.make_image(fill_color="black", back_color="white").convert("RGB").resize((q, q), Image.NEAREST)
    img.paste(qr, (q_left, q_top))

    def at(dx, dy):  # offsets in q units from the QR top-left corner
        return int(q_left + dx * q), int(q_top + dy * q)

    portrait(d, (*at(-3.85, -0.85), *at(-2.45, 0.95)))
    text, small = ImageFont.truetype(SANS_B, int(q * 0.15)), ImageFont.truetype(SANS, int(q * 0.11))
    d.text(at(-2.30, -0.78), "Name", font=small, fill=LABEL)
    d.text(at(-2.30, -0.60), "Anna Maria Eriksson", font=ImageFont.truetype(SANS_B, int(q * 0.13)), fill=INK)
    d.text(at(-2.30, -0.33), "DOB: 12/08/1974", font=text, fill=INK)
    d.text(at(-2.30, -0.05), "Gender: FEMALE", font=text, fill=INK)
    d.text(at(-2.30, 0.55), "0000 1234 5678", font=ImageFont.truetype(SANS_B, int(q * 0.20)), fill=INK)
    d.text(at(-2.30, 1.40), "SPECIMEN - NOT A VALID DOCUMENT", font=small, fill=(170, 40, 40))
    return img


def to_pdf(pages, out):
    """One image per page, about 300 dpi."""
    c = pdf_canvas.Canvas(str(out))
    for page in pages:
        pw, ph = page.width * 72 / 300, page.height * 72 / 300
        c.setPageSize((pw, ph))
        c.drawImage(ImageReader(page), 0, 0, pw, ph)
        c.showPage()
    c.setTitle("Specimen passport, two pages")
    c.setAuthor("")
    c.save()


def main(root):
    root = Path(root)
    td3, card = root / "td3", root / "aadhaar"
    td3.mkdir(parents=True, exist_ok=True)
    card.mkdir(parents=True, exist_ok=True)
    page = passport_page()
    page.save(td3 / "specimen-passport-scan.png", optimize=True)
    phone_photo(page).save(td3 / "specimen-passport-photo.jpg", quality=85)
    to_pdf([other_page(), page], td3 / "specimen-passport-2-pages.pdf")
    qr_id_card().save(card / "specimen-qr-id-card.png", optimize=True)


if __name__ == "__main__":
    main(sys.argv[1] if len(sys.argv) > 1 else "input-docs")
