// Markdown to safe DOM. Model output is untrusted, so everything goes through
// DOMPurify, and the page's CSP blocks scripts and remote images on top of that.

import DOMPurify from 'dompurify';
import { marked } from 'marked';

DOMPurify.addHook('afterSanitizeAttributes', (node) => {
  if (node.tagName === 'A') {
    node.setAttribute('target', '_blank');
    node.setAttribute('rel', 'noopener noreferrer');
  }
});

export function renderMarkdown(target: HTMLElement, text: string, onCopy: (code: string) => void): void {
  const html = marked.parse(text, { async: false, gfm: true, breaks: true });
  target.innerHTML = DOMPurify.sanitize(html, { FORBID_TAGS: ['style', 'form', 'input', 'button', 'iframe'], FORBID_ATTR: ['style'] });

  for (const table of target.querySelectorAll('table')) {
    const wrap = document.createElement('div');
    wrap.className = 'table-wrap';
    table.replaceWith(wrap);
    wrap.append(table);
  }

  for (const pre of target.querySelectorAll('pre')) {
    const code = pre.querySelector('code');
    const language = code?.className.match(/language-([\w+#.-]+)/)?.[1] ?? 'code';
    const block = document.createElement('div');
    block.className = 'code-block';
    const head = document.createElement('div');
    head.className = 'code-head';
    const label = document.createElement('span');
    label.textContent = language;
    const copy = document.createElement('button');
    copy.type = 'button';
    copy.className = 'text-btn';
    copy.textContent = 'Copy';
    copy.addEventListener('click', () => {
      onCopy(code?.textContent ?? pre.textContent ?? '');
      copy.textContent = 'Copied';
      setTimeout(() => (copy.textContent = 'Copy'), 1500);
    });
    head.append(label, copy);
    pre.replaceWith(block);
    block.append(head, pre);
  }
}
