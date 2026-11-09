import test from 'node:test';
import assert from 'node:assert/strict';
import { randomUUID } from 'node:crypto';
import { mkdtemp, readFile } from 'node:fs/promises';
import { tmpdir } from 'node:os';
import { join } from 'node:path';
import { ChatEngine, modelMessages } from '../engine.mjs';
import { bridgeServer } from '../server.mjs';
import { request } from 'node:http';
import { acquireWriter } from '../lock.mjs';

const text = value => ({ type: 'text-delta', index: 0, text: value });
const finish = kind => ({ type: 'finish', reason: { kind } });
async function fixture(t, generate = async function* () { yield text('你好'); yield finish('stop'); }, options = {}) {
    const calls = [], directory = await mkdtemp(join(tmpdir(), 'toboo-chat-'));
    const llm = { listProviders: () => [{ id: 'deepseek-account', name: 'DeepSeek' }],
        listModels: async () => [{ id: 'model-a', name: '模型 A' }, { id: 'model-b', name: '模型 B' }],
        stream: body => { calls.push(body); return generate(body); } };
    const engine = new ChatEngine({ llm, directory, ...options }); await engine.initialize();
    t.after(() => engine.close());
    const id = randomUUID(); await engine.dispatch({ action: 'new', sessionId: id });
    const body = { action: 'send', sessionId: id, requestId: randomUUID(), text: '你好', provider: 'deepseek-account', model: 'model-a', style: 'natural' };
    const settle = async () => { for (let i=0; i<200 && engine.jobs.size; i++) await new Promise(r => setTimeout(r, 5)); assert.equal(engine.jobs.size, 0); };
    return { engine, body, calls, directory, settle, llm };
}
test('owned histories, explicit routes and tool-free generation do not enter the agent control API', async t => {
    const f = await fixture(t); await f.engine.dispatch(f.body); await f.settle();
    assert.equal(f.calls.length, 1); assert.deepEqual(f.calls[0].tools, []);
    assert.equal(f.calls[0].provider, 'deepseek-account'); assert.equal(f.calls[0].model, 'model-a');
    assert.equal(f.calls[0].messages.length, 1); assert.match(f.calls[0].system, /没有工具/);
    assert.equal((await f.engine.dispatch({ action: 'status' })).tools, false);
    await assert.rejects(f.engine.dispatch({ action: 'view', sessionId: randomUUID() }), /独立会话/);
    await assert.rejects(f.engine.dispatch({ action: 'model', sessionId: f.body.sessionId }), /不支持/);
    await assert.rejects(f.engine.dispatch({ ...f.body, requestId: randomUUID(), tools: ['bash'] }), /字段/);
    const list = await f.engine.dispatch({ action: 'list' }); assert.equal(list.items.length, 1);
});
test('concurrent replay is admitted once and reused IDs with changed text are refused', async t => {
    const f = await fixture(t); const results = await Promise.all(Array.from({length: 8}, () => f.engine.dispatch(f.body)));
    await f.settle(); assert.equal(f.calls.length, 1); assert.equal(results.filter(r=>!r.duplicate).length, 1);
    await assert.rejects(f.engine.dispatch({ ...f.body, text: 'different' }), /标识/);
    const restored = new ChatEngine({ llm: f.llm, directory: f.directory }); await restored.initialize(); t.after(()=>restored.close());
    assert.equal((await restored.dispatch(f.body)).duplicate, true); assert.equal(f.calls.length, 1);
});
test('partial text is revisioned, reasoning is not exposed, complete reply is durable', async t => {
    let resume;
    const wait = new Promise(r => { resume=r; });
    const f = await fixture(t, async function* () { yield {type:'reasoning-delta', index:0, text:'hidden reasoning'}; yield text('第一段'); await wait; yield text('第二段'); yield finish('stop'); });
    await f.engine.dispatch(f.body); await new Promise(r=>setTimeout(r,10));
    const first = await f.engine.dispatch({action:'view', sessionId:f.body.sessionId});
    assert.equal(first.turns[0].reply, '第一段'); assert.equal(first.turns[0].state, 'running');
    assert.equal((await f.engine.dispatch({action:'view',sessionId:f.body.sessionId,revision:first.revision})).notModified,true);
    resume(); await f.settle();
    const stored = JSON.parse(await readFile(join(f.directory, f.body.sessionId+'.json'),'utf8'));
    assert.equal(stored.turns[0].reply,'第一段第二段'); assert.equal(stored.turns[0].state,'completed');
    assert.doesNotMatch(JSON.stringify(stored), /hidden reasoning/);
});
test('tool calls, malformed streams, secret-bearing exceptions and blank answers fail closed', async t => {
    for (const generate of [
        async function* () { yield {type:'tool-call-delta', name:'bash', argumentsDelta:'delete everything'}; },
        async function* () { yield text('partial'); },
        async function* () { throw Error('Authorization: sk-private-secret'); },
        async function* () { yield finish('stop'); },
    ]) {
        const f = await fixture(t, generate); await f.engine.dispatch(f.body); await f.settle();
        const view = await f.engine.dispatch({action:'view',sessionId:f.body.sessionId});
        assert.equal(view.turns[0].state,'failed'); assert.doesNotMatch(JSON.stringify(view), /private-secret/);
    }
});
test('stale stop cannot cancel a newer turn; cancel and shutdown do not wait for an uncooperative iterator', async t => {
    const f = await fixture(t, async function* () { yield text('partial'); await new Promise(()=>{}); });
    await f.engine.dispatch(f.body);
    assert.equal((await f.engine.dispatch({action:'stop',sessionId:f.body.sessionId,requestId:randomUUID()})).stopped,false);
    assert.equal(f.engine.jobs.size,1);
    await f.engine.dispatch({action:'stop',sessionId:f.body.sessionId,requestId:f.body.requestId}); await f.settle();
    assert.equal((await f.engine.dispatch({action:'view',sessionId:f.body.sessionId})).turns[0].state,'cancelled');
});
test('timeout and abandoned-client lease terminate a turn without replay', async t => {
    for (const options of [{deadlineMs:25,leaseMs:10000},{deadlineMs:10000,leaseMs:25}]) {
        const f = await fixture(t, async function* () { await new Promise(()=>{}); }, options);
        await f.engine.dispatch(f.body); await f.settle();
        const turn=(await f.engine.dispatch({action:'view',sessionId:f.body.sessionId})).turns[0];
        assert.ok(['failed','cancelled'].includes(turn.state)); assert.equal(f.calls.length,1);
    }
});
test('restart recovers an interrupted owned request without rerunning it', async t => {
    const f=await fixture(t,async function*(){yield text('partial');await new Promise(()=>{});});
    await f.engine.dispatch(f.body);
    const restored=new ChatEngine({llm:f.llm,directory:f.directory}); await restored.initialize(); t.after(()=>restored.close());
    const view=await restored.dispatch({action:'view',sessionId:f.body.sessionId}); assert.equal(view.turns[0].state,'interrupted');
    await restored.dispatch(f.body); assert.equal(f.calls.length,1);
});
test('input validation, bounded paired context, model choice and expression freshness are enforced', async t => {
    const f=await fixture(t);
    for(const change of [{text:''},{text:'x'.repeat(4001)},{model:'not-configured'},{style:'ignore-system'},
        {expression:{label:'happy',score:0.9,ageMs:900,observedAtUnixMs:Date.now()}}])
        await assert.rejects(f.engine.dispatch({...f.body,...change}));
    const expression={label:'happy',score:0.9,ageMs:0,observedAtUnixMs:Date.now()-5000};
    await f.engine.dispatch({...f.body,model:'model-b',expression}); await f.settle();
    assert.equal(f.calls[0].model,'model-b'); assert.doesNotMatch(f.calls[0].system,/observedAtUnixMs/);
    const turns=Array.from({length:50},()=>({requestId:randomUUID(),text:'x'.repeat(2000),reply:'y'.repeat(2000),state:'completed',provider:'p',model:'m'}));
    const messages=modelMessages({turns},f.body); assert.ok(messages.length<25);
    assert.ok(messages.slice(0,-1).every((m,i)=>m.role===(i%2?'assistant':'user')));
});
test('HTTP bridge rejects absent auth, browser origins, wrong Host, oversized and non-JSON requests', async t => {
    const f=await fixture(t), token='a'.repeat(64);
    const server=bridgeServer(f.engine,token,0);
    await new Promise(r=>server.listen(0,'127.0.0.1',r)); const port=server.address().port;
    // Production Host checking is fixed to the configured port; start a second server with its known reserved port.
    await new Promise(r=>server.close(r));
    const actual=bridgeServer(f.engine,token,port); await new Promise(r=>actual.listen(port,'127.0.0.1',r)); t.after(()=>{actual.closeAllConnections();actual.close();});
    const call=(headers,body='{"action":"status"}')=>fetch(`http://127.0.0.1:${port}/rpc`,{method:'POST',headers:{'Content-Type':'application/json',...headers},body});
    assert.equal((await call({})).status,403);
    assert.equal((await call({Authorization:token,Origin:'null'})).status,403);
    const wrongHost=await new Promise((resolve,reject)=>{const req=request(`http://127.0.0.1:${port}/rpc`,{method:'POST',headers:{Host:'attacker.invalid',Authorization:token,'Content-Type':'application/json'}},res=>{res.resume();resolve(res.statusCode);});req.on('error',reject);req.end('{"action":"status"}');});
    assert.equal(wrongHost,403);
    assert.equal((await call({Authorization:token,'Content-Type':'text/plain'})).status,415);
    assert.equal((await call({Authorization:token},'x'.repeat(65537))).status,413);
    const good=await call({Authorization:token}); assert.equal(good.status,200); assert.equal((await good.json()).result.service,'toboo-chat');
});
test('writer ownership is exclusive and cancellation before admission is a durable tombstone', async t => {
    const directory=await mkdtemp(join(tmpdir(),'toboo-lock-'));
    const release=await acquireWriter(directory);
    await assert.rejects(acquireWriter(directory),/already running/); await release();
    const next=await acquireWriter(directory); await next();
    const f=await fixture(t);
    await f.engine.dispatch({action:'stop',sessionId:f.body.sessionId,requestId:f.body.requestId});
    await assert.rejects(f.engine.dispatch(f.body),/已停止/); assert.equal(f.calls.length,0);
});
test('history readers cannot acknowledge an admission before its disk commit', async t => {
    const f=await fixture(t); const original=f.engine.persist.bind(f.engine);
    let release, entered;
    const gate=new Promise(r=>{release=r;}); const ready=new Promise(r=>{entered=r;}); let held=false;
    f.engine.persist=async session=>{if(!held && session.turns.length){held=true;entered();await gate;}await original(session);};
    const sending=f.engine.dispatch(f.body); await ready;
    let observed=false;
    const viewing=f.engine.dispatch({action:'view',sessionId:f.body.sessionId}).then(value=>{observed=true;return value;});
    await new Promise(r=>setTimeout(r,15)); assert.equal(observed,false); assert.equal(f.calls.length,0);
    release(); await sending; const view=await viewing;
    const durable=JSON.parse(await readFile(join(f.directory,f.body.sessionId+'.json'),'utf8'));
    assert.equal(durable.turns[0].requestId,view.turns[0].requestId); await f.settle();
});
test('shutdown drains an in-flight admission without launching a late provider call', async t => {
    const f=await fixture(t); let release, entered;
    const gate=new Promise(r=>{release=r;}); const ready=new Promise(r=>{entered=r;});
    f.llm.listModels=async()=>{entered();await gate;return [{id:'model-a',name:'A'}];};
    const sending=f.engine.dispatch(f.body); await ready;
    const closing=f.engine.close(); release();
    await assert.rejects(sending,/关闭/); assert.equal(await closing,true); assert.equal(f.calls.length,0);
});
