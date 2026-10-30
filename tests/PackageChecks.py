"""Validate the shareable archive, including icon resources and the absence of user data."""
from pathlib import Path, PurePosixPath
import hashlib
import json
import struct
import sys
import zipfile

archive = Path(sys.argv[1])
with zipfile.ZipFile(archive) as zipped:
    files = {entry.filename.replace('\\', '/'): entry for entry in zipped.infolist() if not entry.is_dir()}
    prefix = 'RobloxAccountManager/'
    assert files and all(name.startswith(prefix) for name in files), 'Missing top-level app folder'
    for name in files:
        parts = PurePosixPath(name).parts
        assert '..' not in parts and not name.startswith('/'), 'Unsafe archive path'
        assert not any(p.lower() in {'data', 'browserdata', '.tools', '.git', 'test-results'} for p in parts), 'Private data directory included'
        assert not name.lower().endswith(('.vault', '.previous', '.backup', '.ramlayout', '.env', '.pdb')), 'Private/development file included'
    for required in ('RobloxAccountManager.exe', 'RobloxAccountManager.dll', 'coreclr.dll', 'hostfxr.dll', 'PresentationFramework.dll', 'START-HERE.txt', 'SHA256SUMS.txt'):
        assert prefix + required in files, f'Missing portable component: {required}'
    config = json.loads(zipped.read(files[prefix + 'RobloxAccountManager.runtimeconfig.json']))['runtimeOptions']
    assert 'includedFrameworks' in config and 'frameworks' not in config and 'framework' not in config, 'Runtime is not self-contained'
    for line in zipped.read(files[prefix + 'SHA256SUMS.txt']).decode('utf-8-sig').splitlines():
        checksum, relative = line.split('  ', 1)
        contents = zipped.read(files[prefix + relative.replace('\\', '/')])
        assert hashlib.sha256(contents).hexdigest() == checksum, f'Checksum mismatch: {relative}'
    exe = zipped.read(files[prefix + 'RobloxAccountManager.exe'])

# Confirm RT_ICON + RT_GROUP_ICON resources exist in the actual shipped EXE.
pe = struct.unpack_from('<I', exe, 0x3C)[0]
assert exe[pe:pe+4] == b'PE\0\0'
sections, optional_size = struct.unpack_from('<H', exe, pe+6)[0], struct.unpack_from('<H', exe, pe+20)[0]
optional = pe+24
magic = struct.unpack_from('<H', exe, optional)[0]
directory = optional + (112 if magic == 0x20B else 96)
resource_rva = struct.unpack_from('<I', exe, directory + 16)[0]
resource_offset = None
section_map = []
for i in range(sections):
    section = optional + optional_size + i*40
    virtual_size, virtual_address, raw_size, raw_offset = struct.unpack_from('<IIII', exe, section+8)
    section_map.append((virtual_address, max(virtual_size, raw_size), raw_offset))
    if virtual_address <= resource_rva < virtual_address + max(virtual_size, raw_size):
        resource_offset = raw_offset + resource_rva - virtual_address
assert resource_offset is not None, 'No resource section'
named, numeric = struct.unpack_from('<HH', exe, resource_offset+12)
types = {struct.unpack_from('<I', exe, resource_offset+16+i*8)[0] for i in range(named+numeric)}
assert 3 in types and 14 in types, 'Executable icon resources are missing'
def rva_offset(rva):
    for address, size, raw in section_map:
        if address <= rva < address + size:
            return raw + rva - address
    raise AssertionError('Invalid resource RVA')
def leaves(offset):
    a, b = struct.unpack_from('<HH', exe, offset+12)
    result = []
    for i in range(a+b):
        _, target = struct.unpack_from('<II', exe, offset+16+i*8)
        if target & 0x80000000:
            result.extend(leaves(resource_offset + (target & 0x7fffffff)))
        else:
            address, size = struct.unpack_from('<II', exe, resource_offset+target)
            result.append(exe[rva_offset(address):rva_offset(address)+size])
    return result
icons = []
for i in range(named+numeric):
    kind, target = struct.unpack_from('<II', exe, resource_offset+16+i*8)
    if kind == 3:
        icons.extend(leaves(resource_offset+(target & 0x7fffffff)))
ico = (Path(__file__).resolve().parents[1] / 'src/LocalAccountManager/Assets/AppIcon.ico').read_bytes()
frames = []
for i in range(struct.unpack_from('<H', ico, 4)[0]):
    size, offset = struct.unpack_from('<II', ico, 6+i*16+8)
    frames.append(ico[offset:offset+size])
assert any(payload in frames for payload in icons), 'Shipped EXE does not contain the expected custom icon'
print(f'PASS share ZIP: {len(files)} files, self-contained runtime, valid checksums, embedded EXE icon, no vaults/profiles/user data')
