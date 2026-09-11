#!/bin/bash
#
# make-fixtures.sh — S1.4 (issue #14)
#
# Generates the DMG test-corpus into fixtures/generated/ (gitignored) using
# Apple's own hdiutil, so every fixture is a genuine article rather than
# something we synthesised. Eleven fixtures are attempted; see the table in
# fixtures/README.md for what each one exercises.
#
# PASSPHRASE for every encrypted fixture: dmg-test-passphrase
#   It is supplied to hdiutil ONLY on stdin (with a trailing newline) via
#   -stdinpass. It is never passed as an argument, so it never lands in the
#   process table.
#
# SAFETY — read before editing:
#   * Every hdiutil/diskutil invocation goes through hd()/hdp()/dk(), which
#     (a) always supply stdin explicitly and (b) impose a hard SIGALRM timeout.
#     A bare hdiutil call that runs out of stdin will escalate to a GUI
#     passphrase dialog on the user's screen. Never call hdiutil directly.
#   * -stdinpass reads the passphrase up to a NEWLINE. Always printf '%s\n'.
#   * -stdinpass on `hdiutil convert` sets the passphrase of the OUTPUT image.
#     It CANNOT be used to read an encrypted source. To read an encrypted
#     image, attach it with -stdinpass and read the raw device.
#   * We detach only devices this run attached (tracked in $ATTACHED). The user
#     may have unrelated images mounted; a broad or looping detach is
#     destructive and is forbidden.
#
# Usage: tools/make-fixtures.sh          # rebuild everything (idempotent)
#
set -u

PASS="dmg-test-passphrase"
TMO=300                                   # hard per-hdiutil-call timeout, seconds

SCRIPT_DIR=$(cd -- "$(dirname -- "$0")" && pwd)
REPO_ROOT=$(cd -- "$SCRIPT_DIR/.." && pwd)
OUT="$REPO_ROOT/fixtures/generated"
WORK="$OUT/.work"
STAGE="$WORK/stage"

mkdir -p "$OUT" "$WORK" "$STAGE" || exit 1

# ---------------------------------------------------------------- bookkeeping

PRODUCED=""
SKIPPED=""

