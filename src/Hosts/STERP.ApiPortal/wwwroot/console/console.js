/*
 * STERP API Console (FR-STERP-PORTAL-002, 010..014). The server renders the shell, the operation tree and the
 * catalogue; this script adds the filter and the definition selector, and renders the document pages, the operation pages
 * with Try it out, the contract-drift check and the Try it out settings from the binding OpenAPI documents
 * (/console/openapi/{name}.json, servers pointing at the configured hosts).
 * - Try it out sends the request from this browser to the server the operation declares with credentials: 'include', so
 *   STERP.Api and STERP.Jobs see the user's own Windows identity; a non-GET call gets a new Idempotency-Key per
 *   instruction unless one is typed.
 * - The context headers are prefilled from the Try it out settings (company, site, business date; browser local storage,
 *   default = the caller's own STERP user defaults). X-STERP-Run-As and Idempotency-Key are never prefilled, so a call
 *   never runs as another user by accident.
 * - Every prepared or executed request is shown as bash curl --negotiate -u :, Windows curl.exe --negotiate -u : and
 *   PowerShell Invoke-RestMethod -UseDefaultCredentials command lines.
 */
(function () {
    'use strict';

    window.sterpConsole = { started: false };

    const config = JSON.parse(document.getElementById('console-config').textContent);
    const activity = window.sterpActivity || { begin() { }, end() { }, touch() { }, startupDone() { } };
    const SETTINGS_KEY = 'sterp.portal.settings';
    const TITLE = 'STERP API Console';
    const METHODS = ['get', 'put', 'post', 'delete', 'options', 'head', 'patch', 'trace'];
    const CONTEXT_HEADERS = { 'x-sterp-company': 'company', 'x-sterp-site': 'site', 'x-sterp-business-date': 'businessDate' };
    const PLACEHOLDERS = {
        'x-sterp-run-as': 'blank = act as yourself',
        'idempotency-key': 'blank = a new key for every Execute',
        'x-sterp-business-date': 'blank = the server date',
    };
    const TRACE_ORDER = ['x-sterp-requirements', 'x-sterp-permission', 'x-sterp-legacy', 'x-sterp-uow', 'x-sterp-engine',
        'x-sterp-outcome', 'x-sterp-wave', 'x-sterp-screen-gate', 'x-sterp-job-result'];
    const REASONS = {
        200: 'OK', 201: 'Created', 202: 'Accepted', 204: 'No Content', 301: 'Moved Permanently', 302: 'Found', 304: 'Not Modified',
        400: 'Bad Request', 401: 'Unauthorized', 403: 'Forbidden', 404: 'Not Found', 405: 'Method Not Allowed', 406: 'Not Acceptable',
        409: 'Conflict', 412: 'Precondition Failed', 415: 'Unsupported Media Type', 422: 'Unprocessable Content',
        428: 'Precondition Required', 429: 'Too Many Requests', 500: 'Internal Server Error', 501: 'Not Implemented',
        502: 'Bad Gateway', 503: 'Service Unavailable', 504: 'Gateway Timeout',
    };

    const state = {
        status: config.status,
        documents: new Map(),
        settings: { company: '', site: '', businessDate: '' },
        settingsSource: 'none',
        session: null,
        drift: null,
        definition: '',
        filter: '',
        route: null,
        renderToken: 0,
    };

    // ------------------------------------------------------------------------------------------------------ DOM helpers
    const $ = (selector, root) => (root || document).querySelector(selector);
    const $$ = (selector, root) => Array.from((root || document).querySelectorAll(selector));

    function h(tag, props, ...children) {
        const e = document.createElement(tag);
        for (const [k, v] of Object.entries(props || {})) {
            if (v === null || v === undefined || v === false) { continue; }
            if (k === 'class') { e.className = v; }
            else if (k.startsWith('on') && typeof v === 'function') { e.addEventListener(k.slice(2), v); }
            else { e.setAttribute(k, v === true ? '' : String(v)); }
        }
        return append(e, children);
    }

    function append(parent, children) {
        for (const c of children) {
            if (c === null || c === undefined || c === false) { continue; }
            if (Array.isArray(c)) { append(parent, c); }
            else { parent.append(c instanceof Node ? c : document.createTextNode(String(c))); }
        }
        return parent;
    }

    /** Text with `code spans` (the binding descriptions use Markdown code spans only). */
    function rich(text) {
        const fragment = document.createDocumentFragment();
        String(text ?? '').split('`').forEach((part, i) => {
            if (part) { fragment.append(i % 2 === 1 ? h('code', null, part) : document.createTextNode(part)); }
        });
        return fragment;
    }

    function paragraphs(text) {
        return String(text ?? '').split(/\n\s*\n/).map(p => p.trim()).filter(Boolean).map(p => h('p', null, rich(p)));
    }

    function note(kind, icon, title, ...body) {
        return h('div', { class: 'note' + (kind ? ' ' + kind : '') },
            h('span', { class: 'icon', 'aria-hidden': 'true' }, icon),
            h('span', null, title ? h('strong', null, title) : null, ...body));
    }

    const INFO = '\u2139', CHECK = '\u2713', BANG = '!';

    function panel(header, body, options) {
        const o = options || {};
        return h('section', { class: 'panel', id: o.id },
            h('header', null, header),
            o.raw || null,
            body === null || body === undefined ? null : h('div', { class: 'body' + (o.tight ? ' tight' : '') }, body));
    }

    const spacer = () => h('span', { class: 'spacer' });
    const pad2 = n => (typeof n === 'number' && n >= 0 && n < 10 ? '0' + n : String(n));
    const stateClass = s => (s === 'delivered' ? 'ok' : s === 'planned' ? 'deferred' : 'op');

    function uuid() {
        if (window.crypto && window.crypto.randomUUID) { return window.crypto.randomUUID(); }
        return 'xxxxxxxx-xxxx-4xxx-yxxx-xxxxxxxxxxxx'.replace(/[xy]/g, c => {
            const r = Math.random() * 16 | 0;
            return (c === 'x' ? r : (r & 0x3 | 0x8)).toString(16);
        });
    }

    function safeDecode(s) {
        try { return decodeURIComponent(s); } catch (e) { return s; }
    }

    async function copy(text, button) {
        try {
            await navigator.clipboard.writeText(text);
            const done = h('span', { class: 'copied', role: 'status' }, 'Copied');
            button.after(done);
            setTimeout(() => done.remove(), 1500);
        } catch (e) {
            button.title = 'Copy is not available in this browser: select the text instead.';
        }
    }

    // ----------------------------------------------------------------------------------------------- host status
    function docMeta(name) { return (config.documents || []).find(d => d.name === name); }
    function docState(name) { return (state.status && state.status.documents && state.status.documents[name]) || 'unknown'; }
    function hostById(id) { return (state.status.hosts || []).find(x => x.id === id); }

    const readinessText = hs => !hs.live ? 'not answering' : hs.detail ? `${hs.readiness} - ${hs.detail}` : hs.readiness;
    const servedText = hs => !hs.indexRead ? 'unknown' : hs.documents.length === 0 ? 'none' : `${hs.documents.length}: ${hs.documents.join(', ')}`;
    const hostTitle = hs => hs.live ? `${hs.name} at ${hs.baseUrl}: ${hs.readiness}` : `${hs.name} at ${hs.baseUrl} is not answering. ${hs.detail}`;

    /** Rewrites every status-dependent text the server rendered (same wording as ConsolePages). */
    function applyStatus(status) {
        state.status = status;
        for (const hs of status.hosts) {
            const metric = $(`.topbar .metric[data-host="${hs.id}"]`);
            if (metric) {
                metric.title = hostTitle(hs);
                $('.dot-live', metric).className = 'dot-live ' + (hs.live ? 'up' : 'down');
            }
            const card = $(`[data-host-panel="${hs.id}"]`);
            if (card) {
                $('header .dot-live', card).className = 'dot-live ' + (hs.live ? 'up' : 'down');
                $('[data-host-down]', card).hidden = hs.live;
                $('[data-host-detail]', card).textContent = hs.live ? '' : hs.detail;
                $('[data-host-ready]', card).textContent = readinessText(hs);
                $('[data-host-docs]', card).textContent = servedText(hs);
            }
        }
        const names = (config.documents || []).map(d => d.name);
        const delivered = names.filter(n => docState(n) === 'delivered');
        const planned = names.filter(n => docState(n) === 'planned');
        $('#delivered-count').textContent = String(delivered.length);
        for (const section of $$('.side-doc')) { section.classList.toggle('is-planned', docState(section.dataset.doc) === 'planned'); }
        for (const badge of $$('[data-state-for]')) {
            const s = docState(badge.dataset.stateFor);
            badge.textContent = s;
            badge.className = 'badge ' + stateClass(s);
        }
        const stat = key => $(`[data-stat="${key}"]`);
        if (stat('delivered')) { stat('delivered').textContent = String(delivered.length); }
        if (stat('planned')) { stat('planned').textContent = String(planned.length); }
        const bar = $('#delivery-bar');
        if (bar) {
            const ops = list => list.reduce((sum, n) => sum + ((docMeta(n) || {}).operations || 0), 0);
            const total = ops(names) || 1;
            const done = ops(delivered);
            $('.done', bar).style.width = (done * 100 / total) + '%';
            $('.todo', bar).style.width = (ops(planned) * 100 / total) + '%';
            bar.setAttribute('aria-label', `${done} of ${ops(names)} operations delivered`);
        }
        const problems = $('#problems');
        if (problems) { problems.replaceChildren(...status.problems.map(p => note('err', BANG, null, p))); }
        for (const holder of $$('[data-availability]')) { holder.replaceChildren(...availability(holder.dataset.availability, holder.dataset.host)); }
    }

    async function refreshStatus() {
        const button = $('#refresh'), spinner = $('#busy');
        button.disabled = true;
        spinner.hidden = false;
        try {
            const response = await fetch('/console/status.json?refresh=true', { cache: 'no-store' });
            if (response.ok) { applyStatus(await response.json()); }
        } catch (e) {
            // the last known status stays on screen
        } finally {
            button.disabled = false;
            spinner.hidden = true;
        }
    }

    /** Notes about whether an operation of this document can answer now (planned slice, host down, state unknown). */
    function availability(docName, hostId) {
        const hs = hostById(hostId);
        if (hs && !hs.live) {
            return [note('err', BANG, `${hs.name} is not running`, `${hs.detail} Start it with `, h('code', null, 'scripts/run-local.ps1'),
                ' before using Try it out.')];
        }
        const s = docState(docName);
        if (s === 'planned') {
            return [note('warn', BANG, 'Planned slice: this operation does not run yet',
                `${docName} is not delivered by the running hosts, so this operation answers `, h('code', null, '501'),
                ' with reason code ', h('code', null, 'API0501'), ' until the slice is delivered.')];
        }
        if (s === 'unknown') {
            return [note('', INFO, 'Delivery state unknown', 'A host that serves this document is not answering; use Refresh in the status bar.')];
        }
        return [];
    }

    // -------------------------------------------------------------------------------------------- Try it out settings
    function normalise(s) {
        return {
            company: String((s && s.company) || '').trim().toUpperCase(),
            site: String((s && s.site) || '').trim().toUpperCase(),
            businessDate: String((s && s.businessDate) || '').trim(),
        };
    }

    function stored() {
        try {
            const s = JSON.parse(window.localStorage.getItem(SETTINGS_KEY) || 'null');
            return s && typeof s === 'object' ? normalise(s) : null;
        } catch (e) {
            return null;
        }
    }

    /** platform_getSession under the caller's Windows identity: the identity panel and the default context. */
    async function loadSession() {
        try {
            const response = await fetch(config.apiBaseUrl + '/api/platform/v1/session', {
                credentials: 'include', cache: 'no-store', headers: { accept: 'application/json' },
                signal: window.AbortSignal && AbortSignal.timeout ? AbortSignal.timeout(15000) : undefined,
            });
            const text = await response.text();
            let body = null;
            try { body = JSON.parse(text); } catch (e) { body = null; }
            state.session = { ok: response.ok, status: response.status, body };
        } catch (e) {
            state.session = { ok: false, status: 0, body: null, error: String((e && e.message) || e) };
        }
        return state.session;
    }

    function sessionDefaults() {
        const user = state.session && state.session.ok && state.session.body && state.session.body.user;
        return user ? normalise({ company: user.defaultCompanyCode, site: user.defaultSiteCode, businessDate: '' }) : null;
    }

    async function loadSettings() {
        await loadSession();
        const s = stored();
        const defaults = sessionDefaults();
        if (s) { state.settings = s; state.settingsSource = 'stored'; }
        else if (defaults) { state.settings = defaults; state.settingsSource = 'defaults'; }
        else { state.settings = normalise(null); state.settingsSource = 'none'; }
        updateContextMetric();
    }

    function settingsSourceText() {
        return state.settingsSource === 'stored' ? 'Saved in this browser.'
            : state.settingsSource === 'defaults' ? 'Your STERP defaults.'
                : 'No STERP defaults - enter the company and site to use.';
    }

    function updateContextMetric() {
        const s = state.settings;
        $('#context-value').textContent = s.company
            ? s.company + (s.site ? ' / ' + s.site : '') + (s.businessDate ? ' \u00b7 ' + s.businessDate : '')
            : 'not set';
    }

    // ------------------------------------------------------------------------------------------------ OpenAPI documents
    function loadDocument(name) {
        if (!state.documents.has(name)) {
            const pending = fetch(`/console/openapi/${encodeURIComponent(name)}.json`, { cache: 'no-cache' })
                .then(r => { if (!r.ok) { throw new Error(`${name} is not one of the binding documents (HTTP ${r.status}).`); } return r.json(); });
            pending.catch(() => state.documents.delete(name));
            state.documents.set(name, pending);
        }
        return state.documents.get(name);
    }

    function resolve(doc, node) {
        let n = node, guard = 0;
        while (n && typeof n === 'object' && typeof n.$ref === 'string' && guard++ < 32) {
            if (!n.$ref.startsWith('#/')) { return {}; }
            n = n.$ref.slice(2).split('/').map(p => p.replace(/~1/g, '/').replace(/~0/g, '~')).reduce((o, k) => (o == null ? o : o[k]), doc);
        }
        return n;
    }

    const refName = node => (node && typeof node.$ref === 'string' ? node.$ref.split('/').pop() : null);

    /** Operations in document order: { path, method, item, op, tags }. */
    function operationsOf(doc) {
        const list = [];
        for (const [path, item] of Object.entries(doc.paths || {})) {
            for (const [method, op] of Object.entries(item || {})) {
                if (!METHODS.includes(method) || !op || typeof op !== 'object') { continue; }
                list.push({ path, method, item, op, tags: op.tags && op.tags.length ? op.tags : ['default'] });
            }
        }
        return list;
    }

    function tagsOf(doc, operations) {
        const tags = (doc.tags || []).filter(t => t && t.name).map(t => ({ name: t.name, description: t.description || '' }));
        for (const o of operations) {
            for (const t of o.tags) { if (!tags.some(x => x.name === t)) { tags.push({ name: t, description: '' }); } }
        }
        return tags;
    }

    function serverOf(doc, found) {
        const pick = list => (Array.isArray(list) && list[0] && list[0].url) || null;
        return String(pick(found.op.servers) || pick(found.item.servers) || pick(doc.servers) || config.apiBaseUrl).replace(/\/+$/, '');
    }

    const hostIdOf = url => (url.toLowerCase() === String(config.jobsBaseUrl).toLowerCase() ? 'jobs' : 'api');

    function parametersOf(doc, found) {
        const map = new Map();
        for (const raw of [...(found.item.parameters || []), ...(found.op.parameters || [])]) {
            const p = resolve(doc, raw);
            if (p && p.name && p.in) { map.set(p.in + ':' + p.name, p); }
        }
        return Array.from(map.values());
    }

    function exampleOf(doc, p) {
        if (p.example !== undefined) { return p.example; }
        if (p.examples) {
            const first = resolve(doc, Object.values(p.examples)[0]);
            if (first && first.value !== undefined) { return first.value; }
        }
        const schema = resolve(doc, p.schema) || {};
        if (schema.example !== undefined) { return schema.example; }
        return schema.default;
    }

    /** Request body of an operation: media type, schema and the template the form starts from. */
    function bodyOf(doc, found) {
        const rb = resolve(doc, found.op.requestBody);
        if (!rb || !rb.content) { return null; }
        const entries = Object.entries(rb.content);
        if (entries.length === 0) { return null; }
        const [mediaType, media] = entries.find(([m]) => m.includes('json')) || entries[0];
        let example = media.example;
        if (example === undefined && media.examples) {
            const first = resolve(doc, Object.values(media.examples)[0]);
            example = first && first.value;
        }
        if (example === undefined) { example = sample(doc, media.schema, 0, []); }
        const template = typeof example === 'string' && !mediaType.includes('json') ? example : JSON.stringify(example, null, 2);
        return { required: !!rb.required, description: rb.description || '', mediaType, schema: media.schema, template };
    }

    /** A value shaped like the schema, for a body without a binding example. */
    function sample(doc, schema, depth, stack) {
        if (!schema || depth > 6) { return null; }
        if (schema.$ref && stack.includes(schema.$ref)) { return null; }
        const next = schema.$ref ? stack.concat(schema.$ref) : stack;
        const s = flatten(doc, schema);
        if (s.example !== undefined) { return s.example; }
        if (s.default !== undefined) { return s.default; }
        if (Array.isArray(s.enum) && s.enum.length) { return s.enum[0]; }
        const type = Array.isArray(s.type) ? s.type.find(t => t !== 'null') : s.type;
        if (type === 'object' || s.properties) {
            const o = {};
            for (const [k, v] of Object.entries(s.properties || {})) { o[k] = sample(doc, v, depth + 1, next); }
            return o;
        }
        if (type === 'array') { return [sample(doc, s.items, depth + 1, next)]; }
        if (type === 'integer' || type === 'number') { return 0; }
        if (type === 'boolean') { return false; }
        return s.format === 'date' ? '2026-10-07' : '';
    }

    /** Resolves a $ref and merges allOf into one object view; refName keeps the schema name for display. */
    function flatten(doc, schema) {
        const name = refName(schema);
        let s = resolve(doc, schema) || {};
        if (Array.isArray(s.allOf)) {
            const merged = Object.assign({}, s, { properties: Object.assign({}, s.properties || {}), required: [...(s.required || [])] });
            delete merged.allOf;
            for (const part of s.allOf) {
                const f = flatten(doc, part);
                if (!merged.type && f.type) { merged.type = f.type; }
                if (!merged.description && f.description) { merged.description = f.description; }
                Object.assign(merged.properties, f.properties || {});
                merged.required.push(...(f.required || []));
                if (!merged.items && f.items) { merged.items = f.items; }
                if (merged.additionalProperties === undefined && f.additionalProperties !== undefined) { merged.additionalProperties = f.additionalProperties; }
                if (!name && f.refName && !merged.refName) { merged.refName = f.refName; }
            }
            if (!merged.type && Object.keys(merged.properties).length) { merged.type = 'object'; }
            s = merged;
        }
        return Object.assign({}, s, { refName: name || s.refName || null });
    }

    function typeText(doc, s) {
        const types = Array.isArray(s.type) ? s.type : s.type ? [s.type] : [];
        if (types.includes('array')) {
            const item = flatten(doc, s.items || {});
            return 'array of ' + (item.refName || typeText(doc, item)) + (types.includes('null') ? ' | null' : '');
        }
        if ((types.includes('object') || s.properties) && s.refName) { return s.refName + (types.includes('null') ? ' | null' : ''); }
        const base = types.length ? types.join(' | ') : s.properties ? 'object' : Array.isArray(s.enum) ? 'enum' : 'any';
        return s.format ? `${base} (${s.format})` : base;
    }

    function schemaFacts(s) {
        const facts = [];
        if (s.minLength !== undefined) { facts.push(`min length ${s.minLength}`); }
        if (s.maxLength !== undefined) { facts.push(`max length ${s.maxLength}`); }
        if (s['x-sterp-length'] !== undefined) { facts.push(`legacy length ${s['x-sterp-length']}`); }
        if (s.minimum !== undefined) { facts.push(`minimum ${s.minimum}`); }
        if (s.maximum !== undefined) { facts.push(`maximum ${s.maximum}`); }
        if (s.pattern) { facts.push(`pattern ${s.pattern}`); }
        if (s.default !== undefined) { facts.push(`default ${JSON.stringify(s.default)}`); }
        return facts;
    }

    function describeCell(s) {
        const facts = schemaFacts(s);
        const extensions = Object.entries(s).filter(([k]) => k.startsWith('x-') && k !== 'x-sterp-length' && k !== 'x-sterp-legacy-field');
        return h('td', null,
            s.description ? rich(s.description) : null,
            facts.length ? h('span', { class: 'legacy' }, facts.join(' \u00b7 ')) : null,
            s['x-sterp-legacy-field'] ? h('span', { class: 'legacy' }, s['x-sterp-legacy-field']) : null,
            extensions.map(([k, v]) => h('span', { class: 'legacy' }, `${k}: ${typeof v === 'object' ? JSON.stringify(v) : v}`)),
            Array.isArray(s.enum) ? h('span', { class: 'chips' }, s.enum.map(v => h('span', { class: 'chip' }, v === null ? 'null' : String(v)))) : null);
    }

    /** Field | Type | Description rows of a schema; nested objects and array items are indented. */
    function schemaTable(doc, schema) {
        const rows = [];
        const walk = (node, field, depth, required, stack) => {
            if (depth > 10) { return; }
            const ref = node && node.$ref;
            if (ref && stack.includes(ref)) {
                rows.push(schemaRow(field ?? '(value)', refName(node) + ' (recursive)', h('td'), depth, required));
                return;
            }
            const next = ref ? stack.concat(ref) : stack;
            const s = flatten(doc, node);
            const types = Array.isArray(s.type) ? s.type : [s.type];
            const isArray = types.includes('array');
            let childDepth = depth + 1;
            if (field !== null) {
                rows.push(schemaRow(field, typeText(doc, s), describeCell(s), depth, required));
            } else if (isArray) {
                rows.push(schemaRow('[ ]', typeText(doc, s), describeCell(s), depth, false));
            } else if (!s.properties) {
                rows.push(schemaRow('(value)', typeText(doc, s), describeCell(s), depth, false));
            } else {
                childDepth = depth;
            }
            if (s.properties) {
                for (const [k, v] of Object.entries(s.properties)) { walk(v, k, childDepth, (s.required || []).includes(k), next); }
            } else if (isArray && s.items) {
                const itemRef = s.items.$ref;
                if (!(itemRef && next.includes(itemRef))) {
                    const item = flatten(doc, s.items);
                    const itemNext = itemRef ? next.concat(itemRef) : next;
                    for (const [k, v] of Object.entries(item.properties || {})) {
                        walk(v, k, childDepth, (item.required || []).includes(k), itemNext);
                    }
                }
            }
            if (s.additionalProperties && typeof s.additionalProperties === 'object') {
                const extra = flatten(doc, s.additionalProperties);
                rows.push(schemaRow('*', typeText(doc, extra), describeCell(extra), childDepth, false));
            }
        };
        walk(schema, null, 0, false, []);
        return h('table', { class: 'grid schema' },
            h('thead', null, h('tr', null, h('th', null, 'Field'), h('th', null, 'Type'), h('th', null, 'Description'))),
            h('tbody', null, rows));
    }

    function schemaRow(field, type, description, depth, required) {
        const cell = h('td', { class: 'field' }, field, required ? h('span', { class: 'req-star', title: 'Required' }, '*') : null);
        cell.style.paddingLeft = (10 + depth * 16) + 'px';
        return h('tr', null, cell, h('td', { class: 'type' }, type), description);
    }

    function schemaBox(doc, schema, label) {
        const name = refName(schema) || flatten(doc, schema).refName;
        return lazyDetails('schema-box', [label, name ? h('span', { class: 'schema-name' }, ' \u00b7 ' + name) : null], () => schemaTable(doc, schema));
    }

    // --------------------------------------------------------------------------------------------- legacy traceability
    function traceValue(key, value, parentKey) {
        if (value === null || value === undefined || value === '' || (Array.isArray(value) && value.length === 0)) {
            return h('span', { class: 'muted' }, 'none');
        }
        if (Array.isArray(value)) {
            if (value.some(v => v && typeof v === 'object')) {
                return h('ul', null, value.map(v => h('li', null, traceValue(key, v, parentKey))));
            }
            if (key === 'x-sterp-requirements') { return h('span', { class: 'chips' }, value.map(v => h('span', { class: 'req-pill' }, String(v)))); }
            if (parentKey === 'x-sterp-legacy' && key === 'cobol') { return h('span', { class: 'chips' }, value.map(v => h('span', { class: 'cobol' }, String(v)))); }
            if (value.every(v => String(v).length <= 48 && !/\s/.test(String(v)))) {
                return h('span', { class: 'chips' }, value.map(v => h('span', { class: 'chip' }, String(v))));
            }
            return h('ul', null, value.map(v => h('li', null, rich(String(v)))));
        }
        if (typeof value === 'object') {
            return h('dl', { class: 'kv' }, Object.entries(value).map(([k, v]) => [h('dt', null, k), h('dd', null, traceValue(k, v, key))]));
        }
        if (parentKey === 'x-sterp-legacy' && key === 'cobol') { return h('span', { class: 'cobol' }, String(value)); }
        if (typeof value === 'string' && value.length <= 48 && !/\s/.test(value)) { return h('span', { class: 'chip' }, value); }
        return h('span', null, rich(String(value)));
    }

    function tracePanel(op) {
        const keys = Object.keys(op).filter(k => k.startsWith('x-'));
        if (keys.length === 0) { return null; }
        keys.sort((a, b) => {
            const ia = TRACE_ORDER.indexOf(a), ib = TRACE_ORDER.indexOf(b);
            return (ia < 0 ? 99 : ia) - (ib < 0 ? 99 : ib);
        });
        return panel(['Legacy traceability', spacer(), h('span', { class: 'badge op' }, 'x-sterp-*')],
            h('dl', { class: 'kv trace' }, keys.map(k => [h('dt', null, k), h('dd', null, traceValue(k, op[k], null))])));
    }

    // ------------------------------------------------------------------------------------------------- command lines
    const shQuote = s => "'" + String(s).replace(/'/g, "'\\''") + "'";
    const cmdQuote = s => '"' + String(s).replace(/"/g, '\\"') + '"';
    const psQuote = s => "'" + String(s).replace(/'/g, "''") + "'";

    function compactBody(text) {
        try { return JSON.stringify(JSON.parse(text)); } catch (e) { return String(text).replace(/\r?\n/g, ' '); }
    }

    function curlBash(r) {
        const args = ['curl --negotiate -u : -X ' + shQuote(r.method), shQuote(r.url)];
        r.headers.forEach(([k, v]) => args.push('-H ' + shQuote(k + ': ' + v)));
        if (r.body !== null) { args.push('-d ' + shQuote(compactBody(r.body))); }
        return args.join(' \\\n  ');
    }

    function curlCmd(r) {
        const args = ['curl.exe --negotiate -u : -X ' + r.method, cmdQuote(r.url)];
        r.headers.forEach(([k, v]) => args.push('-H ' + cmdQuote(k + ': ' + v)));
        if (r.body !== null) { args.push('-d ' + cmdQuote(compactBody(r.body))); }
        return args.join(' ');
    }

    function powerShell(r) {
        let line = 'Invoke-RestMethod -UseDefaultCredentials -Method ' + r.method.charAt(0) + r.method.slice(1).toLowerCase() + ' -Uri ' + psQuote(r.url);
        let contentType = null;
        const pairs = [];
        r.headers.forEach(([k, v]) => {
            if (k.toLowerCase() === 'content-type') { contentType = v; } else { pairs.push(psQuote(k) + ' = ' + psQuote(v)); }
        });
        if (pairs.length) { line += ' -Headers @{ ' + pairs.join('; ') + ' }'; }
        if (contentType) { line += ' -ContentType ' + psQuote(contentType); }
        if (r.body !== null) { line += ' -Body ' + psQuote(compactBody(r.body)); }
        return line;
    }

    const GENERATORS = [
        { title: 'bash / Git Bash (curl)', make: curlBash },
        { title: 'Windows Command Prompt (curl.exe)', make: curlCmd },
        { title: 'PowerShell (Invoke-RestMethod)', make: powerShell },
    ];

    function snippets(request) {
        return GENERATORS.map(g => {
            let text;
            try { text = g.make(request); } catch (e) { text = 'Command line not available: ' + e.message; }
            const button = h('button', { class: 'btn sm', type: 'button' }, 'Copy');
            button.addEventListener('click', () => copy(text, button));
            return h('div', { class: 'snippet' },
                h('div', { class: 'snippet-head' }, h('span', null, g.title), spacer(), button),
                h('pre', { class: 'code' }, text));
        });
    }

    // ------------------------------------------------------------------------------------------------ operation page
    function acceptOf(doc, op) {
        const types = new Set();
        for (const [code, raw] of Object.entries(op.responses || {})) {
            if (!String(code).startsWith('2')) { continue; }
            for (const m of Object.keys((resolve(doc, raw) || {}).content || {})) { types.add(m); }
        }
        return types.size ? Array.from(types).join(', ') : 'application/json';
    }

    /** Context headers come from the settings; X-STERP-Run-As, Idempotency-Key and other headers are never prefilled. */
    function initialValue(doc, p) {
        if (p.in === 'header') {
            const setting = CONTEXT_HEADERS[String(p.name).toLowerCase()];
            return setting ? state.settings[setting] || '' : '';
        }
        const example = exampleOf(doc, p);
        return example === undefined || example === null ? '' : String(example);
    }

    function lazyDetails(className, summary, build) {
        const details = h('details', { class: className }, h('summary', null, summary));
        details.addEventListener('toggle', () => {
            if (details.open && !details.dataset.built) { details.dataset.built = '1'; append(details, [build()]); }
        });
        return details;
    }

    function responseDetails(doc, code, raw, platformWide) {
        const r = resolve(doc, raw) || {};
        const chip = code === '501' ? 'c501' : 'c' + String(code).charAt(0);
        return lazyDetails('resp', [h('span', { class: 'code-chip ' + chip }, code), h('span', { class: 'text' }, rich(r.description || REASONS[code] || '')),
            platformWide ? h('span', { class: 'badge op', title: 'Not declared by the operation: every operation of a slice that is not delivered answers it' }, 'any operation') : null], () => {
            const content = Object.entries(r.content || {});
            return h('div', { class: 'resp-body' }, content.length === 0 ? h('p', { class: 'muted' }, 'No body.') : content.map(([mediaType, media]) => {
                let example = media.example;
                if (example === undefined && media.examples) { example = (resolve(doc, Object.values(media.examples)[0]) || {}).value; }
                return [
                    h('div', { class: 'media' }, h('span', { class: 'badge op' }, mediaType)),
                    media.schema ? schemaBox(doc, media.schema, 'Schema') : null,
                    example === undefined ? null : [h('div', { class: 'example-title' }, 'Example'),
                        h('pre', { class: 'code light' }, typeof example === 'string' ? example : JSON.stringify(example, null, 2))],
                ];
            }));
        });
    }

    function decode(buffer, contentType) {
        const m = /charset=([^;]+)/i.exec(contentType || '');
        try { return new TextDecoder(m ? m[1].trim().replace(/"/g, '') : 'utf-8').decode(buffer); }
        catch (e) { return new TextDecoder('utf-8').decode(buffer); }
    }

    /** The STERP outcome envelope or problem details as one plain sentence above the body. */
    function outcomeNote(result, json, hostName, server) {
        if (!result.sent) {
            return note('err', BANG, 'The service did not answer',
                `${hostName} at ${server} did not respond (${result.error}). Check in the status bar that it is running, that its `,
                'certificate is trusted and that this console\'s origin is listed in ', h('code', null, 'Sterp:Host:CorsOrigins'), '.');
        }
        if (!json || typeof json !== 'object') { return null; }
        const reason = json.reasonCode || (json.outcome && json.outcome.reasonCode) || '';
        if (result.status === 501 && reason === 'API0501') {
            return note('warn', BANG, 'Planned slice', 'The host answered 501 with reason code API0501: the operation belongs to a slice that is ',
                'not delivered yet, exactly as the catalogue describes.');
        }
        if (result.status === 403 && reason === 'API0010') {
            return note('err', BANG, 'No STERP user is mapped to your Windows login', json.detail || '',
                ' Calls run under your own Windows identity, so an administrator has to map it to an STERP user (guide section 5.2).');
        }
        if (result.status === 401) {
            return note('err', BANG, 'Windows authentication failed', 'The browser did not negotiate your Windows identity with the host.');
        }
        if (json.outcome && typeof json.outcome === 'object') {
            const o = json.outcome;
            const kind = o.returnCode === 0 ? 'evidence' : o.returnCode <= 6 ? 'warn' : 'err';
            return note(kind, kind === 'evidence' ? CHECK : BANG,
                `RC ${pad2(o.returnCode)} ${o.severity || ''}${o.reasonCode ? ' \u00b7 ' + o.reasonCode : ''}`, o.message || '');
        }
        if (typeof json.status === 'number' && (json.title || json.type)) {
            const rc = json.returnCode === undefined || json.returnCode === null ? '' : ' \u00b7 RC ' + pad2(json.returnCode);
            return note(json.status >= 500 ? 'err' : 'warn', BANG, `${json.title || 'Problem'}${reason ? ' \u00b7 ' + reason : ''}${rc}`, json.detail || '');
        }
        return null;
    }

    function responsePanel(request, result, hostName, server) {
        let shown = result.text || '', json = null;
        if (result.sent && /json/i.test(result.contentType) && shown.trim()) {
            try { json = JSON.parse(shown); shown = JSON.stringify(json, null, 2); } catch (e) { json = null; }
        }
        const pill = !result.sent ? 'is-error' : result.status === 501 ? 'is-deferred'
            : result.status < 300 ? 'is-ok' : result.status < 500 ? 'is-warn' : 'is-err';
        const outcome = outcomeNote(result, json, hostName, server);
        return panel(['Response', spacer(), h('span', { class: 'badge op' }, request.method)], null, {
            raw: [
                h('div', { class: 'result-bar' },
                    result.sent ? [h('span', { class: 'status-pill ' + pill }, String(result.status)), h('span', { class: 'metric' }, result.statusText)]
                        : h('span', { class: 'status-pill is-error' }, 'no answer'),
                    h('span', { class: 'metric' }, h('strong', null, String(result.elapsed)), ' ms'),
                    result.sent ? h('span', { class: 'metric' }, h('strong', null, String(result.bytes)), ' bytes') : null,
                    spacer(),
                    h('span', { class: 'metric mono' }, request.url)),
                outcome ? h('div', { class: 'body outcome' }, outcome) : null,
                shown.trim() ? h('pre', { class: 'code' }, shown) : null,
                h('div', { class: 'body' },
                    h('dl', { class: 'kv' },
                        request.headers.map(([k, v]) => [h('dt', null, '\u2192 ' + k), h('dd', null, v)]),
                        (result.headers || []).map(([k, v]) => [h('dt', null, '\u2190 ' + k), h('dd', null, v)])),
                    h('p', { class: 'muted footnote' }, 'The browser negotiates your Windows identity with the host itself; only the ',
                        'response headers the host exposes to the console are listed.')),
            ],
        });
    }

    function operationPage(docName, doc, found) {
        const op = found.op;
        const method = found.method.toUpperCase();
        const server = serverOf(doc, found);
        const hostId = hostIdOf(server);
        const hostName = hostId === 'jobs' ? 'STERP.Jobs' : 'STERP.Api';
        const params = parametersOf(doc, found);
        const body = bodyOf(doc, found);
        const safe = method === 'GET' || method === 'HEAD' || method === 'OPTIONS';
        const writes = !safe && !/^read[ -]only/i.test(String(op['x-sterp-uow'] || ''));
        const accept = acceptOf(doc, op);
        const values = new Map(), inputs = new Map(), errors = new Map();
        const fieldKey = p => p.in + ':' + p.name;
        const commandsBox = h('div');
        const responseBox = h('div', { 'aria-live': 'polite' });
        let bodyText = body ? body.template : '';
        let textarea = null, bodyError = null;
        let pendingKey = null, keyUsed = false, lastSent = null;

        const changed = () => { lastSent = null; renderCommands(); };
        const renderCommands = () => commandsBox.replaceChildren(...snippets(lastSent || prepare()));

        function clearError(key) {
            const e = errors.get(key), i = inputs.get(key);
            if (e) { e.hidden = true; }
            if (i) { i.classList.remove('is-invalid'); }
        }

        const fields = params.map(p => {
            const schema = flatten(doc, p.schema || {});
            const key = fieldKey(p);
            const id = ('p-' + p.in + '-' + p.name).toLowerCase().replace(/[^a-z0-9-]+/g, '-');
            const example = exampleOf(doc, p);
            const choices = Array.isArray(schema.enum) ? schema.enum.filter(v => v !== null).map(String)
                : (Array.isArray(schema.type) ? schema.type : [schema.type]).includes('boolean') ? ['true', 'false'] : null;
            const input = choices
                ? h('select', { id }, h('option', { value: '' }, p.required ? '(choose)' : '(not sent)'), choices.map(v => h('option', { value: v }, v)))
                : h('input', {
                    id, type: 'text', autocomplete: 'off', spellcheck: 'false',
                    placeholder: PLACEHOLDERS[String(p.name).toLowerCase()] || (example === undefined || example === null ? '' : String(example)),
                });
            const update = () => { values.set(key, input.value); clearError(key); changed(); };
            input.addEventListener('input', update);
            input.addEventListener('change', update);
            inputs.set(key, input);
            const error = h('div', { class: 'field-error', hidden: true });
            errors.set(key, error);
            const facts = [typeText(doc, schema), ...schemaFacts(schema), schema['x-sterp-legacy-field']].filter(Boolean).join(' \u00b7 ');
            return [
                h('label', { for: id }, p.name, p.required ? h('span', { class: 'req-star', title: 'Required' }, '*') : null,
                    h('span', { class: 'in-tag' }, p.in),
                    p.description ? h('span', { class: 'hint' }, rich(p.description)) : null,
                    h('span', { class: 'schema-hint' }, facts)),
                h('div', null, input, error),
            ];
        });

        function prepare() {
            let path = found.path;
            const query = [];
            const headers = [['accept', accept]];
            for (const p of params) {
                const v = values.get(fieldKey(p)) || '';
                if (p.in === 'path') { path = path.split('{' + p.name + '}').join(encodeURIComponent(v)); }
                else if (p.in === 'query' && v !== '') { query.push(encodeURIComponent(p.name) + '=' + encodeURIComponent(v)); }
                else if (p.in === 'header' && v !== '') { headers.push([p.name, v]); }
            }
            if (!safe && !headers.some(([k]) => k.toLowerCase() === 'idempotency-key')) {
                if (!pendingKey || keyUsed) { pendingKey = uuid(); keyUsed = false; }
                headers.push(['Idempotency-Key', pendingKey]);
            }
            const sendBody = body && bodyText.trim() !== '' ? bodyText : null;
            if (sendBody !== null) { headers.push(['Content-Type', body.mediaType.includes('json') ? 'application/json' : body.mediaType]); }
            return { method, url: server + path + (query.length ? '?' + query.join('&') : ''), headers, body: sendBody };
        }

        function validate() {
            let first = null;
            for (const p of params) {
                if (p.in !== 'path' || !p.required || (values.get(fieldKey(p)) || '') !== '') { continue; }
                const e = errors.get(fieldKey(p)), i = inputs.get(fieldKey(p));
                e.textContent = 'Required: the path cannot be built without it.';
                e.hidden = false;
                i.classList.add('is-invalid');
                first = first || i;
            }
            if (body && body.mediaType.includes('json') && bodyText.trim() !== '') {
                try { JSON.parse(bodyText); } catch (e) {
                    bodyError.textContent = 'The body is not valid JSON: ' + e.message;
                    bodyError.hidden = false;
                    textarea.classList.add('is-invalid');
                    first = first || textarea;
                }
            }
            if (first) { first.focus(); }
            return first === null;
        }

        const executeButton = h('button', { class: 'btn primary', type: 'button' }, 'Execute');
        const resetButton = h('button', { class: 'btn', type: 'button' }, 'Reset form');

        async function execute() {
            if (executeButton.disabled || !validate()) { return; }
            const request = prepare();
            keyUsed = true;
            lastSent = request;
            renderCommands();
            const spin = h('span', { class: 'spinner', 'aria-hidden': 'true' });
            executeButton.prepend(spin);
            executeButton.disabled = resetButton.disabled = true;
            responseBox.setAttribute('aria-busy', 'true');
            activity.begin();
            try {
                const started = performance.now();
                let result;
                try {
                    const response = await fetch(request.url, {
                        method: request.method, headers: request.headers, body: request.body ?? undefined,
                        credentials: 'include', cache: 'no-store', mode: 'cors',
                    });
                    const buffer = await response.arrayBuffer();
                    const contentType = response.headers.get('content-type') || '';
                    result = {
                        sent: true, status: response.status, statusText: response.statusText || REASONS[response.status] || '',
                        elapsed: Math.round(performance.now() - started), bytes: buffer.byteLength, contentType,
                        text: decode(buffer, contentType), headers: Array.from(response.headers.entries()),
                    };
                } catch (e) {
                    result = { sent: false, elapsed: Math.round(performance.now() - started), error: String((e && e.message) || e) };
                }
                responseBox.replaceChildren(responsePanel(request, result, hostName, server));
                responseBox.scrollIntoView({ block: 'nearest' });
            } finally {
                spin.remove();
                executeButton.disabled = resetButton.disabled = false;
                responseBox.removeAttribute('aria-busy');
                activity.end();
            }
        }

        function reset() {
            values.clear();
            for (const p of params) {
                const v = initialValue(doc, p);
                values.set(fieldKey(p), v);
                inputs.get(fieldKey(p)).value = v;
                clearError(fieldKey(p));
            }
            bodyText = body ? body.template : '';
            if (textarea) { textarea.value = bodyText; textarea.classList.remove('is-invalid'); bodyError.hidden = true; }
            pendingKey = null;
            keyUsed = false;
            lastSent = null;
            responseBox.replaceChildren();
            renderCommands();
        }

        executeButton.addEventListener('click', execute);
        resetButton.addEventListener('click', reset);

        let bodyPanel = null;
        if (body) {
            textarea = h('textarea', { rows: Math.min(18, Math.max(6, bodyText.split('\n').length + 1)), spellcheck: 'false', 'aria-label': 'Request body (' + body.mediaType + ')' });
            textarea.addEventListener('input', () => { bodyText = textarea.value; textarea.classList.remove('is-invalid'); bodyError.hidden = true; changed(); });
            bodyError = h('div', { class: 'field-error', hidden: true });
            const resetBody = h('button', { class: 'btn sm', type: 'button' }, 'Reset to example');
            resetBody.addEventListener('click', () => { textarea.value = bodyText = body.template; textarea.classList.remove('is-invalid'); bodyError.hidden = true; changed(); });
            bodyPanel = panel(['Request body', body.required ? h('span', { class: 'req-star', title: 'Required' }, '*') : null,
                h('span', { class: 'badge op' }, body.mediaType), spacer(), resetBody],
                [body.description ? h('p', { class: 'panel-text' }, rich(body.description)) : null, textarea, bodyError,
                    body.schema ? schemaBox(doc, body.schema, 'Schema') : null]);
        }

        const paramsPanel = params.length ? panel('Parameters', h('div', { class: 'field-grid' }, fields)) : null;
        const sendPanel = panel('Send', [
            h('div', { class: 'btn-row' }, executeButton, resetButton,
                writes ? h('span', { class: 'badge deferred' }, 'Writes to the database') : null,
                h('span', { class: 'metric' }, 'as your Windows identity on ', h('strong', null, hostName), ' ', server)),
            h('h2', { class: 'section' }, 'Command lines (Windows authentication)'),
            commandsBox,
        ]);
        (paramsPanel || bodyPanel || sendPanel).id = 'try-it';

        function jumpToForm() {
            const target = $('#try-it');
            const reduced = window.matchMedia && window.matchMedia('(prefers-reduced-motion: reduce)').matches;
            target.scrollIntoView({ block: 'start', behavior: reduced ? 'auto' : 'smooth' });
            const first = target.querySelector('input, select, textarea, button');
            if (first) { first.focus({ preventScroll: true }); }
        }

        const responses = Object.entries(op.responses || {});
        // contracts 5.10: an operation of a slice that is not delivered answers 501 API0501 even where it does not declare it
        const notImplemented = !op.responses || op.responses['501'] || !(doc.components && doc.components.responses && doc.components.responses.NotImplemented)
            ? null : responseDetails(doc, '501', { $ref: '#/components/responses/NotImplemented' }, true);
        const state0 = docState(docName);
        const root = h('div', { class: 'operation' },
            h('div', { class: 'endpoint-head' },
                h('span', { class: 'verb ' + found.method }, method),
                h('span', { class: 'route' }, found.path),
                h('span', { class: 'req', title: 'operationId' }, op.operationId),
                h('span', { class: 'badge ' + stateClass(state0), 'data-state-for': docName }, state0),
                h('span', { class: 'badge op', title: server }, `${hostName} \u00b7 ${op['x-sterp-engine'] || (hostId === 'jobs' ? 'batch' : 'online')}`),
                op.deprecated ? h('span', { class: 'badge err' }, 'deprecated') : null,
                spacer(),
                h('button', { class: 'btn sm', type: 'button', onclick: jumpToForm }, 'Try it out')),
            h('p', { class: 'lede' }, op.summary || ''),
            h('div', { 'data-availability': docName, 'data-host': hostId }, availability(docName, hostId)),
            op.description ? panel('Description', h('div', { class: 'desc' }, paragraphs(op.description))) : null,
            tracePanel(op),
            paramsPanel,
            bodyPanel,
            sendPanel,
            responseBox,
            responses.length ? panel(['Responses', spacer(), h('span', { class: 'badge op' }, responses.length + ' documented')], null,
                { raw: h('div', null, responses.map(([code, raw]) => responseDetails(doc, code, raw, false)), notImplemented) }) : null);
        reset();
        return root;
    }

    // ------------------------------------------------------------------------------------------------- document page
    const slug = text => String(text).toLowerCase().replace(/[^a-z0-9]+/g, '-').replace(/^-+|-+$/g, '');
    const hashFor = (doc, op, tag) => '#/' + encodeURIComponent(doc) + (op ? '/' + encodeURIComponent(op) : tag ? '/tags/' + encodeURIComponent(tag) : '');
    const stat = (n, label, cls) => h('div', { class: 'stat' + (cls || '') }, h('div', { class: 'n' }, String(n)), h('div', { class: 'l' }, label));

    function firstSentence(text) {
        const t = String(text || '').replace(/\s+/g, ' ').trim();
        const end = t.search(/\.\s/);
        const sentence = end > 0 ? t.slice(0, end + 1) : t;
        return sentence.length > 320 ? sentence.slice(0, sentence.lastIndexOf(' ', 300)) + ' ...' : sentence;
    }

    function documentPage(name, doc) {
        const info = doc.info || {};
        const operations = operationsOf(doc);
        const tags = tagsOf(doc, operations);
        const meta = docMeta(name) || {};
        const docStateNow = docState(name);
        const servers = [...new Set([
            ...(doc.servers || []).map(s => s.url),
            ...operations.flatMap(o => [...(o.item.servers || []), ...(o.op.servers || [])].map(s => s.url)),
        ])];
        const servedBy = (state.status.hosts || []).filter(x => x.documents.includes(name)).map(x => x.name);
        const hostId = name === 'STERP-JOBS' ? 'jobs' : 'api';
        return h('div', { class: 'document' },
            h('h1', null, info.title || name),
            h('p', { class: 'lede' }, rich(firstSentence(info.description))),
            h('div', { class: 'stat-row' },
                stat(operations.length, operations.length === 1 ? 'operation' : 'operations'),
                stat(tags.length, tags.length === 1 ? 'group' : 'groups'),
                stat(meta.wave || '-', 'wave'),
                h('div', { class: 'stat' }, h('div', { class: 'n' }, h('span', { class: 'badge ' + stateClass(docStateNow), 'data-state-for': name }, docStateNow)),
                    h('div', { class: 'l' }, 'delivery state'))),
            h('div', { 'data-availability': name, 'data-host': hostId }, availability(name, hostId)),
            panel(['Document', spacer(), h('span', { class: 'badge op' }, 'OpenAPI ' + (doc.openapi || ''))],
                h('dl', { class: 'kv' },
                    h('dt', null, 'name'), h('dd', null, name),
                    h('dt', null, 'version'), h('dd', null, info.version || ''),
                    h('dt', null, 'web service'), h('dd', null, meta.webService || ''),
                    h('dt', null, 'servers'), h('dd', null, servers.join(', ')),
                    h('dt', null, 'served by'), h('dd', null, servedBy.length ? servedBy.join(', ') : 'no host (binding document only)'),
                    h('dt', null, 'download'), h('dd', null,
                        h('a', { href: `/docs/${encodeURIComponent(name)}.yaml` }, name + '.yaml'), ' \u00b7 ',
                        h('a', { href: `/docs/${encodeURIComponent(name)}.json` }, name + '.json')))),
            info.description ? panel('Description', h('div', { class: 'desc' }, paragraphs(info.description))) : null,
            tags.map(t => {
                const ops = operations.filter(o => o.tags.includes(t.name));
                if (ops.length === 0) { return null; }
                return panel([h('a', { href: hashFor(name, null, t.name) }, t.name), spacer(),
                    h('span', { class: 'badge op' }, ops.length + (ops.length === 1 ? ' operation' : ' operations'))], [
                    t.description ? h('p', { class: 'panel-text' }, rich(t.description)) : null,
                    h('table', { class: 'grid' },
                        h('thead', null, h('tr', null, ['Method', 'Path', 'Operation', 'Summary'].map(x => h('th', null, x)))),
                        h('tbody', null, ops.map(o => h('tr', null,
                            h('td', { class: 'nowrap' }, h('span', { class: 'verb sm ' + o.method }, o.method.toUpperCase())),
                            h('td', { class: 'path' }, o.path),
                            h('td', { class: 'nowrap' }, h('a', { class: 'op-link', href: hashFor(name, o.op.operationId) }, o.op.operationId)),
                            h('td', null, rich(o.op.summary || '')))))),
                ], { id: 'tag-' + slug(t.name), tight: true });
            }));
    }

    // -------------------------------------------------------------------------------------------- contract drift
    const DIFFERENCE = {
        missing_from_console: 'Host has it, console does not',
        missing_from_host: 'Console describes it, host does not',
    };
    const DRIFT_STATE = {
        'in-sync': ['ok', 'in sync'],
        drift: ['err', 'differs'],
        'not-served': ['deferred', 'not served (planned)'],
        unreachable: ['err', 'unreachable'],
        unknown: ['op', 'unknown'],
    };

    function driftReport(report) {
        const count = s => report.documents.filter(d => d.state === s).length;
        return h('div', null,
            h('div', { class: 'stat-row' },
                stat(count('in-sync'), 'in sync'),
                stat(count('drift'), 'differ', count('drift') ? ' err' : ''),
                stat(count('not-served'), 'not served (planned)', ' warn'),
                stat(count('unreachable') + count('unknown'), 'not checked')),
            report.hosts.filter(x => !x.reachable).map(x => note('err', BANG, `Could not read ${x.baseUrl}/openapi/index.json`, x.error || '')),
            report.documents.map(d => {
                const [cls, text] = DRIFT_STATE[d.state] || ['op', d.state];
                const differences = d.copies.flatMap(c => c.differences.map(x => Object.assign({ host: c.host }, x)));
                return panel([h('a', { href: hashFor(d.name) }, d.name), h('span', { class: 'muted' }, d.title), spacer(), h('span', { class: 'badge ' + cls }, text)], [
                    h('dl', { class: 'kv' },
                        h('dt', null, 'binding sha-256'), h('dd', null, d.sha256),
                        h('dt', null, 'operations'), h('dd', null, String(d.operations)),
                        d.copies.length === 0
                            ? [h('dt', null, 'published by'), h('dd', null, d.state === 'not-served' ? 'no host (binding document only)' : 'unknown: a host is not answering')]
                            : d.copies.map(c => [h('dt', null, c.host), h('dd', null, c.error ? 'not compared: ' + c.error
                                : c.identical ? 'identical bytes' : `differs: sha-256 ${c.sha256}, ${c.operations} operations`)])),
                    differences.length === 0 ? null : h('table', { class: 'grid spaced-top' },
                        h('thead', null, h('tr', null, ['Difference', 'Host', 'Method', 'Path'].map(x => h('th', null, x)))),
                        h('tbody', null, differences.map(x => h('tr', null,
                            h('td', { class: 'nowrap' }, DIFFERENCE[x.kind] || x.kind),
                            h('td', { class: 'nowrap' }, x.host),
                            h('td', { class: 'nowrap' }, h('span', { class: 'verb sm ' + x.method.toLowerCase() }, x.method)),
                            h('td', { class: 'path' }, x.path))))),
                ]);
            }));
    }

    function driftPage() {
        const out = h('div', { 'aria-live': 'polite' }, state.drift ? driftReport(state.drift)
            : note('', INFO, null, 'Not checked yet. The hosts must be running for this to report anything.'));
        const button = h('button', { class: 'btn primary', type: 'button' }, 'Check against live hosts');
        button.addEventListener('click', async () => {
            const spin = h('span', { class: 'spinner', 'aria-hidden': 'true' });
            button.prepend(spin);
            button.disabled = true;
            out.setAttribute('aria-busy', 'true');
            activity.begin();
            try {
                const response = await fetch('/console/drift.json', { cache: 'no-store' });
                if (!response.ok) { throw new Error('HTTP ' + response.status); }
                state.drift = await response.json();
                out.replaceChildren(driftReport(state.drift));
            } catch (e) {
                out.replaceChildren(note('err', BANG, 'The check did not complete', String((e && e.message) || e)));
            } finally {
                spin.remove();
                button.disabled = false;
                out.removeAttribute('aria-busy');
                activity.end();
            }
        });
        return h('div', null,
            h('h1', null, 'Contract drift'),
            h('p', { class: 'lede' }, 'The console renders the thirteen binding OpenAPI documents it carries itself. This check proves they are ',
                'still exactly what STERP.Api and STERP.Jobs publish: byte for byte, and operation by operation where the bytes differ.'),
            h('div', { class: 'btn-row spaced' }, button),
            out);
    }

    // -------------------------------------------------------------------------------------- Try it out settings page
    function identityBody() {
        const s = state.session;
        if (!s) { return note('', INFO, null, 'Not checked yet.'); }
        if (s.ok && s.body && s.body.user) {
            const b = s.body, u = b.user;
            return h('dl', { class: 'kv' },
                h('dt', null, 'Windows login'), h('dd', null, b.windowsLogin || u.windowsLogin || ''),
                h('dt', null, 'STERP user'), h('dd', null, u.userCode + (u.displayName ? ' - ' + u.displayName : '')),
                h('dt', null, 'SYSADM'), h('dd', null, b.isSysAdm ? 'yes (may send X-STERP-Run-As)' : 'no'),
                h('dt', null, 'default company / site'), h('dd', null, `${u.defaultCompanyCode || '-'} / ${u.defaultSiteCode || '-'}`),
                h('dt', null, 'access'), h('dd', null, (b.access || []).map(a => `${a.companyCode}/${a.siteCode}`).join(', ') || 'none'),
                h('dt', null, 'business date'), h('dd', null, b.businessDate || ''));
        }
        if (s.status === 401 || s.status === 403) {
            const detail = (s.body && (s.body.detail || s.body.title)) || 'HTTP ' + s.status;
            return note('err', BANG, s.status === 403 ? 'No STERP user is mapped to your Windows login' : 'Windows authentication failed', detail);
        }
        return note('err', BANG, 'STERP.Api did not answer', s.error || 'HTTP ' + s.status);
    }

    function settingsPage() {
        const company = h('input', { id: 'set-company', type: 'text', maxlength: '10', autocomplete: 'off', spellcheck: 'false' });
        const site = h('input', { id: 'set-site', type: 'text', maxlength: '10', autocomplete: 'off', spellcheck: 'false' });
        const date = h('input', { id: 'set-date', type: 'date' });
        const source = h('span', { class: 'metric', role: 'status' }, settingsSourceText());
        const show = () => {
            company.value = state.settings.company;
            site.value = state.settings.site;
            date.value = state.settings.businessDate;
            source.textContent = settingsSourceText();
            updateContextMetric();
        };
        const save = h('button', { class: 'btn primary', type: 'button' }, 'Save');
        save.addEventListener('click', () => {
            state.settings = normalise({ company: company.value, site: site.value, businessDate: date.value });
            state.settingsSource = 'stored';
            window.localStorage.setItem(SETTINGS_KEY, JSON.stringify(state.settings));
            show();
        });
        const defaults = h('button', { class: 'btn', type: 'button' }, 'Use my defaults');
        defaults.addEventListener('click', () => {
            window.localStorage.removeItem(SETTINGS_KEY);
            const d = sessionDefaults();
            state.settings = d || normalise(null);
            state.settingsSource = d ? 'defaults' : 'none';
            show();
        });
        const identity = h('div', null, identityBody());
        const recheck = h('button', { class: 'btn sm', type: 'button' }, 'Check again');
        recheck.addEventListener('click', async () => {
            recheck.disabled = true;
            identity.setAttribute('aria-busy', 'true');
            try { await loadSession(); identity.replaceChildren(identityBody()); }
            finally { recheck.disabled = false; identity.removeAttribute('aria-busy'); }
        });
        show();
        return h('div', null,
            h('h1', null, 'Try it out settings'),
            h('p', { class: 'lede' }, 'The context headers X-STERP-Company, X-STERP-Site and X-STERP-Business-Date of every operation are ',
                'prefilled from these settings and can still be changed per call. They are kept in this browser only.'),
            panel('Context headers', [
                h('div', { class: 'field-grid' },
                    h('label', { for: 'set-company' }, 'Company', h('span', { class: 'hint' }, 'X-STERP-Company: the company code of the session.')),
                    h('div', null, company),
                    h('label', { for: 'set-site' }, 'Site', h('span', { class: 'hint' }, 'X-STERP-Site: required by site-scoped operations.')),
                    h('div', null, site),
                    h('label', { for: 'set-date' }, 'Business date',
                        h('span', { class: 'hint' }, 'X-STERP-Business-Date: blank = the server date, as in the legacy client.')),
                    h('div', null, date)),
                h('div', { class: 'btn-row spaced-top' }, save, defaults, source),
            ]),
            panel(['Your identity', spacer(), h('span', { class: 'badge op' }, 'platform_getSession'), recheck], identity),
            note('', INFO, null, 'Calls run under your own Windows identity: the browser negotiates it with the host on every call. ',
                h('code', null, 'X-STERP-Run-As'), ' (SYSADM "Continue as") and ', h('code', null, 'Idempotency-Key'),
                ' are never prefilled, so a call never runs as another user by accident.'));
    }

    // ------------------------------------------------------------------------------------------------ operation tree
    const tree = $$('.side-doc').map(section => ({
        section,
        name: section.dataset.doc,
        service: $('.side-service', section),
        tags: $$('.side-tag', section).map(tag => ({
            tag,
            items: $$('.side-item', tag).map(item => ({ item, op: item.dataset.op, search: item.dataset.search || '' })),
        })),
    }));

    function applyFilter() {
        const terms = state.filter.toLowerCase().split(/\s+/).filter(Boolean);
        let any = false;
        for (const d of tree) {
            const inScope = !state.definition || d.name === state.definition;
            let docAny = false;
            for (const t of d.tags) {
                let tagAny = false;
                for (const i of t.items) {
                    const show = inScope && terms.every(term => i.search.includes(term));
                    i.item.hidden = !show;
                    tagAny = tagAny || show;
                }
                t.tag.hidden = !tagAny;
                docAny = docAny || tagAny;
            }
            d.section.hidden = !docAny;
            any = any || docAny;
        }
        const empty = $('#side-empty');
        empty.hidden = any;
        empty.textContent = any ? '' : `Nothing matches "${state.filter.trim()}".`;
    }

    function highlight(route) {
        for (const a of $$('.side-nav a')) {
            const active = a.dataset.nav === route.view;
            a.classList.toggle('is-active', active);
            if (active) { a.setAttribute('aria-current', 'page'); } else { a.removeAttribute('aria-current'); }
        }
        let current = null;
        for (const d of tree) {
            d.service.classList.toggle('is-active', route.view === 'document' && route.doc === d.name);
            for (const t of d.tags) {
                for (const i of t.items) {
                    const active = route.view === 'operation' && route.doc === d.name && route.op === i.op;
                    i.item.classList.toggle('is-active', active);
                    if (active) { i.item.setAttribute('aria-current', 'page'); current = current || i.item; }
                    else { i.item.removeAttribute('aria-current'); }
                }
            }
        }
        if (current && !current.hidden) { current.scrollIntoView({ block: 'nearest' }); }
    }

    // ---------------------------------------------------------------------------------------------------------- routes
    // #/ overview, #/{doc}, #/{doc}/tags/{tag}, #/{doc}/{operationId}, #/drift, #/settings; other fragments are page anchors
    function currentRoute() {
        const hash = location.hash || '';
        if (!hash.startsWith('#/')) { return { view: config.view === 'guide' ? 'guide' : 'overview' }; }
        const parts = hash.slice(2).split('/').filter(Boolean).map(safeDecode);
        if (parts.length === 0) { return { view: 'overview' }; }
        if (parts[0] === 'drift' || parts[0] === 'settings') { return { view: parts[0] }; }
        if (parts.length >= 3 && parts[1] === 'tags') { return { view: 'document', doc: parts[0], tag: parts.slice(2).join('/') }; }
        if (parts.length >= 2) { return { view: 'operation', doc: parts[0], op: parts[1] }; }
        return { view: 'document', doc: parts[0] };
    }

    function navigate(hash) {
        if (location.hash === hash) { render().catch(report); } else { location.hash = hash; }
    }

    function notFound(message) {
        return h('div', null, h('h1', null, 'Not found'), note('err', BANG, null, message), h('p', null, h('a', { href: '#/' }, 'Back to the overview')));
    }

    async function render() {
        const route = currentRoute();
        if (config.view === 'guide' && route.view !== 'guide') {
            location.replace('/' + location.hash);
            return;
        }
        const token = ++state.renderToken;
        state.route = route;
        if (state.definition && route.doc && route.doc !== state.definition) {
            state.definition = route.doc;
            $('#definition').value = route.doc;
            applyFilter();
        }
        highlight(route);
        const overview = $('#view-overview'), guide = $('#view-guide'), app = $('#view-app'), main = $('#main');
        const fixed = route.view === 'overview' || route.view === 'guide';
        if (overview) { overview.hidden = !fixed; }
        if (guide) { guide.hidden = !fixed; }
        app.hidden = fixed;
        if (fixed) {
            // a superseded document render leaves the busy flag to the newest render, which is this one
            main.removeAttribute('aria-busy');
            app.replaceChildren();
            document.title = route.view === 'guide' ? 'How to call the STERP APIs - ' + TITLE : TITLE;
            return;
        }
        main.setAttribute('aria-busy', 'true');
        activity.begin();
        try {
            let node, title;
            if (route.view === 'drift') { node = driftPage(); title = 'Contract drift'; }
            else if (route.view === 'settings') { node = settingsPage(); title = 'Try it out settings'; }
            else {
                let doc = null, failure = null;
                try { doc = await loadDocument(route.doc); } catch (e) { failure = e; }
                if (token !== state.renderToken) { return; }
                const found = doc && route.view === 'operation' ? operationsOf(doc).find(o => o.op.operationId === route.op) : null;
                if (!doc) { node = notFound(String((failure && failure.message) || failure)); title = 'Not found'; }
                else if (route.view === 'document') { node = documentPage(route.doc, doc); title = route.doc; }
                else if (found) { node = operationPage(route.doc, doc, found); title = route.op; }
                else { node = notFound(`${route.doc} has no operation ${route.op}.`); title = 'Not found'; }
            }
            app.replaceChildren(node);
            document.title = title + ' - ' + TITLE;
            const anchor = route.tag ? document.getElementById('tag-' + slug(route.tag)) : null;
            if (anchor) { anchor.scrollIntoView({ block: 'start' }); } else { main.scrollTop = 0; }
        } finally {
            if (token === state.renderToken) { main.removeAttribute('aria-busy'); }
            activity.end();
        }
    }

    // -------------------------------------------------------------------------------------------------------- start-up
    function report(e) {
        if (window.console) { window.console.error('STERP API Console:', e); }
    }

    function bind() {
        const filter = $('#filter'), definition = $('#definition');
        filter.addEventListener('input', () => { state.filter = filter.value; applyFilter(); });
        filter.addEventListener('keydown', e => {
            if (e.key === 'Escape' && filter.value) { filter.value = ''; state.filter = ''; applyFilter(); }
        });
        definition.addEventListener('change', () => {
            state.definition = definition.value;
            applyFilter();
            if (state.definition) { navigate(hashFor(state.definition)); }
        });
        $('#refresh').addEventListener('click', refreshStatus);
        window.addEventListener('hashchange', () => { render().catch(report); });
        document.addEventListener('keydown', e => {
            if (e.key !== '/' || e.ctrlKey || e.altKey || e.metaKey) { return; }
            const t = e.target;
            if (t && (t.isContentEditable || /^(INPUT|TEXTAREA|SELECT)$/.test(t.tagName))) { return; }
            e.preventDefault();
            filter.focus();
        });
    }

    async function start() {
        bind();
        // a restored form state would show a filter or definition the tree does not reflect
        $('#filter').value = '';
        $('#definition').value = '';
        applyFilter();
        try { await loadSettings(); } catch (e) { report(e); }
        try { await render(); } catch (e) { report(e); }
        window.sterpConsole.started = true;
        activity.startupDone();
    }

    start();
})();
