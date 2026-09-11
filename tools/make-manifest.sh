#!/bin/bash
#
# make-manifest.sh — S1.5 (issue #15)
#
# Writes fixtures/manifest.json: for every image in fixtures/generated/, its
# recipe, size, encryption, expected filesystem, and — the point of the whole
# exercise — the SHA-256 of its FULLY DECODED RAW SECTOR STREAM.
#
# The decoded hash is produced by APPLE'S decoder, never by ours. It is the
# ground truth our reader is measured against, so deriving it from our own code
# would make the test circular and worthless.
#
#   unencrypted:  hdiutil convert <img> -format UDTO -o <tmp>   (yields .cdr)
#                 shasum -a 256 <tmp>.cdr
#
#   encrypted:    hdiutil attach -stdinpass -nomount -readonly <img>
#                 dd if=/dev/rdiskN bs=1m | shasum -a 256
#                 hdiutil detach <that exact device>
#
# The two methods are cross-checked against each other on one unencrypted
# fixture (see CROSSCHECK below) and must agree; if they ever stop agreeing the
# script says so loudly and records which method each hash came from.
#
# PASSPHRASE for the encrypted fixtures: dmg-test-passphrase
#   Supplied to hdiutil ONLY on stdin, newline-terminated, via -stdinpass.
#   Never an argument, so it never lands in the process table.
#
# SAFETY — read before editing. These are not style preferences; an earlier
# attempt at this corpus hung hdiutil and put a GUI passphrase dialog on the
# developer's screen:
#   * Every hdiutil call goes through hd()/hdp(), which always supply stdin
#     explicitly and impose a hard SIGALRM timeout. A bare hdiutil that runs
#     out of stdin escalates to a GUI prompt. Never call hdiutil directly.
#   * -stdinpass reads up to a NEWLINE. Always printf '%s\n'.
#   * -stdinpass on `hdiutil convert` sets the passphrase of the OUTPUT image;
#     it CANNOT decrypt a source. Encrypted sources must be attached and read
#     through their raw device — which is exactly why the two hash methods
#     above exist.
#   * We detach ONLY devices this run attached, tracked in $ATTACHED and
#     cleaned up by a trap. The developer may well have unrelated images
#     mounted (an iOS Simulator runtime, a personal volume); a broad or looping
#     `hdiutil detach` is destructive and is forbidden.
#   * On timeout or failure a fixture is recorded as skipped. Never retry
#     interactively.
#
# Usage: tools/make-manifest.sh          # rewrites fixtures/manifest.json
#
# NOTE ON STALENESS: hdiutil bakes volume UUIDs and timestamps into the images,
# so re-running make-fixtures.sh produces a corpus with DIFFERENT hashes. The
# manifest therefore describes the corpus currently on this machine, not a
# universal golden value. Regenerate it whenever you regenerate the fixtures.
# Each record carries image_sha256 (the hash of the .dmg container itself) so a
# test can detect a manifest that has drifted out of step with its fixtures.
#
set -u

PASS="dmg-test-passphrase"
TMO=300                                   # hard per-hdiutil-call timeout, seconds
CROSSCHECK="exfat-zlib.dmg"               # hashed BOTH ways, as a control

SCRIPT_DIR=$(cd -- "$(dirname -- "$0")" && pwd)
REPO_ROOT=$(cd -- "$SCRIPT_DIR/.." && pwd)
GEN="$REPO_ROOT/fixtures/generated"
MANIFEST="$REPO_ROOT/fixtures/manifest.json"
WORK="$GEN/.manifest-work"

[ -d "$GEN" ] || { echo "no $GEN — run tools/make-fixtures.sh first" >&2; exit 1; }
rm -rf "$WORK"; mkdir -p "$WORK" || exit 1

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
    rm -rf "$WORK"
}
trap cleanup EXIT INT TERM

# dev_of — the whole-disk /dev/diskN from hdiutil attach's output. attach
# interleaves progress lines ("Checksumming ...") with the device table, so we
# match /dev/disk explicitly rather than taking the first line.
dev_of() { printf '%s\n' "$1" | grep -Eo '^/dev/disk[0-9]+' | head -1; }

