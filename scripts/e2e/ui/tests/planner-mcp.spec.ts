import { test, expect, APIRequestContext } from '@playwright/test';
import { open } from './helpers';

// The planner through the MCP endpoint (#307): a coding agent opens, plans, comments on and closes
// a GitHub issue with Hephaisto's tools, and the planner - its API, its page and the stand-in's
// board - shows each change at once, without a board read in between.
//
// Needs the stand-in's board and the MCP endpoint with the pager suite's tokens:
//
//   GITHUB_STANDIN_URL=http://<host>:8110
//   HEPHAISTO_MCP_URL=http://<host>:8183/mcp
//   HEPHAISTO_MCP_TOKEN=<pager-shared>            what a gateway holds: a shared token that may write
//   HEPHAISTO_MCP_TOKEN_READONLY=<pager-readonly>
//
// (tilt_config.json "github": "stand-in" and "mcp": true; eval "$(scripts/e2e/mcp-secrets.sh --print)"
// gives the tokens.) Without them every spec skips with a PRECONDITION marker.

const STANDIN = process.env.GITHUB_STANDIN_URL?.replace(/\/$/, '');
const MCP = process.env.HEPHAISTO_MCP_URL;
const TOKEN = process.env.HEPHAISTO_MCP_TOKEN;
const READONLY = process.env.HEPHAISTO_MCP_TOKEN_READONLY;

const PLANNER_TOOLS = [
  'list_planner_issues', 'get_planner_issue', 'lookup_planner_fields',
  'create_planner_issue', 'update_planner_issue', 'close_planner_issue', 'comment_on_planner_issue',
];

type Call = { isError: boolean; text: string; json: any };

/** One JSON-RPC request, as a gateway sends it: stateless, the answer as JSON or as one SSE event. */
async function rpc(request: APIRequestContext, token: string, method: string, params: object): Promise<any> {
  const res = await request.post(MCP!, {
    headers: { Authorization: `Bearer ${token}`, Accept: 'application/json, text/event-stream', 'User-Agent': 'hephaisto-planner-mcp-e2e' },
    data: { jsonrpc: '2.0', id: Date.now(), method, params },
  });
  expect(res.status(), `${method} answered HTTP ${res.status()}`).toBe(200);

  const body = await res.text();
  const data = body.split('\n').filter(l => l.startsWith('data: ')).pop();
  return JSON.parse(data ? data.slice(6) : body);
}

async function call(request: APIRequestContext, tool: string, args: object, token = TOKEN!): Promise<Call> {
  const message = await rpc(request, token, 'tools/call', { name: tool, arguments: args });
  expect(message.error, `${tool}: ${JSON.stringify(message.error)}`).toBeFalsy();

  const text = (message.result.content ?? []).filter((c: { type: string }) => c.type === 'text').map((c: { text: string }) => c.text).join('');
  let json: any = null;
  try { json = JSON.parse(text); } catch { /* a refusal is a sentence */ }

  return { isError: message.result.isError === true, text, json };
}

/** A name or an option as the tools hand it out: plain, or inside the untrusted envelope when it has a space. */
const plain = (value: string) => value.replace(/^<untrusted-evidence>\n?|\n?<\/untrusted-evidence>$/g, '').trim();

const ref = (i: { repository: string; number: number }) => `${i.repository}#${i.number}`;

async function boardItem(request: APIRequestContext, reference: string) {
  const items = (await (await request.get(`${STANDIN}/github/control/project`)).json()).items;
  return items.find((i: { repository: string; number: number }) => ref(i) === reference);
}

async function plannerItem(request: APIRequestContext, reference: string) {
  const res = await request.get('/api/planner');
  expect(res.ok()).toBeTruthy();
  return (await res.json()).items.find((i: { repository: string; number: number }) => ref(i) === reference);
}

test.describe.configure({ mode: 'serial' });

