#!/usr/bin/env python3
"""
Display resource (.bin) parser & generator for 256×32 monochrome LCD.

File format (per frame, 133 bytes total):
    ┌────────┬──────────┬─────────┬──────────────────┐
    │ 01 00  │ ID(u16)  │  0x64   │  128 bytes data  │
    │ magic  │ LE       │ param   │  SSD1306 format   │
    └────────┴──────────┴─────────┴──────────────────┘

SSD1306 column-major encoding: each byte = 8 vertical pixels in one
column (LSB = top, MSB = bottom). 1 bit = white/on, 0 bit = black/off.

8 frames compose one 256×32 display image:
    Page 0 (rows  0- 7): Frame 0 (cols   0-127) + Frame 1 (cols 128-255)
    Page 1 (rows  8-15): Frame 2 (cols   0-127) + Frame 3 (cols 128-255)
    Page 2 (rows 16-23): Frame 4 (cols   0-127) + Frame 5 (cols 128-255)
    Page 3 (rows 24-31): Frame 6 (cols   0-127) + Frame 7 (cols 128-255)

Usage:
    python parse_bin.py info     <file.bin>                查看文件信息
    python parse_bin.py extract  <file.bin> [--out DIR]    提取 PNG 序列
    python parse_bin.py generate <out.bin>  <img.png> ...  单张/多张 → bin
    python parse_bin.py generate <out.bin>  --dir <DIR>    整个目录 → bin
"""

import struct
import sys
import os
import re
import glob as glob_mod
from pathlib import Path

# ── Display constants ──────────────────────────────────────────────
DISPLAY_W = 256
DISPLAY_H = 32
BYTES_PER_HALF = 128          # 128 columns per half-page
FRAMES_PER_IMG = 8            # 4 pages × 2 halves
HEADER_SIZE   = 5             # magic(2) + id(2) + param(1)
FRAME_SIZE    = HEADER_SIZE + BYTES_PER_HALF  # 133
MAGIC         = b'\x01\x00'
PARAM         = 0x64

# ── Natural sort for numbered filenames ────────────────────────────
def _nat_key(s):
    """Natural sort key: 'frame_2.png' < 'frame_10.png'."""
    s = os.path.basename(s) if isinstance(s, str) else str(s)
    return [int(t) if t.isdigit() else t.lower() for t in re.split(r'(\d+)', s)]


def _collect_pngs(paths, from_dir=None):
    """
    Collect and sort PNG paths from multiple sources.

    Returns a sorted list of (label, path) tuples.
    Handles: individual files, directory scan, glob patterns.
    """
    pngs = []

    # Directory mode
    if from_dir:
        d = Path(from_dir)
        if not d.is_dir():
            print(f"  Error: not a directory: {from_dir}")
            sys.exit(1)
        for f in sorted(d.iterdir(), key=lambda x: _nat_key(x.name)):
            if f.suffix.lower() == '.png':
                pngs.append(f)
    else:
        # Individual files / glob patterns
        for p in paths:
            expanded = glob_mod.glob(p)
            if expanded:
                pngs.extend(Path(x) for x in expanded)
            else:
                # Not a glob — accept as literal path (will error later if missing)
                pngs.append(Path(p))

    if not pngs:
        print("  Error: no PNG files found")
        sys.exit(1)

    # Deduplicate while preserving order
    seen = set()
    uniq = []
    for p in pngs:
        rp = p.resolve()
        if rp not in seen:
            seen.add(rp)
            uniq.append(p)
    pngs = sorted(uniq, key=lambda x: _nat_key(x.name))

    print(f"  Found {len(pngs)} PNG(s):")
    for p in pngs:
        print(f"    {p.name}")
    return pngs


# ── Dithering ──────────────────────────────────────────────────────
def _floyd_steinberg(img_gray):
    """
    Floyd-Steinberg dithering on a grayscale PIL Image.
    Returns a new '1' (1-bit) Image with error-diffused dithering.
    """
    import numpy as np
    w, h = img_gray.size
    arr = np.array(img_gray, dtype=np.float32)
    out = np.zeros((h, w), dtype=np.uint8)

    for y in range(h):
        for x in range(w):
            old = arr[y, x]
            new = 255.0 if old > 127.0 else 0.0
            out[y, x] = 255 if new > 127 else 0
            err = old - new
            if x + 1 < w:
                arr[y, x + 1] += err * 7 / 16
            if y + 1 < h:
                if x > 0:
                    arr[y + 1, x - 1] += err * 3 / 16
                arr[y + 1, x]     += err * 5 / 16
                if x + 1 < w:
                    arr[y + 1, x + 1] += err * 1 / 16
    try:
        from PIL import Image
    except ImportError:
        pass
    return Image.fromarray(out, mode='L').convert('1')


