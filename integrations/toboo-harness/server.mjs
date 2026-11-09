import { createServer } from 'node:http';
import { timingSafeEqual } from 'node:crypto';
import { ChatError, publicFailure } from './engine.mjs';

export function bridgeServer(engine, token, port = 37848) {
    const secret = Buffer.from(token);
    const server = createServer(async (request, response) => {
        response.setHeader('Content-Type', 'application/json; charset=utf-8');
        response.setHeader('Cache-Control', 'no-store');
        response.setHeader('X-Content-Type-Options', 'nosniff');
        const provided = Buffer.from(request.headers.authorization || '');
        if (request.headers.origin !== undefined || request.headers.host !== `127.0.0.1:${port}`
            || provided.length !== secret.length || !timingSafeEqual(provided, secret)) {
            response.writeHead(403); response.end(JSON.stringify({ error: { code: 'DENIED', message: '拒绝访问。' } })); return;
        }
        if (request.method !== 'POST' || request.url !== '/rpc') { response.writeHead(404); response.end('{}'); return; }
        if (!/^application\/json(?:;|$)/i.test(request.headers['content-type'] || '')) {
            response.writeHead(415); response.end('{}'); return;
        }
        try {
            const chunks = []; let length = 0;
            for await (const chunk of request) {
                length += chunk.length;
                if (length > 65536) throw new ChatError('TOO_LARGE', '请求内容过大。');
                chunks.push(chunk);
            }
            let body;
            try { body = JSON.parse(Buffer.concat(chunks).toString('utf8')); }
            catch { throw new ChatError('INVALID_JSON', '请求格式无效。'); }
            const result = await engine.dispatch(body);
            response.end(JSON.stringify({ result }));
        } catch (error) {
            response.writeHead(error instanceof ChatError && error.code === 'TOO_LARGE' ? 413 : 400);
            response.end(JSON.stringify({ error: publicFailure(error) }));
        }
    });
    server.requestTimeout = 10000; server.headersTimeout = 5000; server.keepAliveTimeout = 3000;
    server.maxConnections = 32; server.maxRequestsPerSocket = 100;
    return server;
}