test.describe('planner through the MCP endpoint', () => {
  let reference = '';
  const title = `Planner tools over MCP ${Date.now()}`;

  test.beforeAll(async ({ request }) => {
    test.skip(!STANDIN || !MCP || !TOKEN, 'PRECONDITION: GITHUB_STANDIN_URL, HEPHAISTO_MCP_URL and HEPHAISTO_MCP_TOKEN are needed (the stand-in board and the MCP endpoint)');

    expect((await request.post(`${STANDIN}/github/control/project/reset`)).status()).toBe(204);
    expect((await request.get('/api/planner?refresh=true')).ok()).toBeTruthy();
  });

  test('a token that may write lists the planner tools, a read-only one only the reads', async ({ request }) => {
    const names = (await rpc(request, TOKEN!, 'tools/list', {})).result.tools.map((t: { name: string }) => t.name);
    expect(names).toEqual(expect.arrayContaining(PLANNER_TOOLS));

    test.skip(!READONLY, 'PRECONDITION: HEPHAISTO_MCP_TOKEN_READONLY is not set');
    const reads = (await rpc(request, READONLY!, 'tools/list', {})).result.tools.map((t: { name: string }) => t.name);
    expect(reads.filter((n: string) => PLANNER_TOOLS.includes(n))).toEqual(['list_planner_issues', 'get_planner_issue', 'lookup_planner_fields']);
  });

  test('an issue opened for today is on the board, planned for today and in the console at once', async ({ page, request }) => {
    const fields = (await call(request, 'lookup_planner_fields', {})).json;
    const repository = plain(fields.repositories[0]);
    const type = plain(fields.issueTypes[0]);
    const priority = fields.fields.find((f: { name: string }) => plain(f.name) === 'Priority');
    expect(priority, 'the stand-in board has a Priority field').toBeTruthy();
    const option = plain(priority.options[0]);

    const created = await call(request, 'create_planner_issue', {
      title, body: 'Opened by the planner MCP e2e.', type, fields: { Priority: option }, day: 'today', onBehalfOf: 'planner-e2e',
    });
    expect(created.isError, created.text).toBe(false);
    reference = created.json.issue.issue;
    expect(reference.startsWith(`${repository}#`)).toBe(true);
    expect(created.json.done).toEqual([`${reference} opened and put on the board`, 'today']);
    expect(created.json.recordedAs).toBe('mcp/pager-shared');
    expect(created.json.claimedBy).toBe('planner-e2e');

    // The other side: GitHub's board has the issue, its field and its label.
    const truth = await boardItem(request, reference);
    expect(truth, `${reference} is on the stand-in's board`).toBeTruthy();
    expect(truth.title).toBe(title);
    expect(truth.type).toBe(type);
    expect(truth.fields.Priority).toBe(option);
    expect(truth.labels).toContain('today');

    // The planner knows of it without a board read: no refresh here.
    const item = await plannerItem(request, reference);
    expect(item, 'the planner has the issue without reading the board again').toBeTruthy();
    expect(item.day).toBe('Today');

    const today = (await call(request, 'list_planner_issues', { view: 'today' })).json;
    expect(today.issues.map((i: { issue: string }) => i.issue)).toContain(reference);

    await open(page, '/planner?view=today');
    await expect(page.getByText(title).first()).toBeVisible();
  });

  test('it is planned for tomorrow and renamed, and both are on the board', async ({ request }) => {
    const updated = await call(request, 'update_planner_issue', { issue: reference, day: 'tomorrow', title: `${title}, renamed` });
    expect(updated.isError, updated.text).toBe(false);
    expect(updated.json.done[0]).toBe('title changed');
    expect(updated.json.done[1]).toMatch(/^planned for /);

    const truth = await boardItem(request, reference);
    expect(truth.title).toBe(`${title}, renamed`);
    expect(truth.labels).not.toContain('today');
    expect(Object.values(truth.dates ?? {}).length, 'the board\'s date field holds tomorrow').toBe(1);

    expect((await plannerItem(request, reference)).day).toBe('Later');
  });

  test('a comment is relayed, signed as an agent\'s, and the issue closes as done', async ({ request }) => {
    const [repository, number] = reference.split('#');

    const commented = await call(request, 'comment_on_planner_issue', { issue: reference, text: 'Picked up by the e2e.', onBehalfOf: 'planner-e2e' });
    expect(commented.isError, commented.text).toBe(false);

    const comments = await (await request.get(`${STANDIN}/github/control/comments`)).json();
    const comment = comments.filter((c: { repository: string; number: number }) => c.repository === repository && c.number === Number(number)).pop();
    expect(comment.body).toContain('Picked up by the e2e.');
    expect(comment.body).toContain('Relayed by an agent through Hephaisto, for planner-e2e.');
    expect(comment.body).not.toContain('hephaisto:planner-comment', 'a relayed comment is never taken for a person\'s');

    const closed = await call(request, 'close_planner_issue', { issue: reference, reason: 'done' });
    expect(closed.isError, closed.text).toBe(false);
    expect(closed.json.done).toEqual(['closed as done']);

    const truth = await boardItem(request, reference);
    expect(truth.state).toBe('closed');
    expect(truth.closedAs).toBe('COMPLETED');

    const again = await call(request, 'update_planner_issue', { issue: reference, day: 'today' });
    expect(again.isError).toBe(true);
    expect(again.text).toContain('is closed');
  });

  test('a read-only token opens nothing', async ({ request }) => {
    test.skip(!READONLY, 'PRECONDITION: HEPHAISTO_MCP_TOKEN_READONLY is not set');

    const message = await rpc(request, READONLY!, 'tools/call', { name: 'create_planner_issue', arguments: { title: 'Should not open' } });
    const refused = message.error !== undefined || message.result?.isError === true;
    expect(refused, JSON.stringify(message)).toBe(true);

    const items = (await (await request.get(`${STANDIN}/github/control/project`)).json()).items;
    expect(items.filter((i: { title: string }) => i.title === 'Should not open')).toEqual([]);
  });
});