# ------------------------------------------------------------------- the table
#
# Recipe and expected filesystem per fixture. Kept in step with the tables in
# fixtures/README.md and the build order in tools/make-fixtures.sh. Recipes
# deliberately use single quotes so they need no JSON escaping.
#
# BASE is the shared 12 MiB exFAT source image that seven fixtures derive from;
# all seven therefore decode to the same raw sector stream, which is what makes
# a codec-vs-codec equality test possible - raw against zlib against ADC on
# bytes that are identical by construction.

BASE_RECIPE="hdiutil create -size 12m -fs exFAT -volname DMGFIX"

meta() {                                  # $1 name -> RECIPE FSYS ENC PURPOSE
    RECIPE=""; FSYS=""; ENC=false; PURPOSE=""
    case "$1" in
    exfat-raw.dmg)
        RECIPE="$BASE_RECIPE | hdiutil convert -format UDRW"
        FSYS=exFAT;   PURPOSE="Flat sector image - UDRW has no koly trailer and no chunk table, so despite the name it carries no chunks of any type. The reader must find no container in it." ;;
    exfat-zlib.dmg)
        RECIPE="$BASE_RECIPE | hdiutil convert -format UDZO"
        FSYS=exFAT;   PURPOSE="zlib-compressed chunks; the main case." ;;
    exfat-udro.dmg)
        RECIPE="$BASE_RECIPE | hdiutil convert -format UDRO"
        FSYS=exFAT;   PURPOSE="Genuine raw (0x00000001) UDIF chunks - a real koly + plist + blkx container storing its chunks uncompressed." ;;
    exfat-sparse.dmg)
        RECIPE="hdiutil create -size 48m -fs exFAT -volname DMGSPARSE, one small file | hdiutil convert -format UDZO"
        FSYS=exFAT;   PURPOSE="Mostly empty volume, so most chunks are zero-fill / ignore." ;;
    exfat-enc256.dmg)
        RECIPE="$BASE_RECIPE | hdiutil convert -format UDRW -encryption AES-256 -stdinpass"
        FSYS=exFAT;   ENC=true
        PURPOSE="encrcdsa v2 wrapper, AES-256." ;;
    exfat-enc128.dmg)
        RECIPE="$BASE_RECIPE | hdiutil convert -format UDRW -encryption AES-128 -stdinpass"
        FSYS=exFAT;   ENC=true
        PURPOSE="encrcdsa v2 wrapper, AES-128 key length." ;;
    exfat-enc-udzo.dmg)
        RECIPE="$BASE_RECIPE | hdiutil convert -format UDZO -encryption AES-256 -stdinpass"
        FSYS=exFAT;   ENC=true
        PURPOSE="encrcdsa v2 wrapper around a genuine UDZO UDIF container (S4.10/#91) - the decrypted payload carries a koly trailer, property list and zlib chunk table, unlike exfat-enc256/128 which wrap a flat UDRW stream with no koly at all." ;;
    fat32.dmg)
        RECIPE="hdiutil create -size 48m -fs 'MS-DOS FAT32' -volname DMGFAT32 | hdiutil convert -format UDZO"
        FSYS=FAT32;   PURPOSE="FAT32 probe. 48 MiB because FAT32 needs at least 65525 clusters." ;;
    hfsplus.dmg)
        RECIPE="hdiutil create -size 12m -fs 'HFS+' -volname DMGHFS | hdiutil convert -format UDZO"
        FSYS="HFS+";  PURPOSE="Negative case - the reader must refuse HFS+." ;;
    apfs.dmg)
        RECIPE="hdiutil create -size 32m -fs APFS -volname DMGAPFS | hdiutil convert -format UDZO"
        FSYS=APFS;    PURPOSE="Negative case - the reader must refuse APFS. 32 MiB because APFS will not newfs into 12 MiB." ;;
    bzip2.dmg)
        RECIPE="$BASE_RECIPE | hdiutil convert -format UDBZ"
        FSYS=exFAT;   PURPOSE="Negative case - bzip2 is an unsupported codec." ;;
    adc.dmg)
        RECIPE="$BASE_RECIPE | hdiutil convert -format UDCO"
        FSYS=exFAT;   PURPOSE="Apple ADC decoder." ;;
    zerofill.dmg)
        RECIPE="hdiutil create -srcfolder <HELLO.TXT README.TXT DATA.BIN ZEROS.BIN> -fs exFAT -volname DMGZERO -format UDZO"
        FSYS=exFAT;   PURPOSE="Zero-fill (0x00000000) chunks alongside zlib and ignore. 'create -srcfolder' is the only hdiutil path found that emits zero-fill; every 'convert' recipe uses the codec for zeros and 'ignore' for free space. ZEROS.BIN puts a long zero run inside allocated space." ;;
    multipart.dmg)
        RECIPE="hdiutil create -size 80m -layout NONE -type UDIF | diskutil partitionDisk MBR ExFAT DMGFIXP1 36M ExFAT DMGFIXP2 R | hdiutil convert -format UDZO"
        FSYS="exFAT x2"
        PURPOSE="Two data partitions, for partition selection. MBR not GPT - diskutil insists on a 200 MiB EFI partition under GPT." ;;
    *)
        return 1 ;;
    esac
    return 0
}

