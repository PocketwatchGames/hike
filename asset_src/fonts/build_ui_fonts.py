"""Builds assets/fonts/alegreya_sans/*.ttf from the originals beside this script.

Alegreya Sans sets OS/2 USE_TYPO_METRICS, and Godot honours it: the line box
becomes the typo ascender/descender (0.8 / 0.2 em) with the typo line gap
dropped, so text sits high in its box and lines run a full 1.0 em apart.
Clearing the flag makes Godot use hhea instead (0.9 / 0.3 em), which is the
same box with the designer's 0.2 em gap split above and below. hhea is then
widened further (LINE_ASCENT / LINE_DESCENT): at the UI's 14-16 px body sizes
that is one more pixel above (the text still read a pixel high) and one more
below, for two more pixels of leading. Godot rounds both UP to whole pixels,
so the values sit just clear of a rounding step at 14 and 16 px. It is a fix
in em units, so it holds at every font size; a FontVariation spacing_top /
spacing_bottom would be a fixed pixel count, right at one size only.

No outlines are touched. The font has no Reserved Font Name, so the OFL
permits shipping the modified file under its own name.

    python asset_src/fonts/build_ui_fonts.py

Godot does not watch asset_src/, so rerun this by hand after editing it.
"""

import os
import struct

HERE = os.path.dirname(os.path.abspath(__file__))
SRC_DIR = os.path.join(HERE, "alegreya_sans")
OUT_DIR = os.path.join(HERE, "..", "..", "assets", "fonts", "alegreya_sans")

FACES = (
    "AlegreyaSans-Regular.ttf",
    "AlegreyaSans-Italic.ttf",
    "AlegreyaSans-Medium.ttf",
    "AlegreyaSans-MediumItalic.ttf",
)

# Line box in em, above and below the baseline. The font ships 0.9 / 0.3.
LINE_ASCENT = 0.965
LINE_DESCENT = 0.365

USE_TYPO_METRICS = 1 << 7
HHEA_ASCENDER = 4
HHEA_DESCENDER = 6
HHEA_LINE_GAP = 8
HEAD_UNITS_PER_EM = 18
OS2_FS_SELECTION = 62
HEAD_CHECKSUM_ADJUSTMENT = 8
CHECKSUM_MAGIC = 0xB1B0AFBA


def checksum(data):
    data += b"\0" * (-len(data) % 4)
    return sum(struct.unpack(f">{len(data) // 4}I", data)) & 0xFFFFFFFF


def table_directory(font):
    count = struct.unpack(">H", font[4:6])[0]
    tables = {}
    for i in range(count):
        record = 12 + 16 * i
        tag, _, offset, length = struct.unpack(">4sIII", font[record:record + 16])
        tables[tag.decode("latin-1")] = (record, offset, length)
    return tables


def patch(font):
    font = bytearray(font)
    tables = table_directory(font)

    _, os2, _ = tables["OS/2"]
    selection = struct.unpack(">H", font[os2 + OS2_FS_SELECTION:os2 + OS2_FS_SELECTION + 2])[0]
    struct.pack_into(">H", font, os2 + OS2_FS_SELECTION, selection & ~USE_TYPO_METRICS)

    _, head, _ = tables["head"]
    upm = struct.unpack(">H", font[head + HEAD_UNITS_PER_EM:head + HEAD_UNITS_PER_EM + 2])[0]
    _, hhea, _ = tables["hhea"]
    struct.pack_into(">h", font, hhea + HHEA_ASCENDER, round(LINE_ASCENT * upm))
    struct.pack_into(">h", font, hhea + HHEA_DESCENDER, -round(LINE_DESCENT * upm))
    struct.pack_into(">h", font, hhea + HHEA_LINE_GAP, 0)

    # Table checksums, then the whole-file adjustment (computed with it zeroed).
    struct.pack_into(">I", font, head + HEAD_CHECKSUM_ADJUSTMENT, 0)
    for record, offset, length in tables.values():
        struct.pack_into(">I", font, record + 4, checksum(bytes(font[offset:offset + length])))
    adjustment = (CHECKSUM_MAGIC - checksum(bytes(font))) & 0xFFFFFFFF
    struct.pack_into(">I", font, head + HEAD_CHECKSUM_ADJUSTMENT, adjustment)
    return bytes(font)


def main():
    os.makedirs(OUT_DIR, exist_ok=True)
    for face in FACES:
        with open(os.path.join(SRC_DIR, face), "rb") as f:
            data = f.read()
        out = os.path.join(OUT_DIR, face)
        with open(out, "wb") as f:
            f.write(patch(data))
        print(f"wrote {os.path.normpath(out)}")


if __name__ == "__main__":
    main()
