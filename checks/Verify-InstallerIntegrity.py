"""Verify this project's NSIS CRC before/after signing, without running Setup.

This checks file structure and bytes, not Authenticode trust. Signing identity,
timestamp and installation behavior require their separate release checks.
"""
import argparse
import hashlib
import json
from pathlib import Path
import re
import struct
import zlib


class IntegrityError(Exception):
    pass


def require(condition, message):
    if not condition:
        raise IntegrityError(message)


def u32(data, offset):
    require(0 <= offset <= len(data) - 4, "A DWORD falls outside the file.")
    return struct.unpack_from('<I', data, offset)[0]


def sha256(data):
    return hashlib.sha256(data).hexdigest().upper()


def file_version(data, stub_end):
    # Informational VS_FIXEDFILEINFO metadata; release signing independently
    # verifies the actual intended product version through Windows.
    key = 'VS_VERSION_INFO\0'.encode('utf-16le')
    versions = set()
    cursor = 0
    while True:
        key_offset = data.find(key, cursor, stub_end)
        if key_offset < 0:
            break
        cursor = key_offset + 2
        block = key_offset - 6
        value = (key_offset + len(key) + 3) & ~3
        if block < 0 or value + 52 > stub_end:
            continue
        length, value_length, kind = struct.unpack_from('<HHH', data, block)
        if kind != 0 or value_length < 52 or block + length > stub_end:
            continue
        if u32(data, value) == 0xFEEF04BD:
            high, low = struct.unpack_from('<II', data, value + 8)
            versions.add('.'.join(str(n) for n in (high >> 16, high & 65535, low >> 16, low & 65535)))
    return next(iter(versions)) if len(versions) == 1 else None


def inspect(path):
    try:
        data = Path(path).read_bytes()
    except OSError:
        raise IntegrityError("The selected artifact could not be read.")
    require(len(data) >= 512 and data[:2] == b'MZ', "Expected a Windows PE installer.")
    pe = u32(data, 0x3C)
    require(pe + 24 <= len(data) and data[pe:pe + 4] == b'PE\0\0', "Invalid PE header bounds.")
    optional = pe + 24
    optional_size = struct.unpack_from('<H', data, pe + 20)[0]
    require(optional_size >= 2 and optional + optional_size <= len(data), "Invalid optional-header bounds.")
    magic = struct.unpack_from('<H', data, optional)[0]
    require(magic in (0x10B, 0x20B), "Unsupported PE optional-header format.")
    directory_start = optional + (96 if magic == 0x10B else 112)
    require(directory_start + 40 <= optional + optional_size, "Missing PE certificate-directory field.")
    certificate_offset = u32(data, directory_start + 32)
    certificate_size = u32(data, directory_start + 36)
    require((certificate_offset == 0) == (certificate_size == 0), "Incomplete PE certificate-directory bounds.")
    if certificate_offset:
        require(certificate_offset % 8 == 0 and certificate_size >= 8,
                "Invalid PE certificate-directory alignment or size.")
        require(certificate_offset + certificate_size == len(data),
                "PE certificate directory does not end at the file boundary.")

    needle = b'\xef\xbe\xad\xdeNullsoftInst'
    candidates = []
    cursor = 0
    while True:
        marker = data.find(needle, cursor)
        if marker < 0:
            break
        cursor = marker + 1
        start = marker - 4
        if start < 512 or start + 28 > len(data):
            continue
        flags, _, _, _, _, header_bytes, following_bytes = struct.unpack_from('<7I', data, start)
        end = start + following_bytes
        if header_bytes > 0 and following_bytes >= 32 and start + 28 < end <= len(data):
            candidates.append((start, flags, header_bytes, following_bytes, end))
    require(len(candidates) == 1, "Expected exactly one bounded NSIS FirstHeader.")
    start, flags, header_bytes, following_bytes, end = candidates[0]
    # CRC checking is unconditional here: this tool never accepts a runtime
    # command-line CRC bypass or repairs/rewrites a damaged artifact.
    content_boundary = certificate_offset if certificate_offset else len(data)
    require(end <= content_boundary, "The NSIS declared end overlaps the PE certificate directory.")
    padding = data[end:content_boundary]
    require(len(padding) <= 7 and not any(padding), "Unexpected bytes between NSIS content and the certificate/file boundary.")
    stored = u32(data, end - 4)
    protected = data[512:end - 4]
    computed = zlib.crc32(protected) & 0xFFFFFFFF
    result = {
        'Schema': 1, 'Tool': 'SnipStudio.NSISIntegrity', 'InputName': Path(path).name,
        'FullSHA256': sha256(data), 'FileBytes': len(data), 'FileVersion': file_version(data, start),
        'ProtectedSHA256': sha256(protected), 'StoredCRC32': f'{stored:08X}',
        'ComputedCRC32': f'{computed:08X}', 'CRCMatches': stored == computed,
        'Header': {'Offset': start, 'Bytes': 28, 'Flags': flags,
                   'UncompressedHeaderBytes': header_bytes, 'DeclaredFollowingBytes': following_bytes,
                   'DeclaredEnd': end, 'ProtectedStart': 512, 'ProtectedEndExclusive': end - 4},
        'PE': {'Offset': pe, 'OptionalHeaderMagic': hex(magic),
               'CertificateOffset': certificate_offset, 'CertificateBytes': certificate_size,
               'PaddingBeforeCertificateOrFileEnd': len(padding)},
        'AuthenticodeTrustVerified': False, 'ArtifactExecuted': False, 'ArtifactModified': False,
    }
    return result


