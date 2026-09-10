#!/usr/bin/env python3
"""make-hostile-corpus.py — S2.9 (issue #25)

Builds the deliberately malformed DMG corpus in ``fixtures/hostile/`` by
MUTATING a good fixture, plus ``fixtures/hostile/cases.json`` describing every
case and the exit code the reader is contractually required to produce for it.

Why mutation rather than synthesis
----------------------------------
A hand-rolled hostile image only proves the reader survives structures we
imagined. Mutating an image ``hdiutil`` actually wrote keeps every field we did
not deliberately break byte-for-byte genuine, so a case that fails fails for the
reason it names and not because the surrounding container was fictional.

The source is ``fixtures/generated/exfat-zlib.dmg`` (a ~33 KB UDZO image with a
koly trailer, an XML property list, two blkx regions and a real data fork), with
``exfat-sparse.dmg`` as a fallback. Run ``tools/make-fixtures.sh`` first.

The contract these cases exist to prove
---------------------------------------
For EVERY image in this corpus the reader must produce a clean non-zero exit
with a specific ``DmgExitCode`` and a useful message:

  * no unhandled exception,
  * no hang — each case carries a wall-clock budget,
  * no allocation spike — each case carries a managed-allocation ceiling.

``cases.json`` carries all three bounds per case, so the assertions live next to
the mutation that motivates them rather than in a table somewhere else.
``tests/Dmg.Core.Tests/Containers/HostileCorpusTests.cs`` consumes it.

Safety
------
This script never calls ``hdiutil``: it only reads one already-generated file
and writes new files under ``fixtures/hostile/``. It attaches nothing and
mounts nothing, so none of the device-detach hazards in
``tools/make-fixtures.sh`` apply here.

Usage
-----
    tools/make-hostile-corpus.py            # rebuild fixtures/hostile/
    tools/make-hostile-corpus.py --source fixtures/generated/adc.dmg
"""

from __future__ import annotations

import argparse
import base64
import hashlib
import json
import plistlib
import struct
import sys
from pathlib import Path

# --------------------------------------------------------------------------
# koly trailer
# --------------------------------------------------------------------------

KOLY_SIZE = 512
KOLY_MAGIC = b"koly"

# offset -> (struct format, field name). Only the fields we mutate are listed;
# everything else is copied through untouched.
KOLY_FIELDS = {
    "Version": (0x004, ">I"),
    "HeaderSize": (0x008, ">I"),
    "DataForkOffset": (0x018, ">Q"),
    "DataForkLength": (0x020, ">Q"),
    "XmlOffset": (0x0D8, ">Q"),
    "XmlLength": (0x0E0, ">Q"),
    "SectorCount": (0x1EC, ">Q"),
}

# mish block header (see docs/03-udif-format-reference.md)
MISH_SIGNATURE = 0x00
MISH_FIRST_SECTOR = 0x08
MISH_SECTOR_COUNT = 0x10
MISH_CHUNK_COUNT = 0xC8
MISH_CHUNK_TABLE = 0xCC
MISH_CHUNK_SIZE = 40

# chunk descriptor field offsets, relative to the descriptor
CHUNK_TYPE = 0x00
CHUNK_SECTOR_NUMBER = 0x08
CHUNK_SECTOR_COUNT = 0x10
CHUNK_COMPRESSED_OFFSET = 0x18
CHUNK_COMPRESSED_LENGTH = 0x20

U64_MAX = 0xFFFFFFFFFFFFFFFF
U32_MAX = 0xFFFFFFFF

# Sector counts at or above this overflow a 64-bit byte count when multiplied
# by 512, which is exactly what BigEndian.SectorsToBytes has to refuse.
SECTORS_THAT_OVERFLOW_X512 = 0x0100000000000000


def koly_get(koly: bytes, name: str) -> int:
    offset, fmt = KOLY_FIELDS[name]
    return struct.unpack_from(fmt, koly, offset)[0]


def koly_set(koly: bytes, **values: int) -> bytes:
    out = bytearray(koly)
    for name, value in values.items():
        offset, fmt = KOLY_FIELDS[name]
        struct.pack_into(fmt, out, offset, value)
    return bytes(out)


# --------------------------------------------------------------------------
# the source image, taken apart
# --------------------------------------------------------------------------