def _preprocess_image(img):
    """
    Preprocess a PIL Image for encoding:
      1. Convert to grayscale
      2. Resize to 256×32 (stretch to fill)
    Returns a '1'-mode PIL Image.
    """
    if img.mode != 'L':
        img = img.convert('L')
    if img.size != (DISPLAY_W, DISPLAY_H):
        print(f"    resize {img.size[0]}×{img.size[1]} → {DISPLAY_W}×{DISPLAY_H}")
        img = img.resize((DISPLAY_W, DISPLAY_H), Image.LANCZOS)
    # 纯阈值二值化
    img = img.point(lambda p: 255 if p >= 64 else 0, mode='1')
    return img


# ── Core encode / decode ───────────────────────────────────────────
def _encode_image(img):
    """
    Encode a 256×32 '1'-mode PIL Image into 8 frame blobs (bytes).
    Returns list of 8 bytearrays.
    """
    if img.size != (DISPLAY_W, DISPLAY_H) or img.mode != '1':
        img = _preprocess_image(img)
    px = img.load()
    frames = []
    for page in range(4):  # 0..3
        left  = bytearray(BYTES_PER_HALF)
        right = bytearray(BYTES_PER_HALF)
        for col in range(BYTES_PER_HALF):
            bl, br = 0, 0
            for bit in range(8):
                row = page * 8 + bit
                if px[col, row]:
                    bl |= (1 << bit)
                if px[col + BYTES_PER_HALF, row]:
                    br |= (1 << bit)
            left[col], right[col] = bl, br
        frames.append(left)
        frames.append(right)
    return frames


def _decode_image(frame_datas):
    """
    Decode 8 frame blobs → 256×32 '1'-mode PIL Image.
    frame_datas: list of 8 byte-like objects (128 bytes each).
    """
    from PIL import Image
    img = Image.new('1', (DISPLAY_W, DISPLAY_H))
    px = img.load()
    for page in range(4):
        ld = frame_datas[page * 2]
        rd = frame_datas[page * 2 + 1]
        for col in range(BYTES_PER_HALF):
            for bit in range(8):
                row = page * 8 + bit
                if ld[col] & (1 << bit):
                    px[col, row] = 1
                if rd[col] & (1 << bit):
                    px[col + BYTES_PER_HALF, row] = 1
    return img


# ── Subcommand: info ───────────────────────────────────────────────
def cmd_info(filepath):
    """Print detailed file structure information."""
    with open(filepath, 'rb') as f:
        data = f.read()
    total = len(data)
    n_frames = total // FRAME_SIZE
    n_images = n_frames // FRAMES_PER_IMG
    rem = total % FRAME_SIZE

    print(f"File       : {filepath}")
    print(f"Size       : {total} bytes")
    print(f"Frames     : {n_frames}  (each {FRAME_SIZE} bytes)")
    print(f"Images     : {n_images}  (each {FRAMES_PER_IMG} frames → 256×32)")
    if rem:
        print(f"Warning    : trailing {rem} bytes (not a multiple of {FRAME_SIZE})")
    print()

    if n_frames == 0:
        return

    print(f"{'Frame':>5}  {'ID':>4}  {'Offset':>8}  {'Unique':>6}  {'Zeros':>5}  First 12 bytes")
    print("-" * 75)
    for i in range(n_frames):
        off = i * FRAME_SIZE
        hdr = data[off:off + HEADER_SIZE]
        fid = struct.unpack('<H', hdr[2:4])[0]
        body = data[off + HEADER_SIZE:off + FRAME_SIZE]
        uniq = len(set(body))
        zcnt = sum(1 for b in body if b == 0)
        preview = body[:12].hex(' ')
        print(f"  {i:3d}   {fid:4d}  0x{off:06X}   {uniq:6d}   {zcnt:5d}  {preview}")

    if n_images > 0:
        print(f"\nImage layout (each = {FRAMES_PER_IMG} frames → 256×32):")
        for img_idx in range(n_images):
            base = img_idx * FRAMES_PER_IMG
            parts = []
            for k in range(FRAMES_PER_IMG):
                page = k // 2
                lr  = "L" if k % 2 == 0 else "R"
                parts.append(f"F{base + k}(p{page}{lr})")
            print(f"  Image {img_idx}: {' + '.join(parts)}")


