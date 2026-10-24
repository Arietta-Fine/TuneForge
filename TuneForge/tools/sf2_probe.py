# -*- coding: utf-8 -*-
"""极简 SF2 结构探针：递归解析 RIFF LIST，列出 preset / instrument / sample 概况。"""
import struct, sys, os

def parse(path):
    size = os.path.getsize(path)
    f = open(path, 'rb')
    def rd(pos, n):
        f.seek(pos); return f.read(n)
    if rd(0, 4) != b'RIFF' or rd(8, 4) != b'sfbk':
        print('not an sf2'); return None

    chunks = {}
    def walk(start, end):
        pos = start
        while pos + 8 <= end:
            cid = rd(pos, 4)
            csz = struct.unpack('<I', rd(pos+4, 4))[0]
            body = pos + 8
            if cid == b'LIST':
                walk(body + 4, body + csz)
            else:
                chunks.setdefault(cid.decode('latin-1'), []).append((body, csz))
            pos = body + csz + (csz & 1)
    walk(12, size)

    info = {}
    for tag in ('INAM', 'isng', 'ifil'):
        if tag in chunks:
            raw = rd(chunks[tag][0][0], chunks[tag][0][1])
            info[tag] = raw.decode('latin-1').strip('\x00').strip()

    smp = chunks.get('smpl', [(0, 0)])[0]

    def records(name, rec, conv):
        if name not in chunks: return []
        p, n = chunks[name][0]
        return [conv(rd(p + i*rec, rec)) for i in range(n // rec)]

    phdr = records('phdr', 38, lambda b: dict(
        name=b[:20].split(b'\x00')[0].decode('latin-1'),
        preset=struct.unpack('<H', b[20:22])[0],
        bank=struct.unpack('<H', b[22:24])[0],
        bag=struct.unpack('<H', b[24:26])[0]))
    inst = records('inst', 22, lambda b: dict(
        name=b[:20].split(b'\x00')[0].decode('latin-1'),
        bag=struct.unpack('<H', b[20:22])[0]))
    shdr = records('shdr', 46, lambda b: dict(
        name=b[:20].split(b'\x00')[0].decode('latin-1'),
        start=struct.unpack('<I', b[20:24])[0],
        end=struct.unpack('<I', b[24:28])[0],
        sloop=struct.unpack('<I', b[28:32])[0],
        eloop=struct.unpack('<I', b[32:36])[0],
        rate=struct.unpack('<I', b[36:40])[0],
        pitch=b[40],
        corr=struct.unpack('<b', b[41:42])[0],
        stype=struct.unpack('<H', b[44:46])[0]))
    pbag = records('pbag', 4, lambda b: struct.unpack('<HH', b))
    pgen = records('pgen', 4, lambda b: struct.unpack('<HH', b))
    ibag = records('ibag', 4, lambda b: struct.unpack('<HH', b))
    igen = records('igen', 4, lambda b: struct.unpack('<HH', b))

    return dict(info=info, smp=smp, phdr=phdr, inst=inst, shdr=shdr,
                pbag=pbag, pgen=pgen, ibag=ibag, igen=igen)

GEN_KEYRANGE = 43
GEN_VELRANGE = 44
GEN_INSTRUMENT = 41
GEN_SAMPLEID = 53
GEN_ROOTKEY = 58
GEN_COARSETUNE = 51
GEN_FINETUNE = 52
GEN_ATTEN = 48
GEN_PAN = 17

def main(path):
    d = parse(path)
    if not d: return
    print('=' * 74)
    print('%s  %.1f MB' % (os.path.basename(path), os.path.getsize(path)/1048576))
    for k, v in d['info'].items(): print('  %s = %s' % (k, v))
    print('  smpl %.1f MB | presets %d | instruments %d | samples %d' % (
        d['smp'][1]/1048576, len(d['phdr'])-1, len(d['inst'])-1, len(d['shdr'])-1))

    samples = [s for s in d['shdr'][:-1] if s['end'] > s['start']]
    tot = sum(s['end']-s['start'] for s in samples)
    print('  有效采样 %d 个，%.1f MB PCM，约 %.1f 秒' % (len(samples), tot*2/1048576, tot/44100))

    print('  --- presets ---')
    for i, p in enumerate(d['phdr'][:-1]):
        print('   [%2d] bank=%3d prog=%3d  %s' % (i, p['bank'], p['preset'], p['name'][:44]))

    print('  --- instruments ---')
    for i, s in enumerate(d['inst'][:-1]):
        print('   [%2d] %s' % (i, s['name'][:44]))

    print('  --- samples（前 20）---')
    for s in samples[:20]:
        print('   %-18s n=%-8d rate=%-6d root=%-3d corr=%-3d loop=%d..%d type=%d' % (
            s['name'][:18], s['end']-s['start'], s['rate'], s['pitch'], s['corr'],
            s['sloop'], s['eloop'], s['stype']))
    return d

if __name__ == '__main__':
    for p in sys.argv[1:]:
        main(p)
