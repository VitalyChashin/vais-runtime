// Copyright (c) 2026 VAIS contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the project root for license information.

import * as fs from 'fs'
import * as path from 'path'
import * as zlib from 'zlib'

const EXCLUDED_DIRS = new Set(['.venv', '__pycache__', '.git'])
const EXCLUDED_EXTS = new Set(['.pyc', '.pyo'])

function collectFiles(dir: string, parentDir: string, out: Array<{ abs: string; rel: string }>): void {
  for (const entry of fs.readdirSync(dir, { withFileTypes: true })) {
    if (entry.isDirectory()) {
      if (!EXCLUDED_DIRS.has(entry.name)) {
        collectFiles(path.join(dir, entry.name), parentDir, out)
      }
    } else if (entry.isFile() && !EXCLUDED_EXTS.has(path.extname(entry.name))) {
      const abs = path.join(dir, entry.name)
      // rel becomes "src/foo.py" when sourceDir is the "src" child of parentDir
      const rel = path.relative(parentDir, abs).replace(/\\/g, '/')
      out.push({ abs, rel })
    }
  }
}

function writeHeader(entryName: string, size: number, mtimeSec: number): Buffer {
  const buf = Buffer.alloc(512)

  // ustar long-name: split into prefix (<=155) + name (<=99)
  let name = entryName
  let prefix = ''
  if (name.length > 99) {
    const slash = name.lastIndexOf('/', 154)
    if (slash > 0) {
      prefix = name.slice(0, slash)
      name = name.slice(slash + 1)
    } else {
      name = name.slice(0, 99)
    }
  }

  buf.write(name, 0, 'ascii')
  buf.write('0000644\0', 100, 'ascii')                                              // mode
  buf.write('0000000\0', 108, 'ascii')                                              // uid
  buf.write('0000000\0', 116, 'ascii')                                              // gid
  buf.write(size.toString(8).padStart(11, '0') + '\0', 124, 'ascii')               // size
  buf.write(mtimeSec.toString(8).padStart(11, '0') + '\0', 136, 'ascii')           // mtime
  buf.fill(0x20, 148, 156)                                                          // checksum placeholder (8 spaces)
  buf[156] = 0x30                                                                   // type '0' = regular file
  buf.write('ustar\0', 257, 'ascii')                                                // magic
  buf.write('00', 263, 'ascii')                                                     // version
  if (prefix) buf.write(prefix.slice(0, 154), 345, 'ascii')

  // Compute and write checksum (sum of all 512 bytes with placeholder spaces)
  let cksum = 0
  for (let i = 0; i < 512; i++) cksum += buf[i]
  buf.write(cksum.toString(8).padStart(6, '0') + '\0 ', 148, 'ascii')

  return buf
}

/** Packs sourceDir into a gzip tar. Entry paths include the directory name, e.g. "src/foo.py". */
export function packSourceDir(sourceDir: string): Buffer {
  const parentDir = path.dirname(sourceDir)
  const files: Array<{ abs: string; rel: string }> = []
  collectFiles(sourceDir, parentDir, files)

  const parts: Buffer[] = []
  for (const { abs, rel } of files) {
    const content = fs.readFileSync(abs)
    const mtime = Math.floor(fs.statSync(abs).mtimeMs / 1000)
    parts.push(writeHeader(rel, content.length, mtime))
    parts.push(content)
    const pad = (512 - (content.length % 512)) % 512
    if (pad > 0) parts.push(Buffer.alloc(pad))
  }
  parts.push(Buffer.alloc(1024)) // end-of-archive: two zero blocks

  return zlib.gzipSync(Buffer.concat(parts))
}