# Build order == manifest order, so the file is stable across runs.
ORDER="exfat-raw.dmg exfat-zlib.dmg exfat-udro.dmg exfat-sparse.dmg exfat-enc256.dmg
       exfat-enc128.dmg exfat-enc-udzo.dmg fat32.dmg hfsplus.dmg apfs.dmg bzip2.dmg adc.dmg
       multipart.dmg zerofill.dmg"

# ------------------------------------------------------------------ hashing

# hash_via_convert — Apple decodes the image to a flat .cdr; we hash that.
# Works for unencrypted images only (convert cannot decrypt a source).
# Sets HASH and DSIZE. $1 = image path.
hash_via_convert() {
    local img="$1" out="$WORK/decoded"
    HASH=""; DSIZE=""
    rm -f "$out" "$out.cdr"
    hd convert "$img" -format UDTO -o "$out" >/dev/null 2>&1 || return 1
    [ -f "$out.cdr" ] || return 1
    HASH=$(shasum -a 256 < "$out.cdr" | awk '{print $1}')
    DSIZE=$(wc -c < "$out.cdr" | tr -d ' ')
    rm -f "$out.cdr"
    [ -n "$HASH" ]
}

# hash_via_attach — attach the image with no filesystem mounted and hash the
# raw device. This is the ONLY way to read an encrypted source. Sets HASH and
# DSIZE. $1 = image path, $2 = "enc" for an encrypted image.
hash_via_attach() {
    local img="$1" enc="${2:-}" out dev
    HASH=""; DSIZE=""
    if [ "$enc" = "enc" ]; then
        out=$(hdp attach -stdinpass -nomount -readonly "$img" 2>/dev/null) || return 1
    else
        out=$(hd  attach          -nomount -readonly "$img" 2>/dev/null) || return 1
    fi
    dev=$(dev_of "$out")
    [ -n "$dev" ] || return 1
    track "$dev"

    local raw="/dev/r${dev#/dev/}"
    # Raw device first (fast, unbuffered); fall back to the buffered device,
    # which tolerates a size that is not a whole number of 1 MiB blocks.
    HASH=$(perl -e 'alarm shift; exec @ARGV' "$TMO" dd if="$raw" bs=1m 2>/dev/null | shasum -a 256 | awk '{print $1}')
    if [ -z "$HASH" ] || [ "$HASH" = "$EMPTY_SHA" ]; then
        HASH=$(perl -e 'alarm shift; exec @ARGV' "$TMO" dd if="$dev" bs=1m 2>/dev/null | shasum -a 256 | awk '{print $1}')
    fi
    DSIZE=$(dk info -plist "$dev" 2>/dev/null \
            | plutil -extract Size raw - 2>/dev/null | tr -d ' ')
    detach "$dev"
    [ -n "$HASH" ] && [ "$HASH" != "$EMPTY_SHA" ]
}

EMPTY_SHA=$(printf '' | shasum -a 256 | awk '{print $1}')

