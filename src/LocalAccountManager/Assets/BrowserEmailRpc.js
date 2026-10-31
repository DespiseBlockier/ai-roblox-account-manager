const emailOutbox = [], emailPending = new Map();
let emailSequence = 0;
const requestEmail = (action, options = {}) => new Promise((resolve,reject) => {
  if (abort.signal.aborted) { reject(new Error('Stopped')); return; }
  if (emailPending.size) { reject(new Error('Wait for the current email operation to finish.')); return; }
  if (!options || typeof options !== 'object' || JSON.stringify(options).length > 4096) { reject(new Error('Invalid email options.')); return; }
  const copy = JSON.parse(JSON.stringify(options)); copy.id = ++emailSequence; copy.action = action;
  emailPending.set(copy.id,{resolve,reject}); emailOutbox.push(copy);
});
abort.signal.addEventListener('abort',()=>{
  for (const call of emailPending.values()) call.reject(new Error('Stopped'));
  emailPending.clear(); emailOutbox.length = 0;
},{once:true});
