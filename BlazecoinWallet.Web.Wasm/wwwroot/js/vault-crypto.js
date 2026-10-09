// The web head's seed-vault crypto — WebCrypto only, no libraries. The browser has no
// Keystore, so at-rest protection is a password-derived key: PBKDF2-SHA256 (600k
// iterations, OWASP-order work factor; native SubtleCrypto, so it's fast) into
// AES-256-GCM. GCM's auth tag doubles as the wrong-password check: decrypt of a
// tampered or wrongly-keyed blob throws, it never returns garbage.
//
// Managed .NET crypto is NOT used because System.Security.Cryptography.AesGcm is
// unsupported on browser-wasm — this module IS the vault's cipher.

const STORE_KEY = 'blz_vault_v1';
const ITERATIONS = 600000;

const te = new TextEncoder();
const td = new TextDecoder();
const b64 = bytes => btoa(String.fromCharCode(...new Uint8Array(bytes)));
const unb64 = s => Uint8Array.from(atob(s), c => c.charCodeAt(0));

async function deriveKey(password, salt, iterations) {
    const material = await crypto.subtle.importKey('raw', te.encode(password), 'PBKDF2', false, ['deriveKey']);
    return crypto.subtle.deriveKey(
        { name: 'PBKDF2', salt, iterations, hash: 'SHA-256' },
        material, { name: 'AES-GCM', length: 256 }, false, ['encrypt', 'decrypt']);
}

/** Encrypts plaintext under the password and persists the blob. Fresh random salt+iv
 *  every call, so re-protecting never reuses a nonce. */
export async function protect(password, plaintext) {
    const salt = crypto.getRandomValues(new Uint8Array(16));
    const iv = crypto.getRandomValues(new Uint8Array(12));
    const key = await deriveKey(password, salt, ITERATIONS);
    const ct = await crypto.subtle.encrypt({ name: 'AES-GCM', iv }, key, te.encode(plaintext));
    localStorage.setItem(STORE_KEY, JSON.stringify(
        { v: 1, iterations: ITERATIONS, salt: b64(salt), iv: b64(iv), ct: b64(ct) }));
}

/** The decrypted plaintext, or null — null means wrong password OR no/corrupt blob. */
export async function unlock(password) {
    const raw = localStorage.getItem(STORE_KEY);
    if (!raw) return null;
    try {
        const blob = JSON.parse(raw);
        const key = await deriveKey(password, unb64(blob.salt), blob.iterations);
        const pt = await crypto.subtle.decrypt({ name: 'AES-GCM', iv: unb64(blob.iv) }, key, unb64(blob.ct));
        return td.decode(pt);
    } catch {
        return null;   // GCM auth failure = wrong password (or a tampered blob)
    }
}

export function hasBlob() { return localStorage.getItem(STORE_KEY) !== null; }

export function wipe() { localStorage.removeItem(STORE_KEY); }

/** Asks the browser to exempt this origin's storage from eviction — browsers may
 *  otherwise clear site data under storage pressure, which would DELETE the encrypted
 *  vault blob. Returns whether persistence is granted (Chrome decides silently on
 *  engagement heuristics; Firefox may prompt). Already-granted resolves true. False is
 *  survivable — the recovery phrase is the designed backup — but the caller should say
 *  so out loud. */
export async function requestPersistence() {
    try {
        if (navigator.storage?.persisted && await navigator.storage.persisted()) return true;
        if (navigator.storage?.persist) return await navigator.storage.persist();
    } catch { /* unsupported/blocked — fall through */ }
    return false;
}