ok()   { PRODUCED="$PRODUCED$1
"; echo "  ok      $1"; }
skip() { SKIPPED="$SKIPPED$1: $2
"; echo "  SKIP    $1 — $2" >&2; }

# ------------------------------------------------------- guarded external cmds

# hd — hdiutil with no passphrase. stdin is /dev/null so it can never block on
# a prompt; SIGALRM caps the runtime. (A pending alarm survives exec(2).)
hd() { perl -e 'alarm shift; exec @ARGV' "$TMO" hdiutil "$@" < /dev/null; }

# hdp — hdiutil with the passphrase on stdin, newline-terminated.
hdp() { printf '%s\n' "$PASS" | perl -e 'alarm shift; exec @ARGV' "$TMO" hdiutil "$@"; }

# dk — diskutil, same guarantees.
dk() { perl -e 'alarm shift; exec @ARGV' "$TMO" diskutil "$@" < /dev/null; }

# --------------------------------------------------------- attach bookkeeping

ATTACHED=""

track()   { ATTACHED="$ATTACHED$1
"; }
untrack() { ATTACHED=$(printf '%s\n' "$ATTACHED" | grep -vx -- "$1"); }

# detach — detach one device WE attached, then forget it.
detach() {
    local d="$1"
    [ -n "$d" ] || return 0
    hd detach "$d" >/dev/null 2>&1 || hd detach -force "$d" >/dev/null 2>&1
    untrack "$d"
}

# cleanup — trap handler. Detaches only tracked devices, nothing else.
cleanup() {
    local d
    while IFS= read -r d; do
        [ -n "$d" ] || continue
        hd detach -force "$d" >/dev/null 2>&1
    done <<EOF
$ATTACHED
EOF
    ATTACHED=""
}
trap cleanup EXIT INT TERM

# dev_of — the whole-disk /dev/diskN from hdiutil attach's output. NOTE: attach
# interleaves progress lines ("Checksumming ...") with the device table, so we
# must match /dev/disk explicitly rather than taking the first line.
dev_of() { printf '%s\n' "$1" | grep -Eo '^/dev/disk[0-9]+' | head -1; }

# --------------------------------------------------------------- stage content
#
# Known files with known content, so later tests can assert on them.

build_stage() {
    rm -rf "$STAGE" && mkdir -p "$STAGE" || return 1
    printf 'Hello, DMG fixture!\n'                   > "$STAGE/HELLO.TXT"
    printf 'Windows_DMG test corpus\nfixture v1\n'   > "$STAGE/README.TXT"
    # 64 KiB of deterministic big-endian counter bytes.
    perl -e 'print pack("N", $_) for 0..16383'       > "$STAGE/DATA.BIN"
    [ -s "$STAGE/DATA.BIN" ]
}

# populate — attach a read/write image, copy the stage files onto every volume
# it mounts, detach. $1 = image path.
populate() {
    local img="$1" out dev mnt rc=0
    out=$(hd attach -nobrowse -owners off "$img") || return 1
    dev=$(dev_of "$out")
    [ -n "$dev" ] || return 1
    track "$dev"
    while IFS= read -r mnt; do
        [ -n "$mnt" ] || continue
        cp "$STAGE"/HELLO.TXT "$STAGE"/README.TXT "$STAGE"/DATA.BIN "$mnt"/ || rc=1
    done <<EOF
$(printf '%s\n' "$out" | grep -o '/Volumes/.*')
EOF
    sync
    detach "$dev"
    return $rc
}

# ------------------------------------------------------------------ primitives

# create_fs_image — a populated read/write image. $1 out, $2 size, $3 fs, $4 volname
create_fs_image() {
    local img="$1" size="$2" fs="$3" vol="$4"
    rm -f "$img"
    hd create -size "$size" -fs "$fs" -volname "$vol" -ov "$img" >/dev/null || return 1
    populate "$img"
}

# conv — hdiutil convert into fixtures/generated. $1 name, $2 src, rest: args
conv() {
    local name="$1" src="$2"; shift 2
    rm -f "$OUT/$name"
    if hd convert "$src" "$@" -o "$OUT/$name" >/dev/null 2>&1; then
        ok "$name"
    else
        skip "$name" "hdiutil convert $* failed"
    fi
}

# conv_enc — as conv, but the OUTPUT is encrypted with $PASS. $4 (default UDRW)
# is the destination format: UDRW yields a flat encrypted stream with no koly
# trailer, UDZO yields a genuine encrypted+compressed UDIF container.
# (-stdinpass here encrypts the output; it does NOT decrypt the source.)
conv_enc() {
    local name="$1" src="$2" cipher="$3" format="${4:-UDRW}"
    rm -f "$OUT/$name"
    if hdp convert "$src" -format "$format" -encryption "$cipher" -stdinpass \
            -o "$OUT/$name" >/dev/null 2>&1; then
        if verify_enc "$OUT/$name"; then
            ok "$name"
        else
            rm -f "$OUT/$name"
            skip "$name" "created but passphrase did not round-trip"
        fi
    else
        skip "$name" "hdiutil convert -format $format -encryption $cipher failed"
    fi
}

# verify_enc — prove the passphrase actually opens the image we just wrote.
# Attaches -nomount -readonly (no filesystem is touched) and detaches again.
verify_enc() {
    local img="$1" out dev
    out=$(hdp attach -stdinpass -nomount -readonly "$img" 2>/dev/null) || return 1
    dev=$(dev_of "$out")
    [ -n "$dev" ] || return 1
    track "$dev"
    detach "$dev"
    return 0
}

# =============================================================================
# Build
# =============================================================================

echo "make-fixtures: output -> $OUT"
build_stage || { echo "cannot build stage content" >&2; exit 1; }

# --- the shared exFAT source image -------------------------------------------
BASE="$WORK/base-exfat.dmg"
BASE_OK=0
if create_fs_image "$BASE" 12m exFAT DMGFIX; then
    BASE_OK=1
else
    echo "FATAL-ish: could not build the exFAT base image" >&2
fi

REASON_NOBASE="exFAT base image could not be created"

# 1. exfat-raw.dmg — UDRW. NOT a UDIF container: hdiutil's "read/write" output is
#    the flat sector image with no koly trailer, no property list and no chunk
#    table, so despite the name it exercises no chunk codec at all. It is kept
#    because a flat image is its own ground truth and the reader has to refuse to
#    find a container in it. exfat-udro.dmg below is the fixture that actually
#    carries raw chunks.
if [ $BASE_OK -eq 1 ]; then conv exfat-raw.dmg "$BASE" -format UDRW
else skip exfat-raw.dmg "$REASON_NOBASE"; fi

# 2. exfat-zlib.dmg — UDZO, the main case
if [ $BASE_OK -eq 1 ]; then conv exfat-zlib.dmg "$BASE" -format UDZO
else skip exfat-zlib.dmg "$REASON_NOBASE"; fi

# 2b. exfat-udro.dmg — UDRO, and the only convert recipe that puts genuine raw
#     (0x00000001) chunks in a UDIF container. UDRO is a read-only UDIF whose
#     chunks are stored uncompressed, so it is a real koly + plist + blkx image
#     whose data chunks are all type 1. Because it converts from the same $BASE
#     as exfat-zlib/adc/bzip2/enc*, it decodes to the same sector stream as they
#     do, which is what lets the conformance suite compare the raw decoder
#     against the zlib and ADC decoders on identical bytes.
#
#     Also tried, and rejected: `convert -format UDZO -imagekey zlib-level=0`
#     does emit raw chunks (64 of them on a 64 MiB source), but it produces them
#     as a side effect of asking zlib for no compression, which is a far more
#     fragile thing to depend on than a documented format name.
if [ $BASE_OK -eq 1 ]; then conv exfat-udro.dmg "$BASE" -format UDRO
else skip exfat-udro.dmg "$REASON_NOBASE"; fi

# 3. exfat-sparse.dmg — a mostly empty exFAT volume, UDZO, so most of the image
#    is zero-fill / ignore chunks rather than data.
SPARSE="$WORK/base-sparse.dmg"
rm -f "$SPARSE"
if hd create -size 48m -fs exFAT -volname DMGSPARSE -ov "$SPARSE" >/dev/null; then
    SOUT=$(hd attach -nobrowse -owners off "$SPARSE")
    SDEV=$(dev_of "$SOUT")
    if [ -n "$SDEV" ]; then
        track "$SDEV"
        SMNT=$(printf '%s\n' "$SOUT" | grep -o '/Volumes/.*' | head -1)
        [ -n "$SMNT" ] && cp "$STAGE/HELLO.TXT" "$SMNT"/
        sync
        detach "$SDEV"
        conv exfat-sparse.dmg "$SPARSE" -format UDZO
    else
        skip exfat-sparse.dmg "could not attach the sparse base image"
    fi
else
    skip exfat-sparse.dmg "hdiutil create -size 48m -fs exFAT failed"
fi

# 4/5. encrypted fixtures — encrcdsa v2, AES-256 and AES-128. Destination format
#      UDRW, so each decrypts to a flat sector stream with no koly trailer at
#      all - see exfat-enc-udzo.dmg below for the encrypted+compressed case that
#      actually carries one.
if [ $BASE_OK -eq 1 ]; then
    conv_enc exfat-enc256.dmg "$BASE" AES-256
    conv_enc exfat-enc128.dmg "$BASE" AES-128
else
    skip exfat-enc256.dmg "$REASON_NOBASE"
    skip exfat-enc128.dmg "$REASON_NOBASE"
fi

# 5b. exfat-enc-udzo.dmg — encrypted AND compressed (S4.10, issue #91). Unlike
#     exfat-enc256/128 above, the destination format here is UDZO, so the
#     decrypted payload is not a flat stream: it is a genuine koly + plist +
#     blkx UDIF container whose chunks are zlib-compressed. That is the
#     "decrypt -> find koly -> parse UDIF -> decode chunks" path docs/04 §1
#     describes, exercised here against a real hdiutil image for the first
#     time - see fixtures/README.md and EncryptedRoundTripTests.
if [ $BASE_OK -eq 1 ]; then
    conv_enc exfat-enc-udzo.dmg "$BASE" AES-256 UDZO
else
    skip exfat-enc-udzo.dmg "$REASON_NOBASE"
fi

# 6. fat32.dmg — FAT32 needs >= 65525 clusters, so it cannot be tiny; we build
#    it at 48 MiB and compress it down with UDZO.
F32="$WORK/base-fat32.dmg"
if create_fs_image "$F32" 48m "MS-DOS FAT32" DMGFAT32; then
    conv fat32.dmg "$F32" -format UDZO
else
    skip fat32.dmg "hdiutil create -fs 'MS-DOS FAT32' failed"
fi

# 7. hfsplus.dmg — negative case: HFS+ must be refused
HFS="$WORK/base-hfsplus.dmg"
if create_fs_image "$HFS" 12m "HFS+" DMGHFS; then
    conv hfsplus.dmg "$HFS" -format UDZO
else
    skip hfsplus.dmg "hdiutil create -fs HFS+ failed"
fi

# 8. apfs.dmg — negative case
APFS="$WORK/base-apfs.dmg"
if create_fs_image "$APFS" 32m APFS DMGAPFS; then
    conv apfs.dmg "$APFS" -format UDZO
else
    skip apfs.dmg "hdiutil create -fs APFS failed"
fi

# 9. bzip2.dmg — negative case: unsupported codec
if [ $BASE_OK -eq 1 ]; then conv bzip2.dmg "$BASE" -format UDBZ
else skip bzip2.dmg "$REASON_NOBASE"; fi

# 10. adc.dmg — Apple ADC decoder
if [ $BASE_OK -eq 1 ]; then conv adc.dmg "$BASE" -format UDCO
else skip adc.dmg "$REASON_NOBASE"; fi

# 11. multipart.dmg — more than one partition, for partition selection.
#     Built by partitioning a blank image with diskutil. MBR is used rather
#     than GPT because diskutil insists on a 200 MiB EFI partition under GPT,
#     which does not fit in an image we are willing to keep small.
MP="$WORK/base-multipart.dmg"
rm -f "$MP"
if hd create -size 80m -layout NONE -type UDIF -ov "$MP" >/dev/null; then
    MOUT=$(hd attach -nomount "$MP")
    MDEV=$(dev_of "$MOUT")
    if [ -n "$MDEV" ]; then
        track "$MDEV"
        if dk partitionDisk "$MDEV" MBR ExFAT DMGFIXP1 36M ExFAT DMGFIXP2 R >/dev/null 2>&1; then
            for m in /Volumes/DMGFIXP1 /Volumes/DMGFIXP2; do
                [ -d "$m" ] && cp "$STAGE"/HELLO.TXT "$STAGE"/README.TXT "$m"/ 2>/dev/null
            done
            sync
            detach "$MDEV"
            conv multipart.dmg "$MP" -format UDZO
        else
            detach "$MDEV"
            skip multipart.dmg "diskutil partitionDisk (MBR, 2 x ExFAT) failed"
        fi
    else
        skip multipart.dmg "could not attach the blank multipart base image"
    fi
else
    skip multipart.dmg "hdiutil create -layout NONE failed"
fi

# 12. zerofill.dmg — the only recipe found that makes hdiutil emit zero-fill
#     (0x00000000) chunks.
#
#     What was tried first, and what it produced (macOS 26.7, build 25G227):
#
#       convert <exFAT image>       -format UDZO   ->  zlib + ignore, no zero-fill
#       convert <exFAT image>       -format UDRO   ->  raw  + ignore, no zero-fill
#       convert <exFAT image>       -format UDCO   ->  ADC  + ignore, no zero-fill
#       convert <64 MiB of zeros>   -format UDZO   ->  64 zlib chunks, no zero-fill
#       convert <64 MiB of zeros>   -format UDRO   ->  1 raw chunk,    no zero-fill
#       convert <half-zero raw>     -format UDZO   ->  48 zlib chunks, no zero-fill
#       convert <.sparseimage>      -format UDZO   ->  zlib + ignore, no zero-fill
#       convert <.sparsebundle>     -format UDZO   ->  64 zlib chunks, no zero-fill
#       create  -type UDIF -fs exFAT               ->  flat image, no koly at all
#
#     `hdiutil convert` never emits zero-fill: it hands every chunk that holds
#     data to the codec, even a chunk that is entirely zeros, and marks only
#     filesystem free space as `ignore` (0x00000002). `hdiutil create -srcfolder`
#     takes a different path — it lays down a fresh volume and knows which of the
#     sectors it wrote are zeros — and that one does emit zero-fill:
#
#       create -srcfolder <dir> -fs exFAT -format UDZO -> zero-fill 9, zlib 3, ignore 2
#       create -srcfolder <dir> -fs exFAT -format UDRO -> zero-fill 2, raw 4, ignore 2
#
#     UDRO looks like the better of the two, because one fixture would then carry
#     zero-fill AND raw AND ignore. It is not usable: on macOS 26.7 (25G227)
#     `create -srcfolder -format UDRO` writes an image that hdiutil itself will
#     not read back — `hdiutil verify`, `hdiutil convert` and even
#     `hdiutil attach -noverify` all answer "corrupt image" — so there is no
#     ground truth to measure against, which is the whole point of a fixture.
#     (Our own reader parses it happily, which says nothing good about either
#     side.) UDZO from the same source converts and verifies normally, so that is
#     what is used; exfat-udro.dmg above supplies the raw chunks instead.
#
#     ZEROS.BIN is what puts a long run of zeros inside *allocated* space rather
#     than free space; without it hdiutil has far less to mark.
#
#     NOTE ON SAFETY: `create -srcfolder` attaches and detaches a volume
#     internally, which this script cannot put in $ATTACHED. It is still a single
#     hd() call, so it has explicit stdin and a hard timeout, and it cleans up
#     after itself — verified by comparing `hdiutil info` before and after. Do not
#     "fix" this by attaching the volume ourselves: -srcfolder is the whole reason
#     the zero-fill chunks exist.
ZSTAGE="$WORK/zstage"
rm -rf "$ZSTAGE"
if mkdir -p "$ZSTAGE" \
   && cp "$STAGE/HELLO.TXT" "$STAGE/README.TXT" "$STAGE/DATA.BIN" "$ZSTAGE"/ \
   && dd if=/dev/zero of="$ZSTAGE/ZEROS.BIN" bs=1m count=8 2>/dev/null; then
    rm -f "$OUT/zerofill.dmg"
    if hd create -srcfolder "$ZSTAGE" -fs exFAT -volname DMGZERO -format UDZO \
            -ov "$OUT/zerofill.dmg" >/dev/null 2>&1; then
        ok zerofill.dmg
    else
        skip zerofill.dmg "hdiutil create -srcfolder -fs exFAT -format UDZO failed"
    fi
else
    skip zerofill.dmg "could not stage the zero-run content"
fi

# =============================================================================

rm -rf "$WORK"

echo
echo "================ make-fixtures summary ================"
NP=$(printf '%s' "$PRODUCED" | grep -c . || true)
NS=$(printf '%s' "$SKIPPED"  | grep -c . || true)
echo "produced: $NP/14"
printf '%s' "$PRODUCED" | sed 's/^/  + /'
if [ "$NS" -gt 0 ]; then
    echo "skipped:  $NS"
    printf '%s' "$SKIPPED" | sed 's/^/  - /'
else
    echo "skipped:  0"
fi
echo "======================================================="
