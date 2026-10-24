# -*- coding: utf-8 -*-
"""列出某个 (key, vel) 命中的 zone，用于核对 SF2 解析。"""
import struct, sys, os
sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
from sf2_probe import parse

def zones_for(d, key, vel):
    buf = open(d['__path'], 'rb').read() if '__path' in d else None
    return d

def main(path, keys):
    d = parse(path)
    if not d: return
    # 需要原始 pdta 缓冲区
    with open(path, 'rb') as f:
        data = f.read()
    # 定位 pdta 各表（sf2_probe.parse 已给出记录，但 zone 需要 pgen/igen 原始值）
    # 这里重新从 parse 的返回里取
    pbag, pgen, ibag, igen = d['pbag'], d['pgen'], d['ibag'], d['igen']
    phdr, inst, shdr = d['phdr'], d['inst'], d['shdr']
    print('%s: presets=%d insts=%d samples=%d' % (os.path.basename(path), len(phdr)-1, len(inst)-1, len(shdr)-1))
    print('pbag=%d pgen=%d ibag=%d igen=%d' % (len(pbag), len(pgen), len(ibag), len(igen)))

    for key in keys:
        for vel in (30, 100):
            hits = []
            # preset 0
            for pz in range(phdr[0]['bag'], phdr[1]['bag']):
                g0, g1 = pbag[pz][0], pbag[pz+1][0]
                instIdx = None
                pk = (0, 127); pv = (0, 127)
                for gi in range(g0, min(g1, len(pgen))):
                    op, val = pgen[gi]
                    if op == 41: instIdx = val
                    elif op == 43: pk = (val & 0xFF, val >> 8)
                    elif op == 44: pv = (val & 0xFF, val >> 8)
                if instIdx is None: continue
                for iz in range(inst[instIdx]['bag'], inst[instIdx+1]['bag']):
                    if iz + 1 >= len(ibag): break
                    h0, h1 = ibag[iz][0], ibag[iz+1][0]
                    ik = (0, 127); iv = (0, 127); sid = None; root = None; tune = 0
                    for gi in range(h0, min(h1, len(igen))):
                        op, val = igen[gi]
                        if op == 43: ik = (val & 0xFF, val >> 8)
                        elif op == 44: iv = (val & 0xFF, val >> 8)
                        elif op == 53: sid = val
                        elif op == 58: root = val
                        elif op == 51: tune = val
                    if sid is None: continue
                    lo = max(pk[0], ik[0]); hi = min(pk[1], ik[1])
                    vlo = max(pv[0], iv[0]); vhi = min(pv[1], iv[1])
                    if lo <= key <= hi and vlo <= vel <= vhi:
                        s = shdr[sid]
                        hits.append((s['name'], ik, iv, sid, root, s['pitch'], s['corr'], s['rate']))
            print('key=%d vel=%d -> %d zones' % (key, vel, len(hits)))
            for h in hits[:8]:
                print('   %-22s keyRange=%-10s velRange=%-10s sid=%-3d root=%s s.pitch=%d corr=%d rate=%d' % h)

if __name__ == '__main__':
    p = sys.argv[1]
    keys = [int(x) for x in sys.argv[2:]] or [60]
    main(p, keys)
