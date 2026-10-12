#!/usr/bin/env python3
"""Extract the client's bundled balance JSON (TextAsset "balance-client" in sharedassets1.assets) for the server.

    scripts/extract-balance.py [BUILD]

Writes ~/CelticHeroesAnalysis/extracted/<build>/balance-client.json (outside git: it is client data).
TextAsset layout: int32 name length, name bytes (4-byte aligned), int32 script length, script bytes.
"""
import os, struct, sys

build = sys.argv[1] if len(sys.argv) > 1 else "24928510"
src = os.path.expanduser(f"~/CelticHeroesAnalysis/client/{build}/CelticHeroes.app/Contents/Resources/Data/sharedassets1.assets")
dst_dir = os.path.expanduser(f"~/CelticHeroesAnalysis/extracted/{build}")
data = open(src, "rb").read()
name = b"balance-client"
i = data.find(name)
if i < 4 or struct.unpack_from("<i", data, i - 4)[0] != len(name):
    sys.exit("balance-client TextAsset not found")
j = (i + len(name) + 3) & ~3
size = struct.unpack_from("<i", data, j)[0]
body = data[j + 4 : j + 4 + size]
if not body.lstrip(b"\xef\xbb\xbf").startswith(b"{"):
    sys.exit("unexpected TextAsset content")
os.makedirs(dst_dir, exist_ok=True)
open(os.path.join(dst_dir, "balance-client.json"), "wb").write(body)
print(f"wrote {len(body):,} bytes to {dst_dir}/balance-client.json")
