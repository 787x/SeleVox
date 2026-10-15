import assert from 'node:assert/strict';
import {execFileSync, spawnSync} from 'node:child_process';
import {mkdtempSync, mkdirSync, writeFileSync, renameSync, rmSync} from 'node:fs';
import os from 'node:os';
import path from 'node:path';
import {fileURLToPath} from 'node:url';
import test from 'node:test';

const checker = fileURLToPath(new URL('./check.mjs', import.meta.url));
test('offline checker covers all tracked sources and recovers after failures', () => {
  const root = mkdtempSync(path.join(os.tmpdir(), 'selevox-docs-links-'));
  const git = (...args) => execFileSync('git', args, {cwd: root, stdio: 'pipe'});
  const write = (name, content) => {
    mkdirSync(path.dirname(path.join(root, name)), {recursive: true});
    writeFileSync(path.join(root, name), content);
  };
  const run = () => {
    const result = spawnSync(process.execPath, [checker, root], {encoding: 'utf8'});
    return {status: result.status, output: result.stdout + result.stderr};
  };
  const pass = () => {
    const result = run();
    assert.equal(result.status, 0, result.output);
  };
  const fail = (...messages) => {
    const result = run();
    assert.equal(result.status, 1, result.output);
    for (const message of messages) assert.ok(result.output.includes(message), result.output);
  };
  try {
    git('init', '--quiet');
    const source = [
      '# Source',
      '[same](#source)',
      '[relative](../target.md#target)',
      '[encoded](../%E4%B8%AD%E6%96%87%20%E6%96%87%E4%BB%B6.md#%E4%B8%AD%E6%96%87%E6%A0%87%E9%A2%98)',
      '[spaces](<../中文 文件.md#中文标题>)',
      '![image](../image%20space.svg)',
      '[ref][destination]',
      '[destination]: ../target.md#target-1',
      '[directory](../guide/)',
      '[directory heading](../guide/#guide-heading)',
      '[dot directory](./#source-directory)',
      '[directory without readme](../empty/)',
      '[external](https://example.invalid/missing) <mailto:nobody@example.invalid>',
      '[repo URL](https://github.com/787x/SeleVox/blob/main/no-such-file.md#nope)',
      '[special](codex://missing) [network path](//example.invalid/missing)',
      '`[inline](missing.md)`',
      '```markdown',
      '[fenced](missing.md) ![fenced image](missing.png)',
      '```',
      '    [indented](missing.md)',
      ''
    ].join('\n');
    write('nested/source.md', source);
    write('nested/README.md', '# Source directory\n');
    write('target.md', '# Target\n# Target\n');
    write('中文 文件.md', '# 中文标题\n');
    write('image space.svg', '<svg xmlns="http://www.w3.org/2000/svg"/>');
    write('guide/README.md', '# Guide heading\n');
    write('empty/.gitkeep', '');
    write('.hidden/extra.MD', '[hidden](../target.md#target)\n');
    write('other/extra.markdown', '[other](../target.md)\n');
    git('add', '.');
    // Untracked Markdown must not be included.
    write('untracked.md', '[untracked](absent.md)\n');
    pass();

    write('nested/source.md', source + '[bad file](./absent.md)\n![bad image](gone.png)\n');
    fail('nested', 'source.md:22:', './absent.md', 'gone.png', 'Cannot find file');
    write('nested/source.md', source.replace('../target.md#target)', '../target.md#absent-heading)'));
    fail('source.md:3:', '#absent-heading', 'target.md', 'Cannot find heading');
    write('nested/source.md', source);
    pass();

    // Deletion without staging must also fail, never supply phantom landmarks.
    rmSync(path.join(root, 'target.md'));
    fail('target.md', 'Cannot find file');
    write('target.md', '# Target\n# Target\n');
    pass();

    // Move a target in the Git index; sources are unchanged and still checked.
    renameSync(path.join(root, 'target.md'), path.join(root, 'moved.md'));
    git('add', '-A', 'target.md', 'moved.md');
    fail('source.md:3:', 'extra.MD:1:', 'extra.markdown:1:', 'target.md');
    renameSync(path.join(root, 'moved.md'), path.join(root, 'target.md'));
    git('add', '-A', 'target.md', 'moved.md');
    pass();

    write('nested/source.md', source + '[bad directory heading](../guide/#absent)\n');
    fail('#absent', 'Cannot find heading');
    write('nested/source.md', source);
    pass();
  } finally {
    rmSync(root, {recursive: true, force: true});
  }
});
