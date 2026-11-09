import { open, readFile, rename, unlink } from 'node:fs/promises';
import { join } from 'node:path';
import { randomUUID } from 'node:crypto';

// Acquire before loading/recovering records: a second host must never "recover" a live writer's turn.
export async function acquireWriter(directory) {
    const path = join(directory, 'bridge.lock'), instanceId = randomUUID();
    for (let attempt = 0; attempt < 3; attempt++) {
        try {
            const file = await open(path, 'wx', 0o600);
            try { await file.writeFile(JSON.stringify({ pid: process.pid, instanceId })); } finally { await file.close(); }
            return async () => {
                try { if (JSON.parse(await readFile(path, 'utf8')).instanceId === instanceId) await unlink(path); } catch { /* Replaced by a later owner. */ }
            };
        } catch (error) {
            if (error.code !== 'EEXIST') throw error;
            let owner;
            try { owner = JSON.parse(await readFile(path, 'utf8')); } catch { throw Error('toboo bridge lock is unreadable; no transcripts were changed'); }
            if (!Number.isInteger(owner.pid) || owner.pid <= 0) throw Error('Invalid toboo bridge lock; no transcripts were changed');
            try { process.kill(owner.pid, 0); throw Error('Another toboo bridge is already running'); }
            catch (probe) { if (probe.code !== 'ESRCH') throw probe; }
            try { await rename(path, path + '.stale-' + randomUUID()); } catch (race) { if (race.code !== 'ENOENT') throw race; }
        }
    }
    throw Error('Could not exclusively own the toboo bridge');
}
