import {execFileSync} from 'node:child_process';
import {readdir, stat} from 'node:fs/promises';
import path from 'node:path';
import {fileURLToPath} from 'node:url';
import {remark} from 'remark';
import remarkGfm from 'remark-gfm';
import validateLinks from 'remark-validate-links';
import {engine} from 'unified-engine';
import {visit} from 'unist-util-visit';
import {VFile} from 'vfile';

// No shell, glob expansion, diff filtering, or ignore files: every tracked Markdown
// source is checked, even in hidden directories or when a target alone changed.
const root = process.argv[2] ? path.resolve(process.argv[2]) : fileURLToPath(new URL('../../', import.meta.url));
const tracked = execFileSync('git', ['ls-files', '-z'], {cwd: root, encoding: 'utf8'}).split('\0');
const markdown = tracked.filter(file => /\.(md|markdown|mdown|mkdn|mkd|mkdown)$/i.test(file));
if (markdown.length === 0) throw new Error('No tracked Markdown files found');

// The upstream validator decodes paths but not fragments. Decode fragments in
// the parsed tree only; percent-encoded Chinese headings then use the same IDs.
function normalizeLocalLinks() {
  return async (tree, file) => {
    const pending = [];
    visit(tree, node => {
      if (!('url' in node) || /^[a-z][a-z\d+.-]*:|^\/\//i.test(node.url)) return;
      const hash = node.url.indexOf('#');
      if (hash < 0) return;
      try {
        node.url = node.url.slice(0, hash + 1) + decodeURIComponent(node.url.slice(hash + 1));
        // Upstream keeps a trailing separator before # when resolving directory
        // links. Resolve these to the README GitHub renders, avoiding a false
        // missing-heading warning on Windows (directory\\ vs directory).
        const pathname = node.url.slice(0, hash).split('?')[0];
        if (pathname && !pathname.startsWith('/')) pending.push((async () => {
          const target = path.resolve(path.dirname(path.resolve(file.cwd, file.path)), decodeURIComponent(pathname));
          if (!(await stat(target)).isDirectory()) return;
          const readme = (await readdir(target)).sort().find(name => /^readme\.(md|markdown|mdown|mkdn)$/i.test(name));
          if (readme) node.url = pathname.replace(/\/$/, '') + '/' + encodeURIComponent(readme) + node.url.slice(hash);
        })().catch(error => {
          // The validator reports absent targets; other filesystem errors must fail.
          if (!['ENOENT', 'ENOTDIR'].includes(error.code)) throw error;
        }));
      } catch {
        file.message(`Invalid URL encoding in link: ${node.url}`, node.position);
      }
    });
    await Promise.all(pending);
  };
}

// Include the original link text alongside upstream's resolved missing target.
function report(files) {
  const lines = [];
  for (const file of files) {
    for (const message of file.messages) {
      const start = message.place?.start;
      const end = message.place?.end;
      const link = start?.offset !== undefined && end?.offset !== undefined
        ? String(file).slice(start.offset, end.offset).replace(/\r?\n/g, ' ')
        : '';
      lines.push(`${path.relative(root, path.resolve(file.cwd, file.path))}:${message.line ?? 1}:${message.column ?? 1}: ${message.reason}${link ? ` | ${link}` : ''}`);
    }
  }
  return lines.length ? lines.join('\n') + '\n' : '';
}

engine({
  cwd: root,
  processor: remark,
  files: markdown.map(file => new VFile({cwd: root, path: path.resolve(root, file)})),
  plugins: [remarkGfm, normalizeLocalLinks, [validateLinks, {
    root,
    repository: false, // Ignore all external URLs, including this repository's.
    urlConfig: {headingPrefix: '#', topAnchor: '#readme'}
  }]],
  detectConfig: false,
  detectIgnore: false,
  frail: true,
  out: false,
  reporter: report
}, (error, code) => {
  if (error) {
    console.error(error);
    process.exitCode = 1;
  } else {
    process.exitCode = code;
    if (!code) console.log(`Markdown links OK (${markdown.length} tracked files).`);
  }
});
