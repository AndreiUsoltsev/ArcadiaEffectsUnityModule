// dumps particle state per emitter per time node, same sampling as tools/simhash.mjs
import fs from 'fs'; import path from 'path'; import vm from 'vm';
const ROOT = process.argv[2]; const OUT = process.argv[3];
globalThis.window = globalThis;
for (const f of ['utils','track','curve','path','sprites','model','sim'])
  vm.runInThisContext(fs.readFileSync(path.join(ROOT,'js','core',f+'.js'),'utf8'),{filename:f});
const AFX = globalThis.AFX;
const files = fs.readdirSync(path.join(ROOT,'library')).filter(f=>f.endsWith('.json'));
const out = [];
for (const f of files) {
  const raw = JSON.parse(fs.readFileSync(path.join(ROOT,'library',f),'utf8'));
  const doc = AFX.Model.migrate(raw.doc || raw);
  const fidx = AFX.Model.forceIndex(doc);
  doc.layers.forEach((l, li) => {
    if (l.type !== 'emitter') return;
    const sim = new AFX.Sim(l); const fc = fidx ? fidx.get(l.id) : null;
    const dur = Math.max(0.05, (l.end - l.start) + (l.pt.life || 1) * 1.6);
    for (let s = 1; s <= 24; s++) {
      sim.ensure(l, dur * s / 24, fc);
      const a = [];
      for (const p of sim.parts) a.push(p.x, p.y, p.vx, p.vy, p.age, p.life, p.size0, p.rot, p.tier, p.gen, p.vi, p.hist ? p.hist.length : -1);
      out.push(f + '|' + li + '|' + s + '|' + sim.parts.length + '|' + a.join(','));
    }
  });
}
fs.writeFileSync(OUT, out.join('\n'));
