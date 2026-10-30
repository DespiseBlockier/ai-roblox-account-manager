// Generate this example with your group/community ID in "Open browser with code".
// A pending join request is NOT membership. Keep the browser open for approval/verification.
const groupId = /* GROUP_ID */ 12345678;
const groupApi = 'https://groups.roblox.com';

async function request(url, options = {}) {
  return fetch(url, {...options, credentials: 'include', signal: ram.signal});
}

// Confirm the visible session still belongs to the selected account before changing membership.
const identityResponse = await request('https://users.roblox.com/v1/users/authenticated');
if (!identityResponse.ok) throw new Error('Sign in again before running this example.');
const identity = await identityResponse.json();
if (String(identity.id) !== ram.account.userId) throw new Error('The browser is signed in to a different account.');

async function isMember() {
  const response = await request(groupApi + '/v2/users/' + encodeURIComponent(ram.account.userId) + '/groups/roles');
  if (!response.ok) throw new Error('Membership check failed (HTTP ' + response.status + '). Stop and retry later.');
  const body = await response.json();
  if (!Array.isArray(body.data)) throw new Error('Unexpected membership response.');
  return body.data.some(item => String(item.group?.id) === String(groupId));
}

if (await isMember()) {
  ram.status('Already a member. Confirming before moving on.');
  ram.done();
  return;
}

ram.status('Requesting to join community ' + groupId + ' as @' + ram.account.username + '.');
const joinUrl = groupApi + '/v1/groups/' + groupId + '/users';
let csrf = document.querySelector('meta[name="csrf-token"]')?.getAttribute('data-token');
async function join() {
  const headers = {'Content-Type': 'application/json'};
  if (csrf) headers['X-CSRF-TOKEN'] = csrf;
  return request(joinUrl, {method: 'POST', headers, body: '{}'});
}

let response = await join();
// Retry once only for Roblox's ordinary CSRF-token handshake. Never replay challenges.
const replacement = response.headers.get('x-csrf-token');
if (response.status === 403 && replacement && !response.headers.get('rblx-challenge-id')) {
  csrf = replacement;
  response = await join();
}
if (response.status === 429) {
  ram.stop('Roblox rate limited joining. Wait before running the queue again.');
  return;
}
if (response.status === 401) throw new Error('This account session expired. Sign in again.');
if (!response.ok && response.status !== 403) throw new Error('Join request failed (HTTP ' + response.status + ').');

ram.status(response.status === 403
  ? 'Complete Join Community and any verification on this Roblox page. Waiting for actual membership.'
  : 'Waiting for actual membership. If approval is required, approve the request in your community.');

while (!await isMember()) {
  await ram.sleep(10000);
}
ram.status('Membership confirmed. Moving to the next selected account.');
ram.done(); // The manager independently confirms membership, closes this browser, and advances.
