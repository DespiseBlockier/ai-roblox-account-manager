// Local signup instrumentation. Registration itself is submitted by Roblox's visible form.
(() => {
  const trusted = location.protocol === 'https:' && (!location.port || location.port === '443') &&
    ['www.roblox.com', 'roblox.com', 'arkoselabs.roblox.com'].includes(location.hostname);
  if (!trusted) return;
  const key = SIGNUP_KEY;
  if (window[key]) return;
  let form = null, submitted = false, entryClicked = false, filledAt = 0;
  const captures = [], wrapped = new WeakSet();
  const visible = e => e && e.getClientRects().length && getComputedStyle(e).visibility !== 'hidden';
  const set = (element, value) => {
    const prototype = element instanceof HTMLSelectElement ? HTMLSelectElement.prototype : HTMLInputElement.prototype;
    const setter = Object.getOwnPropertyDescriptor(prototype, 'value')?.set;
    if (!setter) return false;
    if (element.value !== value) {
      setter.call(element, value);
      element.dispatchEvent(new Event('input', {bubbles:true}));
      element.dispatchEvent(new Event('change', {bubbles:true}));
    }
    return element.value === value;
  };
  const option = (element, number, isMonth = false) => {
    if (!(element instanceof HTMLSelectElement)) return null;
    const month = ['jan','feb','mar','apr','may','jun','jul','aug','sep','oct','nov','dec'][number-1];
    return [...element.options].find(o => o.value.trim() && (Number(o.value) === number || isMonth && (o.value.toLowerCase().slice(0,3) === month || o.textContent.trim().toLowerCase().slice(0,3) === month)))?.value ?? null;
  };
  const fill = values => {
    if (location.hostname !== 'www.roblox.com' && location.hostname !== 'roblox.com') return {phase:'waiting'};
    const username = document.querySelector('#signup-username');
    const password = document.querySelector('#signup-password');
    const month = document.querySelector('#MonthDropdown');
    const day = document.querySelector('#DayDropdown');
    const year = document.querySelector('#YearDropdown');
    const button = document.querySelector('#signup-button');
    if (username?.value === values.username && filledAt && performance.now() - filledAt >= 1000) {
      const ids = ['signup-usernameInputValidation', ...(username.getAttribute('aria-describedby') || '').split(/\s+/)];
      const errors = ids.map(id => document.getElementById(id)).filter(e => visible(e) && e !== username);
      const text = errors.map(e => e.textContent || '').join(' ').trim().toLowerCase();
      if (/already.*(?:in use|taken)|username.*taken/.test(text)) return {phase:'username-taken'};
      if (/not appropriate|inappropriate|not allowed/.test(text)) return {phase:'username-inappropriate'};
      if (text && username.getAttribute('aria-invalid') === 'true') return {phase:'username-rejected'};
    }
    if (submitted) return {phase:'submitted'};
    if (!(username instanceof HTMLInputElement) || !(password instanceof HTMLInputElement) || password.type !== 'password' ||
        !(button instanceof HTMLButtonElement) || ![username,password,month,day,year,button].every(visible)) {
      if (!entryClicked) {
        const entry = [...document.querySelectorAll('a[href],button')].find(e => visible(e) &&
          (/^create\s+(?:an?\s+)?account$/i.test(e.textContent.trim()) || /^sign\s*up$/i.test(e.textContent.trim())) && e.id !== 'signup-button');
        if (entry) { entryClicked = true; entry.click(); return {phase:'opening'}; }
      }
      return {phase:'waiting'};
    }
    if (form !== button) {
      form = button;
      button.addEventListener('click', () => { submitted = true; }, {capture:true, once:true});
    }
    const [m,y] = [option(month, values.month, true), option(year, values.year)];
    if (m === null || y === null) return {phase:'review'};
    set(year,y); set(month,m);
    const d = option(day, values.day);
    if (d === null) return {phase:'review'};
    // React controls receive their normal input/change events. Never submit via fetch here.
    if (![set(month,m),set(day,d),set(year,y),set(username,values.username),set(password,values.password)].every(Boolean)) return {phase:'review'};
    if (!filledAt) { filledAt = performance.now(); return {phase:'validating'}; }
    if (performance.now() - filledAt < 1000) return {phase:'validating'};
    if (button.disabled || button.getAttribute('aria-disabled') === 'true') return {phase:'review'};
    submitted = true; button.click();
    return {phase:'submitted'};
  };
  const inspect = script => {
    if (!(script instanceof HTMLScriptElement) || wrapped.has(script)) return;
    const callback = script.getAttribute('data-callback');
    if (!callback || !/^[A-Za-z_$][\w$]{0,150}$/.test(callback) || typeof window[callback] !== 'function') return;
    let url; try { url = new URL(script.src, location.href); } catch { return; }
    if (url.protocol !== 'https:' || !/(^|\.)arkoselabs\.com$/.test(url.hostname) && url.hostname !== 'arkoselabs.roblox.com') return;
    const publicKey = /\/v2\/([a-f0-9-]{36})\/api\.js/i.exec(url.pathname)?.[1];
    if (!publicKey) return;
    wrapped.add(script);
    const setup = window[callback];
    window[callback] = function(enforcement) {
      if (enforcement && typeof enforcement.setConfig === 'function') {
        const configure = enforcement.setConfig;
        enforcement.setConfig = function(config) {
          if (config && typeof config.onCompleted === 'function' && typeof config.data?.blob === 'string') {
            const configuredKey = typeof config.publicKey === 'string' && /^[a-f0-9-]{36}$/i.test(config.publicKey) ? config.publicKey : publicKey;
            const record = {publicKey:configuredKey, blob:config.data.blob, domain:url.hostname, complete:config.onCompleted.bind(config), applied:false, completed:false, shown:false, failed:false};
            if (captures.length < 8) captures.push(record);
            const wrap = (name, update) => {
              const original = config[name];
              return function() { update(); if (typeof original === 'function') return original.apply(this, arguments); };
            };
            config = {...config,
              onShown:wrap('onShown', () => { record.shown = true; }),
              onHide:wrap('onHide', () => { record.shown = false; }),
              onError:wrap('onError', () => { record.failed = true; }),
              onFailed:wrap('onFailed', () => { record.failed = true; }),
              onReset:wrap('onReset', () => { record.completed = true; }),
              onCompleted:wrap('onCompleted', () => { record.completed = true; })
            };
          }
          return configure.call(this, config);
        };
      }
      return setup.apply(this, arguments);
    };
  };
  const scan = () => document.querySelectorAll('script[data-callback][src]').forEach(inspect);
  const observer = new MutationObserver(scan); observer.observe(document, {childList:true, subtree:true}); scan();
  const challenge = () => {
    scan(); const index = captures.findIndex(c => c.shown && !c.failed && !c.applied && !c.completed);
    if (index < 0) return null;
    const value = captures[index];
    return {index, websitePublicKey:value.publicKey, blob:value.blob, funcaptchaApiJSSubdomain:value.domain};
  };
  const apply = (index, token) => {
    const value = captures[index];
    if (!value || !value.shown || value.failed || value.applied || value.completed || typeof token !== 'string' || !token.length || token.length > 20000) return false;
    value.applied = true; value.complete({token}); return true;
  };
  Object.defineProperty(window, key, {value:Object.freeze({fill, challenge, apply}), configurable:true, enumerable:false});
})();
