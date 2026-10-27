import struct
import sys
from pathlib import Path

ivf = Path(sys.argv[1]).read_bytes()
header_length = struct.unpack_from("<H", ivf, 6)[0]
frame_size = struct.unpack_from("<I", ivf, header_length)[0]
frame_start = header_length + 12
frame = ivf[frame_start:frame_start + frame_size]
if len(frame) != frame_size:
    raise ValueError(f"Expected {frame_size} frame bytes, found {len(frame)}.")

chunk = b"VP8 " + struct.pack("<I", frame_size) + frame
if frame_size & 1:
    chunk += b"\0"
body = b"WEBP" + chunk
Path(sys.argv[2]).write_bytes(b"RIFF" + struct.pack("<I", len(body)) + body)
