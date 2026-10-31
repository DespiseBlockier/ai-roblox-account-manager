// Compiled into the application. No scripts are downloaded from a solver service.
const solverOutbox = [], solverPending = new Map();
const accountOutbox = [], accountPending = new Map();
const signupOutbox = [], signupPending = new Map();
let signupSequence = 0, signupFailed = false;
const requestCreateAccount = options => new Promise((resolve, reject) => {
  if (abort.signal.aborted) { reject(new Error('Stopped')); return; }
  if (identity !== null) { reject(new Error('Open a guest browser with no account selected to create an account.')); return; }
  if (signupFailed) { reject(new Error('A signup failed. Stop this run and check its outcome before trying again.')); return; }
  if (signupPending.size) { reject(new Error('Wait for the current signup to finish before creating another account.')); return; }
  if (!options || typeof options !== 'object' || JSON.stringify(options).length > 12000) { reject(new Error('Invalid signup options.')); return; }
  const copy = JSON.parse(JSON.stringify(options)); copy.id = ++signupSequence;
  signupPending.set(copy.id, {resolve, reject}); signupOutbox.push(copy);
});
let accountSequence = 0;
const requestAddAccount = password => new Promise((resolve, reject) => {
  if (abort.signal.aborted) { reject(new Error('Stopped')); return; }
  if (password != null && (typeof password !== 'string' || !password.length || password.length > 1024 || /[\x00-\x1f\x7f]/.test(password))) { reject(new Error('Supply a password of 1–1024 characters, or omit it.')); return; }
  if (accountPending.size) { reject(new Error('An account save is already running.')); return; }
  const id = ++accountSequence;
  accountPending.set(id, {resolve, reject}); accountOutbox.push({id, password:password ?? null});
});
let solverSequence = 0;
const solverRequest = (provider, action, apiKey, task) => new Promise((resolve, reject) => {
  if (abort.signal.aborted) { reject(new Error('Stopped')); return; }
  if (solverPending.size >= 4) { reject(new Error('At most four solver requests may run at once.')); return; }
  const id = ++solverSequence;
  solverPending.set(id, {resolve, reject});
  solverOutbox.push({id, provider, action, apiKey, task});
});
abort.signal.addEventListener('abort', () => {
  for (const call of solverPending.values()) call.reject(new Error('Stopped'));
  solverPending.clear(); solverOutbox.length = 0;
  for (const call of accountPending.values()) call.reject(new Error('Stopped'));
  accountPending.clear(); accountOutbox.length = 0;
  for (const call of signupPending.values()) call.reject(new Error('Stopped'));
  signupPending.clear(); signupOutbox.length = 0;
}, {once:true});
const registerSolver = (provider, apiKey, options = {}) => {
  if (typeof apiKey !== 'string' || !apiKey.length || apiKey.length > 512 || /[\x00-\x1f\x7f]/.test(apiKey)) throw new Error('Enter a valid solver API key.');
  const config = JSON.parse(JSON.stringify(options));
  return Object.freeze({
    status: async () => Boolean(await solverRequest(provider, 'status', apiKey, null)),
    solvecaptcha: async captcha => {
      if (!captcha || typeof captcha !== 'object') return {success:false, token:null, solution:null, errorCode:'CAPTCHA_NOT_FOUND', message:'No readable CAPTCHA was found.'};
      const task = Object.assign({}, config, captcha);
      if (task.kind === 'funcaptcha') {
        const hasProxy = ['proxyType','proxyAddress','proxyPort','proxyLogin','proxyPassword'].some(name => Object.hasOwn(task, name));
        task.type = provider === 'solvex' || hasProxy ? 'FunCaptchaTask' : 'FunCaptchaTaskProxyless';
        if (typeof task.data === 'string') {
          if (provider === 'solvex') {
            try { const value = JSON.parse(task.data); if (typeof value.blob === 'string') task.data = value.blob; } catch { }
          } else {
            try { JSON.parse(task.data); } catch { task.data = JSON.stringify({blob:task.data}); }
          }
        }
      } else if (task.kind) {
        return {success:false, token:null, solution:null, errorCode:'UNSUPPORTED_CAPTCHA_TYPE', message:'These solver helpers currently support FunCaptcha only.'};
      }
      delete task.kind;
      if (JSON.stringify(task).length > 64000) throw new Error('CAPTCHA task is too large.');
      return await solverRequest(provider, 'solve', apiKey, task);
    }
  });
};
const fetchCaptcha = () => {
  const websiteURL = location.origin + location.pathname;
  for (const [selector,kind] of [['.g-recaptcha[data-sitekey]','recaptcha-v2'],['.cf-turnstile[data-sitekey]','turnstile']]) {
    const element = document.querySelector(selector);
    if (element) {
      const result = {kind, websiteURL, websiteKey:element.getAttribute('data-sitekey')};
      if (kind === 'turnstile') {
        const metadata = {};
        if (element.hasAttribute('data-action')) metadata.action = element.getAttribute('data-action');
        if (element.hasAttribute('data-cdata')) metadata.cdata = element.getAttribute('data-cdata');
        if (Object.keys(metadata).length) result.metadata = metadata;
      }
      return result;
    }
  }
  for (const frame of document.querySelectorAll('iframe[src]')) {
    let url; try { url = new URL(frame.src, location.href); } catch { continue; }
    if (url.protocol !== 'https:') continue;
    if ((url.hostname === 'www.google.com' || url.hostname.endsWith('.recaptcha.net')) && url.pathname.includes('/recaptcha/')) {
      const key = url.searchParams.get('k'); if (key) return {kind:'recaptcha-v2', websiteURL, websiteKey:key};
    }
    if (url.hostname.endsWith('.arkoselabs.com') || url.hostname === 'arkoselabs.roblox.com' || (['www.roblox.com','roblox.com'].includes(url.hostname) && /arkose|captcha/i.test(url.pathname))) {
      const key = url.searchParams.get('public_key') || url.searchParams.get('publicKey') || url.searchParams.get('pk') || url.searchParams.get('pkey') || /\/v2\/([a-f0-9-]{36})\//i.exec(url.pathname)?.[1];
      if (key) {
        const result = {kind:'funcaptcha', websiteURL, websitePublicKey:key};
        const blob = url.searchParams.get('data[blob]') || url.searchParams.get('blob') || url.searchParams.get('dataExchangeBlob');
        if (blob) result.data = JSON.stringify({blob});
        return result;
      }
    }
  }
  return null; // Cross-origin challenges without readable configuration must be handled manually.
};