class Image:
    """One UDIF image split into the three parts we mutate independently."""

    def __init__(self, raw: bytes) -> None:
        if len(raw) < KOLY_SIZE:
            raise SystemExit("source image is smaller than a koly trailer")
        self.koly = raw[-KOLY_SIZE:]
        if self.koly[:4] != KOLY_MAGIC:
            raise SystemExit("source image has no koly trailer; pick another fixture")
        self.raw = raw
        xml_offset = koly_get(self.koly, "XmlOffset")
        xml_length = koly_get(self.koly, "XmlLength")
        if xml_length == 0 or xml_offset + xml_length > len(raw) - KOLY_SIZE:
            raise SystemExit("source image has no usable XML property list")
        fork_offset = koly_get(self.koly, "DataForkOffset")
        fork_length = koly_get(self.koly, "DataForkLength")
        if fork_offset != 0:
            raise SystemExit("source image has a non-zero DataForkOffset; unsupported here")
        self.data_fork = raw[fork_offset:fork_offset + fork_length]
        self.xml = raw[xml_offset:xml_offset + xml_length]
        # Anything between the data fork and the plist (hdiutil leaves nothing,
        # but do not assume it).
        self.gap = raw[fork_offset + fork_length:xml_offset]
        self.plist = plistlib.loads(self.xml)

    def blkx(self) -> list[dict]:
        return self.plist["resource-fork"]["blkx"]


def assemble(image: Image, *, data_fork: bytes | None = None,
             xml: bytes | None = None, koly_overrides: dict[str, int] | None = None,
             fix_lengths: bool = True) -> bytes:
    """Rebuild an image from parts, keeping the koly honest by default.

    ``fix_lengths`` recomputes DataForkLength/XmlOffset/XmlLength from the parts
    actually written, so a mutation that changes a size does not accidentally
    also become a "koly points outside the file" case. Set it False when the
    inconsistency IS the mutation.
    """
    fork = image.data_fork if data_fork is None else data_fork
    body = image.xml if xml is None else xml
    koly = image.koly
    if fix_lengths:
        koly = koly_set(
            koly,
            DataForkLength=len(fork),
            XmlOffset=len(fork) + len(image.gap),
            XmlLength=len(body),
        )
    if koly_overrides:
        koly = koly_set(koly, **koly_overrides)
    return fork + image.gap + body + koly


def plist_to_xml(plist: dict) -> bytes:
    return plistlib.dumps(plist, fmt=plistlib.FMT_XML)


def patched_plist(image: Image, mutate) -> bytes:
    """Deep-copy the source plist, hand it to ``mutate``, return XML bytes."""
    plist = plistlib.loads(image.xml)
    mutate(plist["resource-fork"]["blkx"])
    return plist_to_xml(plist)


def patch_u64(data: bytes, offset: int, value: int) -> bytes:
    out = bytearray(data)
    struct.pack_into(">Q", out, offset, value)
    return bytes(out)


def patch_u32(data: bytes, offset: int, value: int) -> bytes:
    out = bytearray(data)
    struct.pack_into(">I", out, offset, value)
    return bytes(out)


def chunk_offset(index: int) -> int:
    return MISH_CHUNK_TABLE + (index * MISH_CHUNK_SIZE)


def mutable_region(image: Image) -> int:
    """Index of a blkx region big enough to carry every chunk-level mutation.

    That means at least two chunks before the terminator (so one can be moved on
    top of the other) and a first chunk spanning at least two sectors (so one can
    be taken away to open a gap). The one-sector MBR region at the front of an
    hdiutil image satisfies neither.
    """
    for index, entry in enumerate(image.blkx()):
        data = entry["Data"]
        count = struct.unpack_from(">I", data, MISH_CHUNK_COUNT)[0]
        if len(data) < chunk_offset(count):
            continue
        real = [slot for slot in range(count)
                if struct.unpack_from(">I", data, chunk_offset(slot) + CHUNK_TYPE)[0]
                != 0xFFFFFFFF]
        if len(real) < 2 or real[:2] != [0, 1]:
            continue
        if struct.unpack_from(">Q", data, chunk_offset(0) + CHUNK_SECTOR_COUNT)[0] >= 2:
            return index
    raise SystemExit(
        "no blkx region in the source has two chunks and a multi-sector first chunk; "
        "pick a different --source")


# --------------------------------------------------------------------------
# the corpus
# --------------------------------------------------------------------------

# Managed-allocation ceiling for a case that has no business allocating much.
# Generous next to the few hundred kilobytes a refusal really costs, and three
# orders of magnitude below the multi-gigabyte spike we are guarding against.
DEFAULT_MAX_MIB = 64

# Wall-clock budget. A refusal is microseconds of work; this only has to be
# tight enough that a hang or a quadratic blow-up trips it on a loaded CI box.
DEFAULT_BUDGET_MS = 10000

CASES: list[dict] = []


def case(name: str, *, body: bytes, mutation: str, expect: list[str], why: str,
         max_mib: int = DEFAULT_MAX_MIB, why_max: str = "",
         budget_ms: int = DEFAULT_BUDGET_MS, boundary: str = "") -> None:
    CASES.append({
        "name": name,
        "bytes": body,
        "mutation": mutation,
        "expect": expect,
        "why": why,
        "max_managed_mib": max_mib,
        "why_max": why_max,
        "budget_ms": budget_ms,
        "boundary": boundary,
    })


