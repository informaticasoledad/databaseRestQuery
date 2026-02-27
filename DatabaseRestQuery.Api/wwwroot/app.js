const qs = (s) => document.querySelector(s);
const statusCards = qs("#status-cards");
const pendingBody = qs("#pending-table tbody");
const metricsPreview = qs("#metrics-preview");
const historyRequestsBody = qs("#history-requests-table tbody");
const historyResponsesBody = qs("#history-responses-table tbody");
const responseViewer = qs("#response-viewer");
const requestBody = qs("#request-body");
const endpointSelect = qs("#endpoint-select");
const txidWrap = qs("#txid-wrap");
const txidInput = qs("#txid-input");
const openapiViewer = qs("#openapi-viewer");
const endpointDocs = qs("#endpoint-docs");

const endpointDefinitions = [
  {
    method: "POST",
    path: "/doQuery",
    purpose: "Ejecuta consulta directa o encola trabajo.",
    input: {
      connectionName: "as400",
      server: { type: "postgresql|sqlserver|sqlserver_legacy|freetds|mysql|db2-iseries", connstr: "..." },
      transactionId: "tx-001",
      query: "select 1",
      command: { commandTimeout: 30, commandText: "select 1", params: [{ name: "@id", value: 1 }] },
      executionTimeout: 30,
      rowsLimit: 100,
      waitForResponse: true,
      useQueue: false,
      compressResult: false,
      streamResult: false,
      queuePartition: "tenant-a",
      responseFormat: "json|jsonl",
      responseQueueCallback: "https://mi-sistema/callback",
      exportToS3: false,
      exportFormat: "jsonl",
      exportCompress: true
    },
    output: { transactionId: "tx-001", ok: true, message: "Consulta ejecutada.", result: [{ col: "value" }], compressedResult: null }
  },
  {
    method: "GET",
    path: "/checkResponse/{transactionId}",
    purpose: "Consulta estado y resultado de una solicitud encolada.",
    input: { transactionId: "tx-001 (path)" },
    output: { transactionId: "tx-001", ok: true, message: "Completado", result: [], compressedResult: null }
  },
  {
    method: "GET",
    path: "/queuePendingJobs",
    purpose: "Lista trabajos pendientes/procesando.",
    input: {},
    output: [{ transactionId: "tx-001", status: "Pending", attempts: 0, maxAttempts: 3, partitionKey: "tenant-a", nextAttemptAt: "2026-02-25T12:00:00Z" }]
  },
  {
    method: "POST",
    path: "/queuePurge",
    purpose: "Elimina trabajos en estado Pending.",
    input: {},
    output: { ok: true, message: "Se eliminaron N mensajes pendientes de la cola." }
  },
  {
    method: "GET",
    path: "/health",
    purpose: "Estado del servicio y politica de colas.",
    input: {},
    output: { ok: true, service: "DatabaseRestQuery.Api", runMode: "All", queue: { pending: 0, processing: 0, completed: 0, failed: 0, delayedRetry: 0 } }
  },
  {
    method: "GET",
    path: "/metrics",
    purpose: "Métricas Prometheus para monitoreo.",
    input: {},
    output: "# HELP ... (texto Prometheus)"
  },
  {
    method: "GET",
    path: "/openapi/v1.json",
    purpose: "Definición OpenAPI en JSON.",
    input: {},
    output: { openapi: "3.x.x", paths: {} }
  },
  {
    method: "GET",
    path: "/historyRecent?limit=30",
    purpose: "Historial de ultimas peticiones y respuestas.",
    input: { limit: 30 },
    output: [{ transactionId: "tx-001", channel: "queue|direct", requestJson: "{...}", responseJson: "{...}", ok: true, message: "..." }]
  }
];

const defaultDoQuery = {
  connectionName: null,
  server: {
    type: "postgresql",
    connstr: "Host=localhost;Port=5432;Database=test;Username=postgres;Password=postgres"
  },
  transactionId: "tx-demo-001",
  command: {
    commandTimeout: 10,
    commandText: "select now() as fecha",
    params: []
  },
  executionTimeout: 30,
  rowsLimit: 100,
  waitForResponse: true,
  useQueue: false,
  compressResult: false,
  streamResult: false,
  queuePartition: "tenant-demo",
  responseFormat: "json",
  responseQueueCallback: null,
  exportToS3: false,
  exportFormat: "jsonl",
  exportCompress: true
};

function setDefaultBody() {
  if (endpointSelect.value === "doQuery") {
    requestBody.value = JSON.stringify(defaultDoQuery, null, 2);
    requestBody.disabled = false;
  } else if (endpointSelect.value === "queuePurge") {
    requestBody.value = "{}";
    requestBody.disabled = false;
  } else {
    requestBody.value = "";
    requestBody.disabled = true;
  }
}

function renderKpis(health) {
  const q = health.queue || {};
  const policy = health.responseQueuePolicy || {};
  const cards = [
    ["RunMode", health.runMode ?? "n/a"],
    ["Pending", q.pending ?? 0],
    ["Processing", q.processing ?? 0],
    ["Completed", q.completed ?? 0],
    ["Failed", q.failed ?? 0],
    ["DelayedRetry", q.delayedRetry ?? 0],
    ["Resp.MaxItems", policy.maxItems ?? "n/a"],
    ["Resp.Retention(h)", policy.retentionHours ?? "n/a"]
  ];

  statusCards.innerHTML = cards
    .map(([label, value]) => `<div class="kpi"><div class="label">${label}</div><div class="value">${value}</div></div>`)
    .join("");
}

