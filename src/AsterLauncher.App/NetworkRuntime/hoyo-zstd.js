'use strict';
const { zstdDecompressSync } = require('node:zlib');
const expected = Number(process.argv[2]);
if (!Number.isSafeInteger(expected) || expected < 0 || expected > 64 * 1024 * 1024) process.exit(1);
let count = 0; const parts = [];
process.stdin.on('data', data => {
  count += data.length;
  if (count > 64 * 1024 * 1024) process.exit(1);
  parts.push(data);
});
process.stdin.on('end', () => {
  try {
    const out = zstdDecompressSync(Buffer.concat(parts), { maxOutputLength: Math.max(1, expected) });
    if (out.length !== expected) process.exit(1);
    process.stdout.write(out);
  } catch { process.exitCode = 1; }
});