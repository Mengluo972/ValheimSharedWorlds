// 极简 ZIP 打包器（无第三方依赖）：把目录内容打成 zip，条目路径统一使用正斜杠，
// 符合 Thunderstore 对 BepInEx 路由（plugins/...）的要求。
// 用法：node zip.js <sourceDir> <outZip>
const fs = require("fs");
const path = require("path");
const zlib = require("zlib");

const src = process.argv[2];
const out = process.argv[3];
if (!src || !out) { console.error("usage: node zip.js <sourceDir> <outZip>"); process.exit(2); }

function crc32(buf) {
  let table = crc32.t;
  if (!table) {
    table = crc32.t = [];
    for (let n = 0; n < 256; n++) { let c = n; for (let k = 0; k < 8; k++) c = c & 1 ? 0xedb88320 ^ (c >>> 1) : c >>> 1; table[n] = c >>> 0; }
  }
  let crc = 0xffffffff;
  for (let i = 0; i < buf.length; i++) crc = table[(crc ^ buf[i]) & 0xff] ^ (crc >>> 8);
  return (crc ^ 0xffffffff) >>> 0;
}

function walk(dir, base = "") {
  const out = [];
  for (const name of fs.readdirSync(dir)) {
    if (name.startsWith(".")) continue; // 跳过隐藏文件
    const full = path.join(dir, name);
    const rel = base ? base + "/" + name : name;
    const st = fs.statSync(full);
    if (st.isDirectory()) out.push(...walk(full, rel));
    else out.push({ full, rel });
  }
  return out;
}

const files = walk(src);
const locals = [];
const central = [];
let offset = 0;

for (const f of files) {
  const data = fs.readFileSync(f.full);
  const crc = crc32(data);
  const deflated = zlib.deflateRawSync(data, { level: 9 });
  const useDeflate = deflated.length < data.length;
  const stored = useDeflate ? deflated : data;
  const method = useDeflate ? 8 : 0;
  const nameBuf = Buffer.from(f.rel, "utf8");
  const mtime = fs.statSync(f.full).mtime;
  const dosTime = ((mtime.getHours() << 11) | (mtime.getMinutes() << 5) | (mtime.getSeconds() >> 1)) & 0xffff;
  const dosDate = (((mtime.getFullYear() - 1980) << 9) | ((mtime.getMonth() + 1) << 5) | mtime.getDate()) & 0xffff;

  const lh = Buffer.alloc(30);
  lh.writeUInt32LE(0x04034b50, 0);
  lh.writeUInt16LE(20, 4);            // version needed
  lh.writeUInt16LE(0, 6);             // flags
  lh.writeUInt16LE(method, 8);
  lh.writeUInt16LE(dosTime, 10);
  lh.writeUInt16LE(dosDate, 12);
  lh.writeUInt32LE(crc, 14);
  lh.writeUInt32LE(stored.length, 18);
  lh.writeUInt32LE(data.length, 22);
  lh.writeUInt16LE(nameBuf.length, 26);
  lh.writeUInt16LE(0, 28);            // extra len
  locals.push(lh, nameBuf, stored);

  const ch = Buffer.alloc(46);
  ch.writeUInt32LE(0x02014b50, 0);
  ch.writeUInt16LE(20, 4);            // version made by
  ch.writeUInt16LE(20, 6);            // version needed
  ch.writeUInt16LE(0, 8);
  ch.writeUInt16LE(method, 10);
  ch.writeUInt16LE(dosTime, 12);
  ch.writeUInt16LE(dosDate, 14);
  ch.writeUInt32LE(crc, 16);
  ch.writeUInt32LE(stored.length, 20);
  ch.writeUInt32LE(data.length, 24);
  ch.writeUInt16LE(nameBuf.length, 28);
  ch.writeUInt16LE(0, 30);            // extra
  ch.writeUInt16LE(0, 32);            // comment
  ch.writeUInt16LE(0, 34);            // disk
  ch.writeUInt16LE(0, 36);            // internal attrs
  ch.writeUInt32LE(0, 38);            // external attrs
  ch.writeUInt32LE(offset, 42);
  central.push(ch, nameBuf);

  offset += lh.length + nameBuf.length + stored.length;
}

const centralBuf = Buffer.concat(central);
const localBuf = Buffer.concat(locals);
const eocd = Buffer.alloc(22);
eocd.writeUInt32LE(0x06054b50, 0);
eocd.writeUInt16LE(0, 4);
eocd.writeUInt16LE(0, 6);
eocd.writeUInt16LE(files.length, 8);
eocd.writeUInt16LE(files.length, 10);
eocd.writeUInt32LE(centralBuf.length, 12);
eocd.writeUInt32LE(localBuf.length, 16);
eocd.writeUInt16LE(0, 20);

fs.writeFileSync(out, Buffer.concat([localBuf, centralBuf, eocd]));
console.log(`wrote ${out} (${files.length} files)`);
for (const f of files) console.log("  " + f.rel);
