import re, pathlib, sys

ROOT = pathlib.Path(r'C:\Users\Lugia\KitLugia\KitLugia.GUI')

m = {
    '#ffd700': 'AccentColor', '#1a1a1a': 'Surface1', '#252525': 'Surface2', '#0f0f0f': 'Surface0',
    '#333333': 'BorderSubtle', '#333': 'BorderSubtle', '#555555': 'BorderStrong', '#555': 'BorderStrong',
    '#8a8a8a': 'TextMuted', '#888888': 'TextMuted', '#a0a0a0': 'TextDescription',
    '#4caf50': 'SuccessBrush', '#c42b1c': 'ErrorBrush', '#ffc107': 'WarningBrush',
}

propalt = 'Background|Foreground|BorderBrush|Fill|Stroke|CaretBrush|SelectionBrush'
pat = re.compile(r'\b(' + propalt + r')="(#[0-9A-Fa-f]{6}|#[0-9A-Fa-f]{3})"')

def rep(mm):
    tok = m.get(mm.group(2).lower())
    if not tok:
        return mm.group(0)
    return mm.group(1) + '="{DynamicResource ' + tok + '}"'

total_files = 0
total_subs = 0
for p in ROOT.rglob('*.xaml'):
    if 'obj' in p.parts or 'bin' in p.parts:
        continue
    raw = p.read_bytes()
    bom = raw.startswith(b'\xef\xbb\xbf')
    t = raw.decode('utf-8-sig')
    subs = [0]
    def counting(mm):
        subs[0] += 1
        return rep(mm)
    t2 = pat.sub(counting, t)
    if subs[0]:
        p.write_bytes((b'\xef\xbb\xbf' if bom else b'') + t2.encode('utf-8'))
        print(f'{p.relative_to(ROOT)}: {subs[0]}')
        total_files += 1
        total_subs += subs[0]
print('arquivos:', total_files, 'subs:', total_subs)