def build_cases(image: Image) -> None:
    fork = image.data_fork
    xml = image.xml
    region = mutable_region(image)

    # ---------------------------------------------------------------- truncation

    case(
        "truncated-mid-plist.dmg",
        body=assemble(image, xml=xml[:len(xml) // 2]),
        mutation="The XML property list is cut in half; koly.XMLLength follows it down, "
                 "so the trailer is self-consistent and only the plist is broken.",
        expect=["CorruptImage"],
        why="An XML document that stops in the middle of an element is malformed, "
            "and a malformed plist is a corrupt image rather than an unknown format.",
        boundary="plist",
    )

    case(
        "truncated-file-mid-plist.dmg",
        body=image.raw[:len(fork) + len(image.gap) + (len(xml) // 2)],
        mutation="The whole file is truncated part-way through the XML property list, "
                 "so the koly trailer is gone with it.",
        expect=["UnsupportedFormat"],
        why="Without the last 512 bytes there is no koly, and a file with no koly is "
            "not a UDIF image at all - that is a format answer, not a corruption one.",
        boundary="plist",
    )

    def cut_mish(entries: list[dict]) -> None:
        entries[region]["Data"] = entries[region]["Data"][:MISH_CHUNK_TABLE - 12]

    case(
        "truncated-mid-mish.dmg",
        body=assemble(image, xml=patched_plist(image, cut_mish)),
        mutation=f"blkx region {region}'s mish block is cut short inside its own header, "
                 f"before the {MISH_CHUNK_TABLE}-byte chunk table starts.",
        expect=["CorruptImage"],
        why="A block map shorter than a mish header cannot be parsed; the reader must "
            "say so instead of reading past the end of the buffer.",
        boundary="mish",
    )

    def cut_chunk_table(entries: list[dict]) -> None:
        data = entries[region]["Data"]
        count = struct.unpack_from(">I", data, MISH_CHUNK_COUNT)[0]
        # Keep the declared count, throw away the second half of the table.
        keep = max(1, count // 2)
        entries[region]["Data"] = data[:chunk_offset(keep)]

    case(
        "truncated-mid-chunk-table.dmg",
        body=assemble(image, xml=patched_plist(image, cut_chunk_table)),
        mutation=f"blkx region {region} keeps its declared chunk count but only carries "
                 "half the chunk descriptors.",
        expect=["CorruptImage"],
        why="The declared count must be bounded by the bytes actually present before "
            "anything is allocated or indexed off it.",
        boundary="chunk-table",
    )

    case(
        "truncated-mid-data-fork.dmg",
        body=assemble(image, data_fork=fork[:len(fork) // 2]),
        mutation="The data fork is cut in half and the plist and koly are re-laid after "
                 "it, so every structure parses but the compressed bytes the last chunks "
                 "point at are simply not there.",
        expect=["CorruptImage"],
        why="A chunk whose payload runs off the end of the file must be a named failure, "
            "not an EndOfStreamException escaping the reader.",
        boundary="data-fork",
    )

    case(
        "truncated-to-511-bytes.dmg",
        body=image.raw[:KOLY_SIZE - 1],
        mutation="The file is one byte shorter than a koly trailer.",
        expect=["UnsupportedFormat"],
        why="The smallest possible UDIF image is 512 bytes; anything smaller has to be "
            "refused before a 512-byte read is attempted at a negative offset.",
        boundary="koly",
    )

    case(
        "empty.dmg",
        body=b"",
        mutation="A zero-byte file.",
        expect=["UnsupportedFormat"],
        why="Nothing at all is still an input the reader is handed, and it must answer "
            "rather than divide by the size of something.",
        boundary="koly",
    )

    # ------------------------------------------------------------ chunk extents

    def offset_past_eof(entries: list[dict]) -> None:
        data = entries[region]["Data"]
        entries[region]["Data"] = patch_u64(
            data, chunk_offset(0) + CHUNK_COMPRESSED_OFFSET, len(image.raw) + (1 << 30))

    case(
        "compressed-offset-past-eof.dmg",
        body=assemble(image, xml=patched_plist(image, offset_past_eof)),
        mutation=f"The first stored chunk of region {region} claims its compressed bytes "
                 "start a gigabyte past the end of the file.",
        expect=["CorruptImage"],
        why="A seek driven by an untrusted offset must be bounded by the file, not "
            "attempted and left to throw.",
        boundary="chunk",
    )

    def offset_overflows(entries: list[dict]) -> None:
        data = entries[region]["Data"]
        entries[region]["Data"] = patch_u64(
            data, chunk_offset(0) + CHUNK_COMPRESSED_OFFSET, U64_MAX - 8)

    case(
        "compressed-offset-overflows.dmg",
        body=assemble(image, xml=patched_plist(image, offset_overflows)),
        mutation="The first stored chunk's CompressedOffset is within eight bytes of "
                 "2^64, so offset + length wraps and DataForkOffset + offset wraps again.",
        expect=["CorruptImage"],
        why="Wrap-around must become a refusal. An unchecked wrap here yields a small "
            "positive offset that looks legitimate and reads the wrong bytes.",
        boundary="chunk",
    )

    def length_max(entries: list[dict]) -> None:
        data = entries[region]["Data"]
        entries[region]["Data"] = patch_u64(
            data, chunk_offset(0) + CHUNK_COMPRESSED_LENGTH, U64_MAX)

    case(
        "compressed-length-max.dmg",
        body=assemble(image, xml=patched_plist(image, length_max)),
        mutation="The first stored chunk declares a CompressedLength of "
                 "0xFFFFFFFFFFFFFFFF.",
        expect=["CorruptImage"],
        why="The classic 'allocate what the file says' bug. 16 exbibytes must be refused "
            "on the declaration, before any buffer is sized from it.",
        boundary="chunk",
    )

    def chunk_sectors_overflow(entries: list[dict]) -> None:
        data = entries[region]["Data"]
        entries[region]["Data"] = patch_u64(
            data, chunk_offset(0) + CHUNK_SECTOR_COUNT, SECTORS_THAT_OVERFLOW_X512)

    case(
        "chunk-sector-count-overflows-x512.dmg",
        body=assemble(image, xml=patched_plist(image, chunk_sectors_overflow)),
        mutation=f"The first chunk of region {region} declares "
                 f"{SECTORS_THAT_OVERFLOW_X512} sectors, which overflows a 64-bit byte "
                 "count when multiplied by 512.",
        expect=["CorruptImage"],
        why="sectors * 512 is the single most common overflow in a UDIF reader; it must "
            "be checked arithmetic that fails, not a wrap that produces a plausible size.",
        boundary="chunk",
    )

    def mish_sectors_overflow(entries: list[dict]) -> None:
        data = entries[region]["Data"]
        entries[region]["Data"] = patch_u64(
            data, MISH_SECTOR_COUNT, SECTORS_THAT_OVERFLOW_X512)

    case(
        "mish-sector-count-overflows-x512.dmg",
        body=assemble(image, xml=patched_plist(image, mish_sectors_overflow)),
        mutation=f"Region {region}'s mish header declares "
                 f"{SECTORS_THAT_OVERFLOW_X512} sectors.",
        expect=["CorruptImage"],
        why="Same overflow one level up, where the product is what a whole-region buffer "
            "would be sized from.",
        boundary="mish",
    )

    case(
        "koly-sector-count-overflows-x512.dmg",
        body=assemble(image, koly_overrides={"SectorCount": SECTORS_THAT_OVERFLOW_X512}),
        mutation=f"koly.SectorCount is {SECTORS_THAT_OVERFLOW_X512}.",
        expect=["CorruptImage"],
        why="Same overflow at the top, where it decides how big the decoded disk is "
            "claimed to be.",
        boundary="koly",
    )

    case(
        "koly-sector-count-max.dmg",
        body=assemble(image, koly_overrides={"SectorCount": U64_MAX}),
        mutation="koly.SectorCount is 0xFFFFFFFFFFFFFFFF - an 8 zebibyte disk.",
        expect=["CorruptImage"],
        why="The saturated value, which is what a naive cast to a signed type turns "
            "into -1.",
        boundary="koly",
    )

    def overlap(entries: list[dict]) -> None:
        data = entries[region]["Data"]
        first_start = struct.unpack_from(
            ">Q", data, chunk_offset(0) + CHUNK_SECTOR_NUMBER)[0]
        entries[region]["Data"] = patch_u64(
            data, chunk_offset(1) + CHUNK_SECTOR_NUMBER, first_start)

    case(
        "overlapping-extents.dmg",
        body=assemble(image, xml=patched_plist(image, overlap)),
        mutation=f"The second chunk of region {region} is moved back on top of the first, "
                 "so two extents claim the same sectors.",
        expect=["CorruptImage"],
        why="Two chunks over one sector means the disk has no single defined content. "
            "Silently letting the later one win would make the decode "
            "implementation-defined.",
        boundary="extent-map",
    )

    def gap(entries: list[dict]) -> None:
        data = entries[region]["Data"]
        count = struct.unpack_from(">Q", data, chunk_offset(0) + CHUNK_SECTOR_COUNT)[0]
        if count < 2:
            raise SystemExit("first chunk is too short to open a gap in")
        entries[region]["Data"] = patch_u64(
            data, chunk_offset(0) + CHUNK_SECTOR_COUNT, count - 1)

    case(
        "gap-between-extents.dmg",
        body=assemble(image, xml=patched_plist(image, gap)),
        mutation=f"The first chunk of region {region} loses its last sector, leaving one "
                 "sector of the disk described by nothing.",
        expect=["CorruptImage"],
        why="An undescribed sector has no defined content; returning zeros for it would "
            "be inventing data the image never carried.",
        boundary="extent-map",
    )

    def huge_chunk_count(entries: list[dict]) -> None:
        data = entries[region]["Data"]
        entries[region]["Data"] = patch_u32(data, MISH_CHUNK_COUNT, U32_MAX)

    case(
        "chunk-count-exhausts-memory.dmg",
        body=assemble(image, xml=patched_plist(image, huge_chunk_count)),
        mutation=f"Region {region} declares {U32_MAX} chunks in a block map a few hundred "
                 "bytes long. Sized naively that table is 160 GB.",
        expect=["CorruptImage"],
        why="The declared count must be validated against the bytes present BEFORE a list "
            "is reserved for it. This is the case that turns a malformed image into an "
            "out-of-memory kill.",
        boundary="mish",
    )

    def bad_signature(entries: list[dict]) -> None:
        data = entries[region]["Data"]
        entries[region]["Data"] = patch_u32(data, MISH_SIGNATURE, 0x6D697367)  # 'misg'

    case(
        "mish-bad-signature.dmg",
        body=assemble(image, xml=patched_plist(image, bad_signature)),
        mutation=f"Region {region}'s block map starts with 'misg' instead of 'mish'.",
        expect=["CorruptImage"],
        why="The magic is the only thing that says the base64 blob is a block map at all.",
        boundary="mish",
    )

    def bad_mish_version(entries: list[dict]) -> None:
        data = entries[region]["Data"]
        entries[region]["Data"] = patch_u32(data, 0x04, 99)

    case(
        "mish-unknown-version.dmg",
        body=assemble(image, xml=patched_plist(image, bad_mish_version)),
        mutation=f"Region {region}'s mish block claims version 99.",
        expect=["UnsupportedFormat"],
        why="A version we do not implement is a format answer, distinct from corruption - "
            "and the distinction is what tells a user to upgrade rather than re-download.",
        boundary="mish",
    )

    # ------------------------------------------------------------------- koly

    case(
        "koly-xml-offset-outside-file.dmg",
        body=assemble(image, koly_overrides={"XmlOffset": U64_MAX - 4096},
                      fix_lengths=True),
        mutation="koly.XMLOffset points 4 KB short of 2^64, far outside the file.",
        expect=["CorruptImage"],
        why="offset + length must be range-checked against the real file size without "
            "wrapping first.",
        boundary="koly",
    )

    case(
        "koly-xml-length-max.dmg",
        body=assemble(image, koly_overrides={"XmlLength": U64_MAX}),
        mutation="koly.XMLLength is 0xFFFFFFFFFFFFFFFF.",
        expect=["CorruptImage"],
        why="A 16 exbibyte property list must be refused on the declaration; allocating "
            "for it is the whole attack.",
        boundary="koly",
    )

    case(
        "koly-xml-length-zero.dmg",
        body=assemble(image, koly_overrides={"XmlLength": 0}),
        mutation="koly.XMLLength is zero, as a resource-fork-only image would have it.",
        expect=["UnsupportedFormat"],
        why="No plist means no block map this build can read; that is a format we do not "
            "support, not damage.",
        boundary="koly",
    )

    case(
        "koly-data-fork-outside-file.dmg",
        body=assemble(image, koly_overrides={"DataForkLength": U64_MAX}),
        mutation="koly.DataForkLength is 0xFFFFFFFFFFFFFFFF.",
        expect=["CorruptImage"],
        why="The data fork has to be inside the file before anything reads from it.",
        boundary="koly",
    )

    case(
        "koly-unknown-version.dmg",
        body=assemble(image, koly_overrides={"Version": 5}),
        mutation="koly.Version is 5; this build reads version 4.",
        expect=["UnsupportedFormat"],
        why="An unknown container version must be named as such rather than parsed "
            "hopefully with version-4 offsets.",
        boundary="koly",
    )

    case(
        "koly-header-size-wrong.dmg",
        body=assemble(image, koly_overrides={"HeaderSize": 0x7FFFFFFF}),
        mutation="koly.HeaderSize claims 2 GB for a structure that is 512 bytes.",
        expect=["CorruptImage"],
        why="A self-describing size that disagrees with the format is corruption, and it "
            "must not be believed by anything that slices on it.",
        boundary="koly",
    )

    # -------------------------------------------------------------- the plist

    lolz = b"""<?xml version="1.0" encoding="UTF-8"?>
<!DOCTYPE plist [
  <!ENTITY lol "lol">
  <!ENTITY lol1 "&lol;&lol;&lol;&lol;&lol;&lol;&lol;&lol;&lol;&lol;">
  <!ENTITY lol2 "&lol1;&lol1;&lol1;&lol1;&lol1;&lol1;&lol1;&lol1;&lol1;&lol1;">
  <!ENTITY lol3 "&lol2;&lol2;&lol2;&lol2;&lol2;&lol2;&lol2;&lol2;&lol2;&lol2;">
  <!ENTITY lol4 "&lol3;&lol3;&lol3;&lol3;&lol3;&lol3;&lol3;&lol3;&lol3;&lol3;">
  <!ENTITY lol5 "&lol4;&lol4;&lol4;&lol4;&lol4;&lol4;&lol4;&lol4;&lol4;&lol4;">
  <!ENTITY lol6 "&lol5;&lol5;&lol5;&lol5;&lol5;&lol5;&lol5;&lol5;&lol5;&lol5;">
  <!ENTITY lol7 "&lol6;&lol6;&lol6;&lol6;&lol6;&lol6;&lol6;&lol6;&lol6;&lol6;">
  <!ENTITY lol8 "&lol7;&lol7;&lol7;&lol7;&lol7;&lol7;&lol7;&lol7;&lol7;&lol7;">
  <!ENTITY lol9 "&lol8;&lol8;&lol8;&lol8;&lol8;&lol8;&lol8;&lol8;&lol8;&lol8;">
]>
<plist version="1.0">
<dict>
  <key>resource-fork</key>
  <dict>
    <key>blkx</key>
    <array>
      <dict>
        <key>Name</key>
        <string>&lol9;</string>
        <key>Data</key>
        <data></data>
      </dict>
    </array>
  </dict>
</dict>
</plist>
"""

    case(
        "plist-billion-laughs.dmg",
        body=assemble(image, xml=lolz),
        mutation="The property list is replaced by a billion-laughs bomb: nine levels of "
                 "nested entity definitions in an internal DTD subset, expanding to "
                 "10^9 characters from under a kilobyte of input.",
        expect=["UnsupportedFormat"],
        why="This must fail CLOSED on the doctype, before any expansion is attempted. "
            "The plist reader strips hdiutil's harmless '<!DOCTYPE plist PUBLIC ...>' "
            "from the prolog so DtdProcessing.Prohibit does not reject a legitimate "
            "image, and that strip is exactly the hole a bomb would come through - so "
            "any doctype carrying an internal '[...]' subset is refused outright.",
        boundary="plist",
    )

    quadratic = (b'<?xml version="1.0" encoding="UTF-8"?>\n'
                 b'<!DOCTYPE plist [ <!ENTITY a "'
                 + (b"A" * 60000)
                 + b'"> ]>\n<plist version="1.0"><dict><key>resource-fork</key>'
                 + b"<dict><key>blkx</key><array><dict><key>Name</key><string>"
                 + (b"&a;" * 20000)
                 + b"</string></dict></array></dict></dict></plist>\n")

    case(
        "plist-quadratic-blowup.dmg",
        body=assemble(image, xml=quadratic),
        mutation="A quadratic-blowup plist: one 60 KB entity referenced 20,000 times, "
                 "for 1.2 GB of expansion with no nesting for a depth limit to catch.",
        expect=["UnsupportedFormat"],
        why="Nesting limits do not help here; only refusing the internal subset does. "
            "The companion to the billion-laughs case, proving the refusal is on the "
            "declaration and not on the shape of the expansion.",
        boundary="plist",
    )

    case(
        "plist-external-entity.dmg",
        body=assemble(
            image,
            xml=b'<?xml version="1.0" encoding="UTF-8"?>\n'
                b'<!DOCTYPE plist SYSTEM "http://127.0.0.1:1/steal.dtd">\n'
                b'<plist version="1.0"><dict><key>resource-fork</key><dict>'
                b"<key>blkx</key><array/></dict></dict></plist>\n"),
        mutation="A doctype with an external SYSTEM identifier pointing at a URL.",
        expect=["CorruptImage", "UnsupportedFormat"],
        why="The reader must never fetch it. A blocking HTTP request to a dead port is "
            "also how this case would show up as a hang rather than a wrong answer.",
        boundary="plist",
    )

    deep = (b'<?xml version="1.0" encoding="UTF-8"?>\n<plist version="1.0">'
            + (b"<array>" * 5000) + (b"</array>" * 5000) + b"</plist>\n")

    case(
        "plist-deeply-nested.dmg",
        body=assemble(image, xml=deep),
        mutation="5,000 nested <array> elements.",
        expect=["CorruptImage"],
        why="Depth must be bounded explicitly. A recursive-descent parser would blow the "
            "stack here, and a StackOverflowException cannot be caught - the process "
            "simply dies with no exit code worth the name.",
        boundary="plist",
    )

    case(
        "plist-not-xml.dmg",
        body=assemble(image, xml=b"\xff" * 4096),
        mutation="The property list is 4 KB of 0xFF, which is not even valid UTF-8.",
        expect=["CorruptImage"],
        why="Undecodable bytes must be a named failure, not a decoder exception.",
        boundary="plist",
    )

    case(
        "plist-root-is-array.dmg",
        body=assemble(
            image,
            xml=b'<?xml version="1.0" encoding="UTF-8"?>\n'
                b'<plist version="1.0"><array><string>no dictionary here</string>'
                b"</array></plist>\n"),
        mutation="A well-formed plist whose root is an array rather than a dictionary.",
        expect=["CorruptImage"],
        why="Valid XML is not a valid block map; the shape has to be checked too.",
        boundary="plist",
    )

    def empty_blkx(entries: list[dict]) -> None:
        del entries[:]

    case(
        "blkx-empty.dmg",
        body=assemble(image, xml=patched_plist(image, empty_blkx)),
        mutation="resource-fork/blkx is an empty array.",
        expect=["CorruptImage"],
        why="An image that describes no regions describes no disk.",
        boundary="plist",
    )

    def bad_base64(entries: list[dict]) -> None:
        entries[region]["Data"] = b""

    bad_b64_xml = patched_plist(image, bad_base64).replace(
        b"<data>\n</data>", b"<data>not base64 at all !!!!</data>", 1)
    if b"not base64" not in bad_b64_xml:
        bad_b64_xml = patched_plist(image, bad_base64).replace(
            b"<data></data>", b"<data>not base64 at all !!!!</data>", 1)

    case(
        "blkx-data-not-base64.dmg",
        body=assemble(image, xml=bad_b64_xml),
        mutation="A blkx Data element holds characters that are not base64.",
        expect=["CorruptImage"],
        why="Decoding untrusted base64 must be a Try, not a throw.",
        boundary="plist",
    )

    def many_entries(entries: list[dict]) -> None:
        template = entries[0]
        del entries[:]
        # One more than BlkxReader.MaxEntries.
        for index in range(65537):
            entries.append({"Attributes": "0x0050", "ID": str(index),
                            "Name": f"r{index}", "Data": template["Data"][:8]})

    case(
        "blkx-too-many-entries.dmg",
        body=assemble(image, xml=patched_plist(image, many_entries)),
        mutation="65,537 blkx regions, one past the reader's ceiling, each a stub.",
        expect=["CorruptImage"],
        why="The number of regions is attacker-controlled and every one of them costs a "
            "parse; the ceiling has to be enforced before the loop, not after it.",
        max_mib=256,
        why_max="The plist itself is a few megabytes of XML, and reading it into a UTF-16 "
                "string legitimately doubles that before a single region is looked at.",
        boundary="plist",
    )

    oversized = 17 * 1024 * 1024  # one mebibyte past BlkxReader.DefaultMaxEntryBytes

    def oversized_data(entries: list[dict]) -> None:
        del entries[1:]
        entries[0]["Data"] = b"\x00" * oversized

    case(
        "blkx-data-oversized.dmg",
        body=assemble(image, xml=patched_plist(image, oversized_data)),
        mutation=f"A single blkx Data payload of {oversized // (1024 * 1024)} MiB, one "
                 "mebibyte past the reader's per-region ceiling.",
        expect=["CorruptImage"],
        why="The base64 length must be measured and refused before the decode buffer is "
            "allocated - the point where 'as big as the file says' becomes an "
            "out-of-memory.",
        max_mib=384,
        why_max="A 23 MiB base64 blob inside the plist costs that much again as a UTF-16 "
                "string and once more when the doctype is stripped; the ceiling is set "
                "well above the honest cost and far below a runaway decode.",
        budget_ms=20000,
        boundary="plist",
    )

    # --------------------------------------------------------------- payloads

    case(
        "zlib-payload-garbage.dmg",
        body=assemble(image, data_fork=b"\xff" * len(fork), fix_lengths=False),
        mutation="Every byte of the data fork is 0xFF, so the zlib streams the chunk "
                 "table points at are not zlib streams.",
        expect=["CorruptImage"],
        why="A codec fed nonsense must fail as corruption, not as an exception out of the "
            "decompressor.",
        boundary="codec",
    )

    def raw_length_mismatch(entries: list[dict]) -> None:
        data = entries[region]["Data"]
        # Turn a zlib chunk into a raw one without changing its byte count, so
        # the raw decoder is handed a payload that does not match its sectors.
        data = patch_u32(data, chunk_offset(0) + CHUNK_TYPE, 0x00000001)
        entries[region]["Data"] = data

    case(
        "raw-chunk-length-mismatch.dmg",
        body=assemble(image, xml=patched_plist(image, raw_length_mismatch)),
        mutation=f"The first chunk of region {region} is relabelled raw (0x00000001) "
                 "while keeping its compressed byte count, so its payload is far shorter "
                 "than the sectors it claims.",
        expect=["CorruptImage"],
        why="A raw chunk is a straight copy, so a length disagreement is the only thing "
            "standing between the image and a buffer the wrong size.",
        boundary="codec",
    )

    def unknown_codec(entries: list[dict]) -> None:
        data = entries[region]["Data"]
        entries[region]["Data"] = patch_u32(
            data, chunk_offset(0) + CHUNK_TYPE, 0x80000042)

    case(
        "unknown-chunk-codec.dmg",
        body=assemble(image, xml=patched_plist(image, unknown_codec)),
        mutation="A chunk declares entry type 0x80000042, which is not in the UDIF format.",
        expect=["UnsupportedFormat"],
        why="An unknown codec has to be named and refused; guessing at it is how a reader "
            "starts producing plausible wrong data.",
        boundary="codec",
    )


# --------------------------------------------------------------------------
# main
# --------------------------------------------------------------------------


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__,
                                     formatter_class=argparse.RawDescriptionHelpFormatter)
    parser.add_argument("--source", default=None,
                        help="the good fixture to mutate (default: "
                             "fixtures/generated/exfat-zlib.dmg)")
    parser.add_argument("--out", default=None,
                        help="output directory (default: fixtures/hostile)")
    args = parser.parse_args()

    repo = Path(__file__).resolve().parent.parent
    out = Path(args.out) if args.out else repo / "fixtures" / "hostile"

    if args.source:
        candidates = [Path(args.source)]
    else:
        generated = repo / "fixtures" / "generated"
        candidates = [generated / "exfat-zlib.dmg", generated / "exfat-sparse.dmg",
                      generated / "adc.dmg"]

    source = next((path for path in candidates if path.is_file()), None)
    if source is None:
        print("make-hostile-corpus: no source fixture found. Run tools/make-fixtures.sh "
              "first; the hostile corpus is built by mutating a real hdiutil image, not "
              "by inventing one.", file=sys.stderr)
        return 1

    raw = source.read_bytes()
    image = Image(raw)
    build_cases(image)

    out.mkdir(parents=True, exist_ok=True)
    # Clear out anything from a previous run so a renamed case cannot linger.
    for stale in out.glob("*.dmg"):
        stale.unlink()

    records = []
    print(f"make-hostile-corpus: source -> {source}")
    print(f"make-hostile-corpus: output -> {out}")
    for entry in CASES:
        body = entry.pop("bytes")
        path = out / entry["name"]
        path.write_bytes(body)
        record = {
            "name": entry["name"],
            "boundary": entry["boundary"],
            "size": len(body),
            "sha256": hashlib.sha256(body).hexdigest(),
            "mutation": entry["mutation"],
            "expect_exit_codes": entry["expect"],
            "why": entry["why"],
            "budget_ms": entry["budget_ms"],
            "max_managed_mib": entry["max_managed_mib"],
        }
        if entry["why_max"]:
            record["why_max_managed_mib"] = entry["why_max"]
        records.append(record)
        print(f"  ok      {entry['name']:<42} {len(body):>9,} bytes  "
              f"-> {'|'.join(entry['expect'])}")

    manifest = {
        "_comment": "The hostile-input corpus for S2.9 (issue #25). Every image here is a "
                    "deliberate mutation of a real hdiutil-produced fixture. The contract "
                    "for all of them is the same: the reader returns a clean non-zero exit "
                    "with one of expect_exit_codes and a useful message - no unhandled "
                    "exception, no hang (budget_ms), no allocation spike "
                    "(max_managed_mib). Generated by tools/make-hostile-corpus.py; do not "
                    "hand-edit.",
        "_staleness": "The source fixture is regenerated by tools/make-fixtures.sh and "
                      "hdiutil bakes fresh UUIDs into it every time, so these hashes "
                      "change on every regeneration. They are here to tell a stale corpus "
                      "from a reader regression, not to be pinned in a test.",
        "schema_version": 1,
        "generator": "tools/make-hostile-corpus.py",
        "source": {
            "name": source.name,
            "size": len(raw),
            "sha256": hashlib.sha256(raw).hexdigest(),
        },
        "count": len(records),
        "cases": records,
    }
    (out / "cases.json").write_text(json.dumps(manifest, indent=2) + "\n")

    print()
    print("================ make-hostile-corpus summary ================")
    print(f"cases:  {len(records)} -> {out}")
    print(f"index:  {out / 'cases.json'}")
    print(f"bytes:  {sum(record['size'] for record in records):,}")
    print("=============================================================")
    return 0


if __name__ == "__main__":
    sys.exit(main())
