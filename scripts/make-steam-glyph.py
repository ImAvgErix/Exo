#!/usr/bin/env python3
"""Write a lightweight Steam-style glyph (white mark, no solid disc) for AMOLED top bar."""
from __future__ import annotations

import struct
import zlib
from pathlib import Path


def write_png(path: Path, w: int, h: int, rgba: bytes) -> None:
    def chunk(tag: bytes, data: bytes) -> bytes:
        return struct.pack(">I", len(data)) + tag + data + struct.pack(
            ">I", zlib.crc32(tag + data) & 0xFFFFFFFF
        )

    raw = bytearray()
    for y in range(h):
        raw.append(0)
        raw.extend(rgba[y * w * 4 : (y + 1) * w * 4])

    ihdr = struct.pack(">IIBBBBB", w, h, 8, 6, 0, 0, 0)
    png = b"\x89PNG\r\n\x1a\n" + chunk(b"IHDR", ihdr) + chunk(
        b"IDAT", zlib.compress(bytes(raw), 9)
    ) + chunk(b"IEND", b"")
    path.write_bytes(png)


def set_px(buf: bytearray, w: int, x: int, y: int, a: int = 255) -> None:
    if 0 <= x < w and 0 <= y < w:
        i = (y * w + x) * 4
        buf[i : i + 4] = bytes((255, 255, 255, a))


def draw_circle(buf: bytearray, w: int, cx: float, cy: float, r: float, thickness: float) -> None:
    r0, r1 = r - thickness / 2, r + thickness / 2
    for y in range(w):
        for x in range(w):
            d = ((x + 0.5 - cx) ** 2 + (y + 0.5 - cy) ** 2) ** 0.5
            if r0 <= d <= r1:
                edge = min(1.0, (thickness / 2) - abs(d - r) + 0.5)
                set_px(buf, w, x, y, max(0, min(255, int(edge * 255))))


def fill_circle(buf: bytearray, w: int, cx: float, cy: float, r: float) -> None:
    for y in range(w):
        for x in range(w):
            d = ((x + 0.5 - cx) ** 2 + (y + 0.5 - cy) ** 2) ** 0.5
            if d <= r:
                a = 255 if d < r - 0.6 else int(max(0, (r - d) / 0.6) * 255)
                set_px(buf, w, x, y, a)


def draw_line(buf: bytearray, w: int, x0: float, y0: float, x1: float, y1: float, thickness: float) -> None:
    steps = int(max(abs(x1 - x0), abs(y1 - y0)) * 2) + 1
    for i in range(steps + 1):
        t = i / steps
        fill_circle(buf, w, x0 + (x1 - x0) * t, y0 + (y1 - y0) * t, thickness / 2)


def main() -> None:
    w = 256
    buf = bytearray(w * w * 4)
    # Hollow ring (not a solid white disc) + crank arm — closer to wifi glyph weight.
    draw_circle(buf, w, 128, 128, 96, 14)
    fill_circle(buf, w, 88, 168, 28)
    fill_circle(buf, w, 168, 88, 18)
    draw_line(buf, w, 100, 156, 158, 98, 18)

    roots = [
        Path("/workspace/Exo/Assets/Logos/steam.png"),
        Path("/workspace/tools/Exo.UiPreview/public/logos/steam.png"),
    ]
    for path in roots:
        write_png(path, w, w, bytes(buf))
        print(f"wrote {path}")


if __name__ == "__main__":
    main()
