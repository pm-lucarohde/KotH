"""Small helpers for writing Stride asset YAML by hand.

Ids are derived from names (uuid5), so regenerating an asset keeps every
id and Game Studio sees the same entities, not new ones.
"""
import struct
import uuid

NAMESPACE = uuid.UUID("6b6f7468-0000-4000-8000-000000000000")


def guid(name):
    return str(uuid.uuid5(NAMESPACE, name)).upper()


def key(name):
    """Component dictionary key: 32 lower-case hex digits."""
    return uuid.uuid5(NAMESPACE, "key/" + name).hex


def f32(x):
    """Shortest text that parses back to the same float32."""
    x = struct.unpack("<f", struct.pack("<f", x))[0]
    for digits in range(1, 10):
        s = "%.*g" % (digits, x)
        if struct.unpack("<f", struct.pack("<f", float(s)))[0] == x:
            break
    if "e" in s and 1e-5 <= abs(x) < 1e9:
        from decimal import Decimal
        s = format(Decimal(s), "f")
    if "e" not in s and "." not in s and "inf" not in s and "nan" not in s:
        s += ".0"
    return s.replace("e-0", "E-").replace("e+0", "E+").replace("e-", "E-").replace("e+", "E+")


def vec3(v):
    return "{X: %s, Y: %s, Z: %s}" % tuple(f32(c) for c in v)


def quat(q):
    return "{X: %s, Y: %s, Z: %s, W: %s}" % tuple(f32(c) for c in q)


def bits_to_float(hexbits):
    return struct.unpack("<f", struct.pack("<I", int(hexbits, 16)))[0]


def indent(text, n):
    pad = " " * n
    return "\n".join(pad + line if line else line for line in text.split("\n"))