# img_field — one scalar out of hdiutil imageinfo. $1 image, $2 key, $3 "enc".
img_field() {
    local img="$1" key="$2" enc="${3:-}" info
    if [ "$enc" = "enc" ]; then
        info=$(hdp imageinfo -stdinpass "$img" 2>/dev/null) || return 1
    else
        info=$(hd  imageinfo "$img" 2>/dev/null) || return 1
    fi
    printf '%s\n' "$info" | awk -v k="$key" -F': *' \
        '$0 ~ "^[[:space:]]*" k ":" { sub("^[^:]*:[[:space:]]*", ""); print; exit }'
}

# =============================================================================
# Walk the corpus
# =============================================================================

echo "make-manifest: corpus -> $GEN"

RECORDS=""
NDONE=0
NSKIP=0
SKIPPED=""
CC_CONVERT=""
CC_ATTACH=""

for NAME in $ORDER; do
    IMG="$GEN/$NAME"
    if [ ! -f "$IMG" ]; then
        echo "  SKIP    $NAME — not present in fixtures/generated/" >&2
        SKIPPED="$SKIPPED$NAME: not present
"; NSKIP=$((NSKIP + 1)); continue
    fi
    meta "$NAME" || continue

    ENCFLAG=""
    [ "$ENC" = true ] && ENCFLAG="enc"

    ISIZE=$(wc -c < "$IMG" | tr -d ' ')
    ISHA=$(shasum -a 256 < "$IMG" | awk '{print $1}')
    FORMAT=$(img_field "$IMG" Format "$ENCFLAG")
    SCHEME=$(img_field "$IMG" partition-scheme "$ENCFLAG")
    [ -n "$FORMAT" ] || FORMAT="unknown"
    [ -n "$SCHEME" ] || SCHEME="unknown"

    # --- the ground-truth hash ------------------------------------------------
    METHOD=""
    if [ "$ENC" = true ]; then
        # convert cannot decrypt a source; attach + raw device is the only way.
        if hash_via_attach "$IMG" enc; then
            METHOD="hdiutil attach -stdinpass -nomount -readonly + dd /dev/rdiskN"
        else
            echo "  SKIP    $NAME — could not decode (attach)" >&2
            SKIPPED="$SKIPPED$NAME: attach/dd decode failed
"; NSKIP=$((NSKIP + 1)); continue
        fi
    else
        if hash_via_convert "$IMG"; then
            METHOD="hdiutil convert -format UDTO"
        else
            echo "  SKIP    $NAME — could not decode (convert -format UDTO)" >&2
            SKIPPED="$SKIPPED$NAME: convert -format UDTO failed
"; NSKIP=$((NSKIP + 1)); continue
        fi
    fi

    # --- the cross-check ------------------------------------------------------
    # One unencrypted fixture is hashed BOTH ways, to prove that "convert to
    # .cdr" and "attach and read the raw device" really are the same stream.
    if [ "$NAME" = "$CROSSCHECK" ]; then
        CC_CONVERT="$HASH"
        SAVE_H="$HASH"; SAVE_D="$DSIZE"
        if hash_via_attach "$IMG"; then
            CC_ATTACH="$HASH"
        else
            CC_ATTACH="(attach method failed)"
        fi
        HASH="$SAVE_H"; DSIZE="$SAVE_D"
    fi

    [ -n "$DSIZE" ] || DSIZE=0

    PASSFIELD=""
    [ "$ENC" = true ] && PASSFIELD="
      \"passphrase\": \"$PASS\","

    RECORDS="$RECORDS    {
      \"name\": \"$NAME\",
      \"recipe\": \"$RECIPE\",
      \"format\": \"$FORMAT\",
      \"partition_scheme\": \"$SCHEME\",
      \"filesystem\": \"$FSYS\",
      \"purpose\": \"$PURPOSE\",
      \"encrypted\": $ENC,$PASSFIELD
      \"image_size\": $ISIZE,
      \"image_sha256\": \"$ISHA\",
      \"decoded_size\": $DSIZE,
      \"decoded_sha256\": \"$HASH\",
      \"decoded_hash_method\": \"$METHOD\"
    },
