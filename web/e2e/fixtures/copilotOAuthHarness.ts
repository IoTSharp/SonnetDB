import { createPinia, setActivePinia } from 'pinia';
import axios from 'axios';
import { BrowserDirectOAuthClient } from '../../src/copilot/browserDirectOAuth';
import {
  createConfiguredBrowserDirectTransport,
  subscribeBrowserDirectCredentialChanges,
} from '../../src/copilot/browserDirectEntry';
import { useAuthStore } from '../../src/stores/auth';

// Served only by the e2e Vite fixture. The browser still uses production OAuth,
// crypto, popup, callback, credential and public transport implementations.
const configuration = {
  issuer: 'https://idp.oauth.test',
  authorizationEndpoint: 'https://idp.oauth.test/authorize',
  tokenEndpoint: 'https://idp.oauth.test/token',
  clientId: 'sonnetdb-browser-e2e',
  redirectUri: 'https://studio.oauth.test/admin/copilot/oauth/callback',
  approvedOrigins: ['https://idp.oauth.test'],
  scopes: ['openid', 'copilot'],
};
const databaseToken = 'database-secret-oauth-e2e';
const client = new BrowserDirectOAuthClient(configuration);
const result = document.querySelector<HTMLOutputElement>('#result')!;
const credential = document.querySelector<HTMLOutputElement>('#credential')!;
const readiness = document.querySelector<HTMLOutputElement>('#readiness')!;
setActivePinia(createPinia());
const auth = useAuthStore();
const unsubscribe = subscribeBrowserDirectCredentialChanges((expiry) => {
  credential.value = expiry ? 'connected' : 'empty';
});

document.querySelector<HTMLButtonElement>('#sign-in')!.onclick = () => {
  result.value = 'pending';
  void client.signIn(databaseToken).then(
    () => { result.value = 'success'; },
    (error: unknown) => {
      const code = error && typeof error === 'object' && 'code' in error ? String(error.code) : 'error';
      result.value = `error:${code}`;
    },
  );
};
document.querySelector<HTMLButtonElement>('#cancel')!.onclick = () => client.cancel();
document.querySelector<HTMLButtonElement>('#logout')!.onclick = () => client.logout();
document.querySelector<HTMLButtonElement>('#database-logout')!.onclick = () => auth.logout();
document.querySelector<HTMLButtonElement>('#rogue-popup')!.onclick = () => {
  window.open('/oauth-rogue', 'oauth-e2e-unrelated', 'popup,width=480,height=400');
};
document.querySelector<HTMLButtonElement>('#probe')!.onclick = () => {
  readiness.value = 'pending';
  const transport = createConfiguredBrowserDirectTransport(
    axios.create({ baseURL: location.origin }),
    databaseToken,
    {
      publicBaseUrl: 'https://ai.oauth.test',
      approvedPublicOrigins: ['https://ai.oauth.test'],
      locationHref: location.href,
    },
  );
  void transport.probeReadiness(new AbortController().signal).then(
    (value) => { readiness.value = value.public.status; },
    () => { readiness.value = 'error'; },
  );
};
window.addEventListener('pagehide', () => {
  unsubscribe();
  client.dispose();
}, { once: true });
document.querySelector<HTMLOutputElement>('#ready')!.value = 'ready';