function renderPending(items) {
  pendingBody.innerHTML = "";
  if (!items || items.length === 0) {
    pendingBody.innerHTML = `<tr><td colspan="5">Sin trabajos pendientes.</td></tr>`;
    return;
  }

  pendingBody.innerHTML = items.map((x) => `
    <tr>
      <td>${x.transactionId}</td>
      <td>${x.status}</td>
      <td>${x.attempts}/${x.maxAttempts}</td>
      <td>${x.partitionKey ?? "default"}</td>
      <td>${x.nextAttemptAt ?? "-"}</td>
    </tr>
  `).join("");
}

function safeParseJson(raw) {
  if (!raw) return null;
  try { return JSON.parse(raw); } catch { return null; }
}

function compact(text, max = 180) {
  if (!text) return "-";
  const s = String(text);
  return s.length > max ? `${s.slice(0, max)}...` : s;
}

function renderHistory(items) {
  const requests = (items || []);
  const responses = requests.filter(x => x.responseJson);

  historyRequestsBody.innerHTML = requests.length === 0
    ? `<tr><td colspan="4">Sin historial.</td></tr>`
    : requests.map((x) => {
      const req = safeParseJson(x.requestJson) || {};
      return `<tr>
        <td>${x.transactionId}</td>
        <td>${x.channel}</td>
        <td>${req.useQueue ?? "-"}</td>
        <td>${x.createdAt ?? "-"}</td>
      </tr>`;
    }).join("");

  historyResponsesBody.innerHTML = responses.length === 0
    ? `<tr><td colspan="4">Sin respuestas.</td></tr>`
    : responses.map((x) => {
      const parsed = safeParseJson(x.responseJson);
      const responseText = parsed ? JSON.stringify(parsed) : x.responseJson;
      return `<tr>
        <td>${x.transactionId}</td>
        <td>${x.ok ?? "-"}</td>
        <td>${compact(x.message, 90)}</td>
        <td><code>${compact(responseText, 240)}</code></td>
      </tr>`;
    }).join("");
}

async function fetchJson(url, init) {
  const res = await fetch(url, init);
  const text = await res.text();
  let body;
  try { body = text ? JSON.parse(text) : null; } catch { body = text; }
  return { ok: res.ok, status: res.status, body };
}

async function refreshStatus() {
  const [health, pending, metrics, history] = await Promise.all([
    fetchJson("/health"),
    fetchJson("/queuePendingJobs"),
    fetch("/metrics").then(r => r.text()),
    fetchJson("/historyRecent?limit=30")
  ]);

  if (health.ok) renderKpis(health.body);
  if (pending.ok) renderPending(pending.body);
  if (history.ok) renderHistory(history.body);
  metricsPreview.textContent = metrics.split("\n").slice(0, 60).join("\n");
}

async function purgeQueue() {
  if (!confirm("Se eliminarán todos los Pending. ¿Continuar?")) return;
  const res = await fetchJson("/queuePurge", { method: "POST", headers: { "Content-Type": "application/json" }, body: "{}" });
  responseViewer.textContent = JSON.stringify({ status: res.status, body: res.body }, null, 2);
  await refreshStatus();
}

async function executeRequest() {
  const endpoint = endpointSelect.value;
  let url = "";
  let init = {};

  if (endpoint === "doQuery") {
    url = "/doQuery";
    init = { method: "POST", headers: { "Content-Type": "application/json" }, body: requestBody.value || "{}" };
  } else if (endpoint === "checkResponse") {
    const txid = txidInput.value.trim();
    if (!txid) {
      alert("Debes indicar transactionId");
      return;
    }
    url = `/checkResponse/${encodeURIComponent(txid)}`;
  } else if (endpoint === "queuePendingJobs") {
    url = "/queuePendingJobs";
  } else if (endpoint === "queuePurge") {
    url = "/queuePurge";
    init = { method: "POST", headers: { "Content-Type": "application/json" }, body: requestBody.value || "{}" };
  } else {
    url = "/health";
  }

  const result = await fetchJson(url, init);
  responseViewer.textContent = JSON.stringify({ status: result.status, body: result.body }, null, 2);
  await refreshStatus();
}

function renderDocs() {
  endpointDocs.innerHTML = endpointDefinitions.map((x) => `
    <div class="endpoint-item">
      <strong><code>${x.method}</code> <code>${x.path}</code></strong>
      <p>${x.purpose}</p>
      <p><strong>Input</strong></p>
      <pre>${JSON.stringify(x.input, null, 2)}</pre>
      <p><strong>Output</strong></p>
      <pre>${typeof x.output === "string" ? x.output : JSON.stringify(x.output, null, 2)}</pre>
    </div>
  `).join("");
}

async function loadOpenApi() {
  try {
    const res = await fetch("/openapi/v1.json");
    if (!res.ok) {
      openapiViewer.textContent = `OpenAPI no disponible. status=${res.status}`;
      return;
    }
    const json = await res.json();
    openapiViewer.textContent = JSON.stringify(json, null, 2);
  } catch (err) {
    openapiViewer.textContent = String(err);
  }
}

function wireTabs() {
  document.querySelectorAll(".tab").forEach(btn => {
    btn.addEventListener("click", () => {
      document.querySelectorAll(".tab").forEach(x => x.classList.remove("active"));
      document.querySelectorAll(".panel").forEach(x => x.classList.remove("active"));
      btn.classList.add("active");
      qs(`#tab-${btn.dataset.tab}`).classList.add("active");
    });
  });
}

endpointSelect.addEventListener("change", () => {
  txidWrap.classList.toggle("hidden", endpointSelect.value !== "checkResponse");
  setDefaultBody();
});

qs("#refresh-status").addEventListener("click", refreshStatus);
qs("#purge-queue").addEventListener("click", purgeQueue);
qs("#execute-request").addEventListener("click", executeRequest);

wireTabs();
setDefaultBody();
renderDocs();
loadOpenApi();
refreshStatus();
setInterval(refreshStatus, 8000);
