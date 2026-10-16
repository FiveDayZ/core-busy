const { test } = require('node:test');
const assert = require('node:assert/strict');
const { readFileSync } = require('node:fs');
const { join } = require('node:path');
const vm = require('node:vm');

// 执行看板自身的渲染函数，不复制转义实现，不依赖浏览器或第三方 DOM 库。
const html = readFileSync(join(__dirname, '../hub/static/index.html'), 'utf8');
const functions = html.slice(html.indexOf('function esc(s)'), html.indexOf('async function api('));
const context = vm.createContext({});
vm.runInContext(html.match(/^const STAGES = .*;$/m)[0] + '\n' + functions, context);

test('device-supplied core name cannot escape the title attribute', () => {
  const name = '"oncut=alert(1)';
  const markup = vm.runInContext(`gradesBadges(${JSON.stringify([{ name, stage: 0, level: 1 }])})`, context);
  assert.match(markup, /^<span class="badge b0" title="[^"<>]*">[^<>]*<\/span>$/);
  assert.ok(markup.includes('&quot;oncut=alert(1)'));
});

test('HTML tags, both quote types and ampersands remain plain text', () => {
  for (const input of ['<img src=x onerror=alert(1)>', '" onclick="alert(1)', "' onclick='alert(1)", '&quot;']) {
    const escaped = vm.runInContext(`esc(${JSON.stringify(input)})`, context);
    assert.doesNotMatch(escaped, /[<>"']/);
    assert.notEqual(escaped, input);
  }
  assert.equal(vm.runInContext('esc(null)', context), '-');
  assert.equal(vm.runInContext('esc("C0")', context), 'C0');
});

test('empty, unscored and successful grade rendering stays available', () => {
  assert.equal(vm.runInContext('gradesBadges([])', context), '<span class="dim">-</span>');
  const unscored = vm.runInContext('gradesBadges([{name:"C0",stage:0,level:1}])', context);
  assert.ok(unscored.includes('未判定') && unscored.includes('Lv1'));
  const scored = vm.runInContext('gradesBadges([{name:"C1",stage:2,level:30,score:98.5}])', context);
  assert.ok(scored.includes('成熟期') && scored.includes('健康 99') && scored.includes('Lv30'));
});
