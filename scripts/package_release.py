"""Package a clean publish directory and preserve third-party notices."""
import argparse
import hashlib
import json
from pathlib import Path
import shutil
import subprocess
import zipfile

parser = argparse.ArgumentParser()
parser.add_argument('--publish-dir', type=Path, required=True)
parser.add_argument('--assets-dir', type=Path, required=True)
args = parser.parse_args()
root = Path(__file__).resolve().parents[1]
publish = args.publish_dir.resolve()
assets = args.assets_dir.resolve()
assets.mkdir(parents=True, exist_ok=True)
for name in ('LICENSE', 'README.md', 'CHANGELOG.md', 'PRIVACY.md'):
    shutil.copy2(root / name, publish / name)
metadata = json.loads((root / 'src/OpenWrtDesk/obj/project.assets.json').read_text(encoding='utf-8'))
cache_roots = [Path(path) for path in metadata['packageFolders']]
packages = set()
for name, entry in metadata['libraries'].items():
    if entry['type'] == 'package':
        package, version = name.lower().split('/')
        packages.add((package, version))
for cache in cache_roots:
    for package in cache.glob('microsoft.*.app.runtime.win-x64'):
        packages.update((package.name, version.name) for version in package.iterdir() if version.is_dir())
for package, version in sorted(packages):
    filename = f'{package}.{version}.nupkg'
    archive_path = next((cache / package / version / filename for cache in cache_roots if (cache / package / version / filename).is_file()), None)
    if archive_path is None:
        raise RuntimeError(f'Missing third-party license package: {package}/{version}')
    with zipfile.ZipFile(archive_path) as archive:
        for item in archive.namelist():
            if Path(item).name.lower() not in ('license.txt', 'notice.txt', 'third-party-notices.txt'):
                continue
            relative = Path('third-party-licenses') / f'{package}.{version}' / item
            assert '..' not in relative.parts
            destination = publish / relative
            destination.parent.mkdir(parents=True, exist_ok=True)
            destination.write_bytes(archive.read(item))
(publish / 'THIRD-PARTY-NOTICES.md').write_text('OpenWrtDesk is MIT licensed. Bundled runtimes and components retain their own licenses; original notices are in third-party-licenses/.\n', encoding='utf-8')
files = [path for path in publish.rglob('*') if path.is_file()]
for path in files:
    relative = path.relative_to(publish)
    assert not set(relative.parts) & {'data', 'history', 'backups', '.git', 'obj', 'bin'}
    assert path.suffix.lower() not in ('.pdb', '.log', '.dmp', '.key', '.pfx')
    assert path.name.lower() != 'profiles.json'
version = '0.1.2'
binary = assets / f'OpenWrtDesk-Windows-x64-{version}.zip'
with zipfile.ZipFile(binary, 'w', zipfile.ZIP_DEFLATED, compresslevel=9) as archive:
    for path in sorted(files):
        archive.write(path, 'OpenWrtDesk/' + path.relative_to(publish).as_posix())
source = assets / f'OpenWrtDesk-Source-{version}.zip'
subprocess.run(['git', 'archive', '--format=zip', '--prefix=OpenWrtDesk/', '-o', str(source), 'HEAD'], cwd=root, check=True)
records = []
for path in (binary, source):
    with zipfile.ZipFile(path) as archive:
        assert archive.testzip() is None and 'OpenWrtDesk/LICENSE' in archive.namelist()
    records.append({'name': path.name, 'size': path.stat().st_size, 'sha256': hashlib.sha256(path.read_bytes()).hexdigest()})
(assets / 'SHA256SUMS.txt').write_text(''.join(f"{item['sha256']}  {item['name']}\n" for item in records), encoding='utf-8')
print(json.dumps(records, indent=2))