"
    NDONE=$((NDONE + 1))
    echo "  ok      $NAME  $HASH"
done

# Strip the trailing comma from the last record.
RECORDS=$(printf '%s' "$RECORDS" | perl -0pe 's/,\n\z/\n/')

# ------------------------------------------------------------- cross-check verdict

if [ -n "$CC_CONVERT" ] && [ "$CC_CONVERT" = "$CC_ATTACH" ]; then
    CC_RESULT="agree"
    CC_NOTE="Both methods produced the same SHA-256 for $CROSSCHECK, so 'convert to .cdr' and 'attach and read /dev/rdiskN' are the same byte stream."
elif [ -n "$CC_CONVERT" ]; then
    CC_RESULT="DISAGREE"
    CC_NOTE="The two methods DISAGREED on $CROSSCHECK (convert=$CC_CONVERT attach=$CC_ATTACH). Unencrypted fixtures in this manifest use 'hdiutil convert -format UDTO'; encrypted ones have no alternative to attach + dd. See fixtures/README.md."
else
    CC_RESULT="not-run"
    CC_NOTE="$CROSSCHECK was not present, so the two hashing methods were not cross-checked."
fi

# ------------------------------------------------------------------- emit JSON

{
cat <<JSON
{
  "_comment": "Ground truth for the DMG test corpus. Generated by tools/make-manifest.sh - do not hand-edit. Every decoded_sha256 comes from Apple's own decoder (hdiutil), never from our reader; a hash produced by the code under test would make the test circular.",
  "_staleness": "hdiutil bakes volume UUIDs and timestamps into the images, so re-running tools/make-fixtures.sh yields a corpus with different hashes. This manifest describes the corpus currently in fixtures/generated/, not a universal golden value. Regenerate it whenever you regenerate the fixtures; compare image_sha256 to detect drift.",
  "schema_version": 1,
  "generator": "tools/make-manifest.sh",
  "fixture_dir": "fixtures/generated",
  "passphrase": "$PASS",
  "hash": {
    "algorithm": "SHA-256",
    "subject": "the fully decoded raw sector stream - the whole-disk image with every codec and the encryption wrapper removed, exactly as Apple's decoder produces it",
    "method_unencrypted": "hdiutil convert <img> -format UDTO -o <tmp>, then shasum -a 256 <tmp>.cdr",
    "method_encrypted": "printf '%s\\\\n' <pass> | hdiutil attach -stdinpass -nomount -readonly <img>, then dd if=/dev/rdiskN bs=1m | shasum -a 256, then detach that exact device",
    "cross_check": {
      "fixture": "$CROSSCHECK",
      "result": "$CC_RESULT",
      "via_convert": "$CC_CONVERT",
      "via_attach": "$CC_ATTACH",
      "note": "$CC_NOTE"
    }
  },
  "shared_source_note": "exfat-raw, exfat-zlib, exfat-udro, exfat-enc256, exfat-enc128, exfat-enc-udzo, bzip2 and adc are all converted from one shared 12 MiB exFAT source image, so their decoded_sha256 values must be identical. That equality is itself a test: it isolates the codec and the encryption wrapper from everything else.",
  "count": $NDONE,
  "fixtures": [
JSON
printf '%s' "$RECORDS"
cat <<'JSON'
  ]
}
JSON
} > "$MANIFEST"

# =============================================================================

echo
echo "================ make-manifest summary ================"
echo "hashed:      $NDONE fixture(s) -> $MANIFEST"
if [ "$NSKIP" -gt 0 ]; then
    echo "skipped:     $NSKIP"
    printf '%s' "$SKIPPED" | sed 's/^/  - /'
else
    echo "skipped:     0"
fi
echo "cross-check: $CROSSCHECK -> $CC_RESULT"
echo "  via convert: ${CC_CONVERT:-n/a}"
echo "  via attach : ${CC_ATTACH:-n/a}"
echo "======================================================="

[ "$CC_RESULT" = "DISAGREE" ] && echo "WARNING: hashing methods disagree; see the manifest and fixtures/README.md" >&2
exit 0