# ── Subcommand: extract ────────────────────────────────────────────
def cmd_extract(filepath, out_dir=None):
    """Extract all display images from a .bin file as PNGs."""
    if out_dir is None:
        out_dir = Path(filepath).stem + "_extracted"
    os.makedirs(out_dir, exist_ok=True)

    with open(filepath, 'rb') as f:
        data = f.read()
    n_frames = len(data) // FRAME_SIZE
    n_images = n_frames // FRAMES_PER_IMG

    print(f"Extracting {n_images} image(s) from {filepath} …")
    for img_idx in range(n_images):
        frame_datas = []
        for k in range(FRAMES_PER_IMG):
            off = (img_idx * FRAMES_PER_IMG + k) * FRAME_SIZE
            frame_datas.append(data[off + HEADER_SIZE:off + FRAME_SIZE])
        img = _decode_image(frame_datas)
        fname = f"frame_{img_idx:04d}.png"
        img.save(os.path.join(out_dir, fname))
        print(f"  [{img_idx+1}/{n_images}] {fname}")
    print(f"Done → {out_dir}/")


# ── Subcommand: generate ───────────────────────────────────────────
def cmd_generate(output_path, paths=None, from_dir=None):
    """
    Generate a .bin file from one or more PNG images.

    Supports:
      - Single PNG  → single-image .bin
      - Multiple PNGs → multi-image .bin (animation sequence)
      - --dir DIR   → all PNGs in directory, naturally sorted
    """
    pngs = _collect_pngs(paths or [], from_dir)
    all_frames = []  # list of bytearrays

    print(f"Generating {output_path} …")
    for png_path in pngs:
        print(f"  {png_path.name}")
        img = Image.open(png_path)
        frames = _encode_image(img)
        all_frames.extend(frames)

    # Write binary
    os.makedirs(os.path.dirname(output_path) or '.', exist_ok=True)
    with open(output_path, 'wb') as f:
        for i, fd in enumerate(all_frames):
            f.write(MAGIC)
            f.write(struct.pack('<H', i))
            f.write(bytes([PARAM]))
            f.write(bytes(fd))

    n_images = len(all_frames) // FRAMES_PER_IMG
    print(f"Done → {output_path}  ({n_images} images, {len(all_frames)} frames, "
          f"{len(all_frames) * FRAME_SIZE} bytes)")


# ── CLI dispatch ───────────────────────────────────────────────────
def main():
    if len(sys.argv) < 2:
        print(__doc__)
        return

    cmd = sys.argv[1].lower()

    if cmd == 'info':
        if len(sys.argv) < 3:
            print("Usage: python parse_bin.py info <file.bin>")
            sys.exit(1)
        cmd_info(sys.argv[2])

    elif cmd == 'extract':
        if len(sys.argv) < 3:
            print("Usage: python parse_bin.py extract <file.bin> [--out DIR]")
            sys.exit(1)
        filepath = sys.argv[2]
        out_dir = None
        args = sys.argv[3:]
        i = 0
        while i < len(args):
            if args[i] == '--out' and i + 1 < len(args):
                out_dir = args[i + 1]; i += 2
            else:
                i += 1
        cmd_extract(filepath, out_dir)

    elif cmd == 'generate':
        args = sys.argv[2:]
        if len(args) < 2:
            print("Usage: python parse_bin.py generate <out.bin> <img.png> [...]")
            print("       python parse_bin.py generate <out.bin> --dir <DIR>")
            sys.exit(1)
        output_path = args[0]
        rest = args[1:]
        from_dir = None
        paths = []
        i = 0
        while i < len(rest):
            if rest[i] == '--dir' and i + 1 < len(rest):
                from_dir = rest[i + 1]; i += 2
            else:
                paths.append(rest[i]); i += 1
        cmd_generate(output_path, paths, from_dir)

    else:
        print(f"Unknown command: {cmd}")
        print(__doc__)
        sys.exit(1)


# ── Entry point ────────────────────────────────────────────────────
if __name__ == '__main__':
    try:
        from PIL import Image
        import numpy as np
    except ImportError as e:
        print(f"Missing dependency: {e}")
        print("Install with: pip install Pillow numpy")
        sys.exit(1)
    main()
