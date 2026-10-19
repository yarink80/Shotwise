'use strict';
const status = document.querySelector('#status'), files = document.querySelector('#files'), send = document.querySelector('#send'), progress = document.querySelector('#progress');
const certificateNotice = document.createElement('p'); certificateNotice.id = 'fingerprint';
document.querySelector('#connection').after(certificateNotice);
let bearer = sessionStorage.getItem('shotwise-session'), max = 2147483648;
async function connect() {
  const pairing = new URLSearchParams(location.hash.slice(1));
  const token = pairing.get('t');
  const fingerprint = pairing.get('fp') || sessionStorage.getItem('shotwise-fingerprint');
  history.replaceState(null, '', '/');
  if (!fingerprint || !/^[A-F0-9]{64}$/.test(fingerprint)) throw Error('Scan a new desktop pairing QR code.');
  document.querySelector('#fingerprint').textContent = 'Desktop certificate SHA-256: ' + fingerprint + '. Compare this with the certificate details in your browser and on your desktop before pairing. This page cannot inspect the TLS certificate; its fingerprint check does not stop an attacker who replaces the page.';
  if (token && !confirm('Before pairing, compare certificate SHA-256 ' + fingerprint + ' with the certificate details in this browser and the desktop pairing dialog. Continue only if all match.')) throw Error('Pairing cancelled. Scan the QR code to try again.');
  const info = await fetch('/api/v1/info', { headers: { Authorization: token ? 'Pairing ' + token : 'Bearer ' + bearer } }).then(r => { if (!r.ok) throw Error('Connection unavailable.'); return r.json(); });
  if (info.fingerprint !== fingerprint) { bearer = null; sessionStorage.removeItem('shotwise-session'); throw Error('Certificate fingerprint mismatch. Reconnect directly to your desktop.'); }
  if (token) {
    const response = await fetch('/api/v1/handshake', { method:'POST', headers:{'Content-Type':'application/json'}, body:JSON.stringify({ token, deviceName:'Mobile browser' }) });
    if (!response.ok) throw Error('Pairing expired or already used. Scan a new QR code.');
    bearer = (await response.json()).token; sessionStorage.setItem('shotwise-session', bearer);
  }
  sessionStorage.setItem('shotwise-fingerprint', fingerprint);
  document.querySelector('#connection').textContent = info.name; max = info.maxUploadBytes;
  if (bearer) { files.disabled = false; status.textContent = 'Connected. Choose your files.'; }
}
files.addEventListener('change', () => { send.disabled = !files.files.length; });
send.addEventListener('click', () => {
  const chosen = Array.from(files.files);
  if (chosen.reduce((n, f) => n + f.size, 0) > max) { status.textContent = 'These files exceed the transfer size limit.'; return; }
  const form = new FormData(); chosen.forEach(f => form.append('files', f));
  const xhr = new XMLHttpRequest(); xhr.open('POST', '/api/v1/files'); xhr.setRequestHeader('Authorization', 'Bearer ' + bearer);
  send.disabled = true; files.disabled = true;
  xhr.upload.onprogress = e => { if (e.lengthComputable) progress.value = e.loaded / e.total * 100; };
  xhr.onload = () => {
    const ok = xhr.status >= 200 && xhr.status < 300;
    status.textContent = ok ? 'Files received. You can find them in Shotwise.' : xhr.status === 401 ? 'Session expired. Scan a new QR code.' : 'Transfer failed (' + xhr.status + '). Please try again.';
    if (xhr.status === 401) { bearer = null; sessionStorage.removeItem('shotwise-session'); }
    files.disabled = !bearer; send.disabled = !bearer || !files.files.length;
    if (ok) { files.value = ''; send.disabled = true; }
  };
  xhr.onerror = () => { status.textContent = 'Connection lost. Check your Wi-Fi and try again.'; files.disabled = false; send.disabled = false; };
  xhr.send(form);
});
connect().catch(e => { status.textContent = e.message; });
