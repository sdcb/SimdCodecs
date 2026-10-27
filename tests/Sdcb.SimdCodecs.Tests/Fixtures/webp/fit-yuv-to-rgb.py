#!/usr/bin/env python3
"""Fit dwebp's no-fancy YUV-to-RGB conversion from paired oracle samples.

The input directory must contain matching .pam files from `dwebp -nofancy -pam`
and .yuv files from `dwebp -yuv`. Requires NumPy.
"""

import argparse
import itertools
import re
import sys
from pathlib import Path

import numpy as np


parser = argparse.ArgumentParser()
parser.add_argument(
    "sample_directory",
    type=Path,
    help="directory containing paired dwebp .pam and .yuv oracle outputs",
)
args = parser.parse_args()

Y = []
U = []
V = []
R = []
G = []
B = []
sample_paths = sorted(args.sample_directory.glob("*.pam"))
if not sample_paths:
    parser.error(f"no .pam oracle samples found in {args.sample_directory}")

for pam_path in sample_paths:
    d = pam_path.read_bytes()
    i = d.index(b"ENDHDR\n") + 7
    hdr = d[:i].decode()
    w = int(re.search(r"WIDTH (\d+)", hdr).group(1))
    h = int(re.search(r"HEIGHT (\d+)", hdr).group(1))
    px = np.frombuffer(d[i:], np.uint8).reshape(h, w, 4).astype(np.int64)
    yuv_path = pam_path.with_suffix(".yuv")
    yuv = np.frombuffer(yuv_path.read_bytes(), np.uint8).astype(np.int64)
    cw, ch = (w + 1) // 2, (h + 1) // 2
    y = yuv[: w * h].reshape(h, w)
    u = yuv[w * h : w * h + cw * ch].reshape(ch, cw)
    v = yuv[w * h + cw * ch :].reshape(ch, cw)
    uu = u.repeat(2, 0).repeat(2, 1)[:h, :w]
    vv = v.repeat(2, 0).repeat(2, 1)[:h, :w]
    for values, array in (
        (Y, y),
        (U, uu),
        (V, vv),
        (R, px[..., 0]),
        (G, px[..., 1]),
        (B, px[..., 2]),
    ):
        values.append(array.ravel())

Y, U, V, R, G, B = [
    np.concatenate(values) for values in (Y, U, V, R, G, B)
]
print("samples", len(Y))


def fit(name, output, terms, ranges, shift=6):
    mask = (output > 0) & (output < 255)
    key = np.unique(
        np.stack([term[mask] for term in terms] + [output[mask]], 1), axis=0
    )
    xs = [key[:, i] for i in range(len(terms))]
    values = key[:, -1]
    lower = values << shift
    upper = (values + 1) << shift
    first_coefficients = np.arange(*ranges[0])
    results = []
    for rest in itertools.product(*[range(*bounds) for bounds in ranges[1:]]):
        other_terms = sum(
            (xs[i + 1] * rest[i]) >> 8 for i in range(len(rest))
        )
        totals = (
            (xs[0][None, :] * first_coefficients[:, None]) >> 8
        ) + other_terms[None, :]
        matches = (lower - totals).max(1) < (upper - totals).min(1)
        for coefficient in first_coefficients[matches]:
            total = ((xs[0] * coefficient) >> 8) + other_terms
            results.append(
                (
                    int(coefficient),
                    *rest,
                    int((lower - total).max()),
                    int((upper - total).min()),
                )
            )
    print(name, "unique", len(values), "fits", len(results), results[:6])
    sys.stdout.flush()


fit("R", R, [Y, V], [(18900, 19250), (25900, 26400)])
fit("B", B, [Y, U], [(18900, 19250), (32800, 33300)])
mask = (G > 0) & (G < 255)
key = np.unique(np.stack([Y[mask], U[mask], V[mask], G[mask]], 1), axis=0)
y, u, v, output = key.T
y_term = (y * 19077) >> 8
lower = output << 6
upper = (output + 1) << 6
u_coefficients = np.arange(6200, 6700)
results = []
for v_coefficient in range(13100, 13600):
    totals = (
        y_term[None, :]
        - ((u[None, :] * u_coefficients[:, None]) >> 8)
        - ((v * v_coefficient) >> 8)[None, :]
    )
    matches = (lower - totals).max(1) < (upper - totals).min(1)
    for u_coefficient in u_coefficients[matches]:
        total = (
            y_term
            - ((u * u_coefficient) >> 8)
            - ((v * v_coefficient) >> 8)
        )
        results.append(
            (
                int(u_coefficient),
                v_coefficient,
                int((lower - total).max()),
                int((upper - total).min()),
            )
        )
print("G unique", len(output), "fits", len(results), results[:6])


def mh(value, coefficient):
    return (value * coefficient) >> 8


clamp = lambda value: np.clip(value >> 6, 0, 255)
red = clamp(mh(Y, 19077) + mh(V, 26149) - 14234)
blue = clamp(mh(Y, 19077) + mh(U, 33050) - 17685)
print("R mism", int((red != R).sum()), "B mism", int((blue != B).sum()))
if results:
    u_coefficient, v_coefficient, constant, _ = results[0]
    green = clamp(
        mh(Y, 19077)
        - mh(U, u_coefficient)
        - mh(V, v_coefficient)
        + constant
    )
    print("G mism", int((green != G).sum()))