def baseline_record(path):
    if Path(path).suffix.lower() != '.json':
        result = inspect(path)
        require(result['CRCMatches'], "The baseline artifact has a CRC mismatch.")
        return result
    try:
        result = json.loads(Path(path).read_text(encoding='utf-8-sig'))
    except (OSError, ValueError):
        raise IntegrityError("The baseline report could not be read.")
    require(isinstance(result, dict) and result.get('Schema') == 1 and
            result.get('Tool') == 'SnipStudio.NSISIntegrity' and result.get('Passed') is True and
            result.get('CRCMatches') is True, "The baseline is not a passing integrity report.")
    return result


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('artifact', help='NSIS Setup file to inspect without executing it')
    parser.add_argument('--report', help='Optional local JSON evidence file')
    parser.add_argument('--baseline', help='Passing pre-sign JSON report or unsigned NSIS file')
    parser.add_argument('--expected-sha256', help='Optional explicitly selected full-file SHA256')
    args = parser.parse_args()
    if args.expected_sha256 and not re.fullmatch(r'[0-9A-Fa-f]{64}', args.expected_sha256):
        parser.error('--expected-sha256 must contain 64 hexadecimal characters')
    report = {'Schema': 1, 'Tool': 'SnipStudio.NSISIntegrity', 'Passed': False,
              'ArtifactExecuted': False, 'ArtifactModified': False,
              'Scope': 'Static PE/NSIS bounds, protected-byte hashes and CRC32 only; no signature-trust or install-behavior proof.'}
    try:
        report.update(inspect(args.artifact))
        require(report['CRCMatches'], "Stored NSIS CRC32 differs from the protected file bytes.")
        if args.expected_sha256:
            require(report['FullSHA256'] == args.expected_sha256.upper(), "Artifact differs from the explicitly selected SHA256.")
        if args.baseline:
            baseline = baseline_record(args.baseline)
            keys = ('ProtectedSHA256', 'Header', 'StoredCRC32', 'ComputedCRC32', 'FileVersion')
            require(all(key in baseline and report[key] == baseline[key] for key in keys),
                    "Protected bytes, NSIS bounds, stored CRC or version changed from the pre-sign baseline.")
            report['BaselineMatched'] = True
            report['BaselineFullSHA256'] = baseline.get('FullSHA256')
        report['Passed'] = True
    except IntegrityError as error:
        report['Error'] = str(error)
    except (ValueError, struct.error, OverflowError):
        report['Error'] = 'Malformed file or baseline structure.'
    text = json.dumps(report, indent=2) + '\n'
    if args.report:
        try:
            Path(args.report).write_text(text, encoding='utf-8')
        except OSError:
            report['Passed'] = False
            report['Error'] = 'The local evidence report could not be written.'
            text = json.dumps(report, indent=2) + '\n'
    print(text, end='')
    return 0 if report['Passed'] else 1


if __name__ == '__main__':
    raise SystemExit(main())
