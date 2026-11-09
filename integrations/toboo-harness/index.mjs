import { randomBytes, randomUUID } from 'node:crypto';
import { mkdir, rename, writeFile, readFile, unlink } from 'node:fs/promises';
import { homedir } from 'node:os';
import { join } from 'node:path';
import { ChatEngine, PROTOCOL } from './engine.mjs';
import { bridgeServer } from './server.mjs';
import { acquireWriter } from './lock.mjs';

export const name = 'toboo-companion-bridge';
export const inject = ['llm'];
export async function apply(ctx) {
    const directory = join(homedir(), '.toboo-harness');
    await mkdir(directory, { recursive: true, mode: 0o700 });
    const release = await acquireWriter(directory);
    const engine = new ChatEngine({ llm: ctx.llm, directory: join(directory, 'sessions') });
    try { await engine.initialize(); } catch (error) { await release(); throw error; }
    const token = randomBytes(32).toString('hex'), instanceId = randomUUID(), port = 37848;
    const server = bridgeServer(engine, token, port);
    try { await new Promise((resolve, reject) => { server.once('error', reject); server.listen(port, '127.0.0.1', resolve); }); }
    catch (error) { await engine.close(); await release(); throw error; }
    const file = join(directory, 'connection.json');
    try {
        await writeFile(file + '.tmp', JSON.stringify({ service: 'toboo-chat', protocol: PROTOCOL, port, token, instanceId, pid: process.pid }), { mode: 0o600 });
        await rename(file + '.tmp', file);
    } catch (error) { server.closeAllConnections(); server.close(); await engine.close(); await release(); throw error; }
    // No Agent/SessionController, global model mutation, file/terminal/servo tool or credentials API is exposed.
    ctx.effect(() => () => {
        server.closeAllConnections(); server.close();
        void engine.close().then(drained => drained ? release() : undefined).catch(() => {});
        void (async () => {
            try { if (JSON.parse(await readFile(file, 'utf8')).instanceId === instanceId) await unlink(file); } catch { /* Already replaced/removed. */ }
        })();
    });
}
