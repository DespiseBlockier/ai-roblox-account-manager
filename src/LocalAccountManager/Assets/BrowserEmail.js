// Observes only Roblox's top-level login and email 2FA UI. No mailbox credentials enter this page.
(() => {
  if (location.protocol !== 'https:' || location.hostname !== 'www.roblox.com' ||
      (location.port && location.port !== '443') || window.top !== window) return;
  const key = EMAIL_KEY;
  if (window[key]) return;
  const documentId = crypto.randomUUID();
  let username = EXPECTED_USERNAME, field = null, challengeId = 0, filled = false;
  const visible = e => e && e.getClientRects().length && getComputedStyle(e).visibility !== 'hidden';
  const capture = () => {
    if (location.pathname.replace(/\/$/, '').toLowerCase() !== '/login') return;
    const input = document.querySelector('#login-username');
    if (input instanceof HTMLInputElement) username = /^[A-Za-z0-9_]{3,20}$/.test(input.value) ? input.value : '';
  };
  document.addEventListener('click', event => { if (event.target?.closest?.('#login-button')) capture(); }, true);
  document.addEventListener('submit', capture, true);
  const prompt = () => {
    const input = document.querySelector('#two-step-verification-code-input');
    if (!(input instanceof HTMLInputElement) || !['text','tel','password'].includes(input.type) || !visible(input) || input.disabled || input.maxLength !== 6) return null;
    const root = input.closest('[role="dialog"],.modal-content,.inline-challenge-container') || input.parentElement?.parentElement;
    if (!root) return null;
    const paragraphs = [...root.querySelectorAll('p,label')].filter(visible).map(e => e.textContent).join(' ');
    if (!/code/i.test(paragraphs) || !/email|e-mail|@/i.test(paragraphs) || /authenticator|authentication app|text message|\bsms\b|recovery code|backup code|security key|passkey/i.test(paragraphs)) return null;
    return {input,root};
  };
  const snapshot = () => {
    const current = prompt();
    if (!current) { field = null; return {documentId, username, kind:'none'}; }
    if (field !== current.input) { field = current.input; challengeId++; filled = false; }
    return {documentId,username,kind:'email2fa',id:challengeId,filled};
  };
  const fill = (id, code, context) => {
    const state = snapshot(), current = prompt();
    if (context !== documentId || !current || state.id !== id || filled || !/^[0-9]{6}$/.test(code)) return false;
    Object.getOwnPropertyDescriptor(HTMLInputElement.prototype,'value').set.call(current.input,code);
    current.input.dispatchEvent(new Event('input',{bubbles:true})); current.input.dispatchEvent(new Event('change',{bubbles:true}));
    filled = true; return current.input.value === code;
  };
  const submit = (id, code, context) => {
    const state = snapshot(), current = prompt();
    if (context !== documentId || !current || state.id !== id || !filled || current.input.value !== code) return false;
    const button = [...current.root.querySelectorAll('button')].find(e => visible(e) && !e.disabled && e.getAttribute('aria-disabled') !== 'true' && /^verify$/i.test(e.textContent.trim()));
    if (!button) return false;
    button.click(); return true;
  };
  Object.defineProperty(window,key,{value:Object.freeze({snapshot,fill,submit}),enumerable:false,configurable:true});
})();
