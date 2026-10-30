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
    root_files = {name[len(prefix):] for name in files if '/' not in name[len(prefix):]}
    root_folders = {name[len(prefix):].split('/')[0] for name in files if '/' in name[len(prefix):]}
    assert root_files == {'RobloxAccountManager.exe', 'START-HERE.txt', 'SHA256SUMS.txt'}, 'Unexpected loose files at the top level'
    assert root_folders == {'Docs'}, 'Unexpected top-level folders'
    for required in ('RobloxAccountManager.exe', 'START-HERE.txt', 'SHA256SUMS.txt', 'Docs/LICENSE', 'Docs/NOTICE.md', 'Docs/examples/join-group.js', 'Docs/ThirdPartyNotices/WebView2-LICENSE.txt'):
        assert prefix + required in files, f'Missing portable component: {required}'
    checked = set()
    for line in zipped.read(files[prefix + 'SHA256SUMS.txt']).decode('utf-8-sig').splitlines():
        checksum, relative = line.split('  ', 1)
        name = prefix + relative.replace('\\', '/')
        assert name not in checked, 'Duplicate checksum entry'
        checked.add(name)
        contents = zipped.read(files[name])
        assert hashlib.sha256(contents).hexdigest() == checksum, f'Checksum mismatch: {relative}'
    assert checked == set(files) - {prefix + 'SHA256SUMS.txt'}, 'Checksums must cover every shipped file'
    exe = zipped.read(files[prefix + 'RobloxAccountManager.exe'])

# Inspect the .NET 6+ bundle manifest, rather than merely trusting a large EXE.
# Format: dotnet/runtime Microsoft.NET.HostModel/Bundle/{Manifest,FileEntry}.cs.
marker = bytes.fromhex('8b1202b96a612038727b930214d7a03213f5b9e6efae3318ee3b2dce24b36aae')
marker_offset = exe.find(marker)
assert marker_offset >= 8, 'EXE is not a .NET single-file bundle'
cursor = struct.unpack_from('<q', exe, marker_offset - 8)[0]
assert 0 < cursor < len(exe), 'Invalid bundle manifest offset'
major, minor, entry_count = struct.unpack_from('<IIi', exe, cursor)
assert major == 6 and minor == 0 and 0 < entry_count < 10000, 'Unsupported bundle manifest'
cursor += 12

def bundle_string():
    global cursor
    length = 0
    for shift in range(0, 35, 7):
        value = exe[cursor]
        cursor += 1
        length |= (value & 0x7f) << shift
        if value < 128:
            break
    else:
        raise AssertionError('Invalid bundle string length')
    result = exe[cursor:cursor + length].decode('utf-8')
    cursor += length
    return result

bundle_id = bundle_string()
deps_offset, deps_size, config_offset, config_size, flags = struct.unpack_from('<qqqqQ', exe, cursor)
cursor += 40
assert flags == 0, 'Compatibility extraction changes the portable data location'
bundled = {}
for _ in range(entry_count):
    offset, size, compressed_size, file_type = struct.unpack_from('<qqqB', exe, cursor)
    cursor += 25
    name = bundle_string()
    assert name not in bundled and '..' not in PurePosixPath(name).parts and not name.startswith('/'), 'Invalid bundle path'
    assert size > 0 and compressed_size == 0 and 0 <= offset < offset + size <= len(exe), 'Invalid bundled file bounds'
    assert not any(part.lower() in {'data', 'browserdata', '.tools', '.git', 'test-results'} for part in PurePosixPath(name).parts), 'Private data in EXE bundle'
    assert not name.lower().endswith(('.vault', '.previous', '.backup', '.ramlayout', '.env', '.pdb')), 'Private/development file in EXE bundle'
    bundled[name] = (offset, size)
# .NET's Windows single-file host statically links coreclr; its managed core library is bundled.
for required in ('RobloxAccountManager.dll', 'System.Private.CoreLib.dll', 'PresentationFramework.dll', 'Microsoft.Web.WebView2.Core.dll', 'Microsoft.Web.WebView2.Wpf.dll'):
    assert required in bundled, f'Missing bundled component: {required}'
assert any(PurePosixPath(name).name == 'WebView2Loader.dll' for name in bundled), 'Missing bundled WebView2 native loader'
assert bundled['RobloxAccountManager.runtimeconfig.json'] == (config_offset, config_size), 'Runtime config manifest mismatch'
config = json.loads(exe[config_offset:config_offset + config_size])['runtimeOptions']
assert 'includedFrameworks' in config and 'frameworks' not in config and 'framework' not in config, 'Runtime is not self-contained'

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
print(f'PASS share ZIP: EXE + 2 text files + Docs; {len(bundled)} bundled components, self-contained runtime, complete checksums, embedded icon, no user data')
