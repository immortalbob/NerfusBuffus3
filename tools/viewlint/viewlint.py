#!/usr/bin/env python3
# ---------------------------------------------------------------------------------------------
# Doc-14 sect. 10 static view lints - run after EVERY geometry edit to the Resources/*.xml views:
#   (a) flag any labeled control whose estimated text width exceeds its declared width
#       (VVS hard-clips at the declared width, and its font is wider than the 2004 renderer's:
#        est = chars * fontsize * 0.48 px, + ~18 px for a checkbox glyph; 1 px tolerance);
#   (b) group controls by (page, top) and flag horizontal overlaps and right/bottom edges past
#       the container (page interior height = notebook height - ~28 px tab strip).
# Exit code 1 if any issue is found, so it can gate CI next to shimcheck.
#
# Usage: python3 tools/viewlint/viewlint.py            (from the repo root)
# ---------------------------------------------------------------------------------------------
import sys, glob, os
import xml.etree.ElementTree as ET

TAB_STRIP = 28          # px eaten by a Notebook's tab strip (doc 14 sect. 2)
VIEW_TITLE_BAR = 28     # px VVS reserves for the window title bar out of the declared <view height>:
                        # a control whose bottom falls in that zone is clipped in-game even though it
                        # sits within the declared height. Every shipped view leaves >=35 px of bottom
                        # slack for exactly this reason; enforcing it here catches an over-shrunk window
                        # (the "resize went badly" clip) offline instead of on the live client.
CHAR_W = 0.48           # px per char per fontsize unit (doc 14 sect. 10)
CHECK_GLYPH = 18        # px for a checkbox glyph
TOLERANCE = 1.0         # the width heuristic is +-subpixel per char

def ints(c, *keys):
    out = []
    for k in keys:
        v = c.get(k)
        out.append(int(v) if v and v.lstrip('-').isdigit() else None)
    return out

def lint(path):
    issues = []
    root = ET.parse(path).getroot()
    vw, vh = int(root.get('width')), int(root.get('height'))

    def walk(el, cw, ch, where):
        for c in el:
            if c.tag == 'page':
                walk(c, cw, ch - TAB_STRIP, where + '/pg[' + c.get('label', '') + ']')
                continue
            if c.tag != 'control':
                continue
            pid = c.get('progid', '').split('.')[-1]
            l, t, w, h = ints(c, 'left', 'top', 'width', 'height')
            name = c.get('name') or (c.get('text') or '')[:20]
            if pid == 'FixedLayout':
                walk(c, cw, ch, where)
                continue
            if l is not None and w is not None and l + w > cw:
                issues.append(f"RIGHT-EDGE {pid} '{name}': right={l+w} > container {cw} ({where})")
            if t is not None and h is not None and t + h > ch:
                issues.append(f"BOTTOM-EDGE {pid} '{name}': bottom={t+h} > container {ch} ({where})")
            txt, fs = c.get('text'), c.get('fontsize')
            if txt and fs and w and pid in ('StaticText', 'Checkbox', 'PushButton'):
                est = len(txt) * int(fs) * CHAR_W + (CHECK_GLYPH if pid == 'Checkbox' else 0)
                if est - TOLERANCE > w:
                    issues.append(f"TEXT-CLIP {pid} '{name}': needs ~{est:.0f}px, declared {w}px ({where})")
            if pid == 'Notebook':
                walk(c, w, h, where + '/nb')

    def rows(el, where):
        ctrls = []
        for c in el:
            if c.tag != 'control':
                continue
            pid = c.get('progid', '').split('.')[-1]
            if pid == 'FixedLayout':
                rows(c, where)
                continue
            if pid == 'Notebook':
                for p in c:
                    if p.tag == 'page':
                        rows(p, where + '/pg[' + p.get('label', '') + ']')
                continue
            l, t, w, h = ints(c, 'left', 'top', 'width', 'height')
            if None in (l, t, w):
                continue
            ctrls.append((t, l, w, pid, c.get('name') or (c.get('text') or '')[:15]))
        for i in range(len(ctrls)):
            for j in range(i + 1, len(ctrls)):
                a, b = ctrls[i], ctrls[j]
                if abs(a[0] - b[0]) < 10 and a[3] != 'PushButton' and b[3] != 'PushButton':
                    if a[1] < b[1] + b[2] and b[1] < a[1] + a[2]:
                        issues.append(f"OVERLAP '{a[4]}'({a[1]}..{a[1]+a[2]}) vs '{b[4]}'({b[1]}..{b[1]+b[2]}) at top~{a[0]} ({where})")

    # Subtract the VVS title bar from the usable height so a control pushed into that bottom zone is
    # flagged (BOTTOM-EDGE) — this is what the old full-height check missed.
    walk(root, vw, vh - VIEW_TITLE_BAR, '')
    rows(root, '')
    return issues

def main():
    base = os.path.join(os.path.dirname(__file__), '..', '..', 'src', 'NB3.Plugin', 'Resources')
    views = sorted(glob.glob(os.path.join(base, 'nb3-*.xml')))
    views = [v for v in views if os.path.basename(v) != 'nb3-spells.xml']   # data, not a view
    total = 0
    for v in views:
        issues = lint(v)
        print(f"{os.path.basename(v)}: {'OK' if not issues else str(len(issues)) + ' issue(s)'}")
        for i in issues:
            print('  ' + i)
        total += len(issues)
    print(f"viewlint: {'PASS' if total == 0 else 'FAIL (' + str(total) + ' issues)'}")
    return 1 if total else 0

if __name__ == '__main__':
    sys.exit(main())
