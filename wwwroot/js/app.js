// ============================================================
// Passport redaction demo: front end
// ============================================================

let viewerInstance = null;
let currentDocName = null;
let currentDocCategory = "td3"; // category of the doc currently in the viewer

const VIEWER_CONTAINER = "#viewer-container";
// Nutrient Web SDK assets (same version as the script tag in index.html).
// No license key: the viewer runs in evaluation mode.
const NUTRIENT_CDN_BASE_URL = "https://cdn.cloud.pspdfkit.com/pspdfkit-web@1.13.1/";

// ============================================================
// Document categories — single source of truth.
// Drives BOTH the sidebar accordion groups AND the redaction
// modal tabs. `id` must match the input-docs subfolder name and
// the backend DocType. Each `field.value` must match a field the
// backend redaction calculus understands.
// ============================================================
const PASSPORT_FIELDS = [
  { value: "photo",          label: "Profile Picture", checked: true },
  { value: "surname",        label: "Surname",         checked: true },
  { value: "givenNames",     label: "Given Names",     checked: true },
  { value: "dateOfBirth",    label: "Date of Birth",   checked: true },
  { value: "sex",            label: "Sex" },
  { value: "nationality",    label: "Nationality" },
  { value: "countryCode",    label: "Country Code" },
  { value: "documentNumber", label: "Document Number", checked: true },
  { value: "expiryDate",     label: "Expiry Date" },
  { value: "dateOfIssue",    label: "Date of Issue" },
  { value: "placeOfBirth",   label: "Place of Birth" },
  { value: "placeOfIssue",   label: "Place of Issue" },
  { value: "signature",      label: "Signature" },
  { value: "mrz",            label: "MRZ Zone",        checked: true },
];

const CATEGORIES = [
  { id: "td3", label: "Passports", fields: PASSPORT_FIELDS },
  {
    id: "td1", label: "Card ID-1",
    fields: [
      { value: "photo",          label: "Profile Picture", checked: true },
      { value: "surname",        label: "Surname",         checked: true },
      { value: "givenNames",     label: "Given Names",     checked: true },
      { value: "documentNumber", label: "Document Number", checked: true },
      { value: "dateOfBirth",    label: "Date of Birth",   checked: true },
      { value: "sex",            label: "Sex" },
      { value: "nationality",    label: "Nationality" },
      { value: "expiryDate",     label: "Expiry Date" },
      { value: "signature",      label: "Signature" },
      { value: "mrz",            label: "MRZ Zone",        checked: true },
    ],
  },
  {
    id: "td2", label: "Card/Document ID-2",
    fields: [
      { value: "photo",          label: "Profile Picture", checked: true },
      { value: "surname",        label: "Surname",         checked: true },
      { value: "givenNames",     label: "Given Names",     checked: true },
      { value: "documentNumber", label: "Document Number", checked: true },
      { value: "dateOfBirth",    label: "Date of Birth",   checked: true },
      { value: "sex",            label: "Sex" },
      { value: "nationality",    label: "Nationality" },
      { value: "expiryDate",     label: "Expiry Date" },
      { value: "mrz",            label: "MRZ Zone",        checked: true },
    ],
  },
  {
    id: "aadhaar", label: "Aadhaar",
    fields: [
      { value: "photo",          label: "Profile Picture", checked: true },
      { value: "givenNames",     label: "Name",            checked: true },
      { value: "dateOfBirth",    label: "Date of Birth",   checked: true },
      { value: "sex",            label: "Gender" },
      { value: "documentNumber", label: "Aadhaar Number",  checked: true },
    ],
  },
];

const CATEGORY_BY_ID = Object.fromEntries(CATEGORIES.map((c) => [c.id, c]));
let activeTab = "td3"; // currently selected modal tab

// --- Passport SVG icon for the toolbar button ---
const PASSPORT_ICON = `
<svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 24 24" width="24" height="24">
  <rect x="3" y="2" width="18" height="20" rx="2" ry="2"
        fill="none" stroke="currentColor" stroke-width="1.8"/>
  <circle cx="12" cy="10" r="3.5"
          fill="none" stroke="currentColor" stroke-width="1.5"/>
  <line x1="6"  y1="16" x2="18" y2="16" stroke="currentColor" stroke-width="1.2"/>
  <line x1="6"  y1="18.5" x2="18" y2="18.5" stroke="currentColor" stroke-width="1.2"/>
  <line x1="12" y1="2"  x2="12" y2="5" stroke="currentColor" stroke-width="1.2"/>
</svg>`;

// ============================================================
// Init
// ============================================================

document.addEventListener("DOMContentLoaded", () => {
  buildModal();
  loadDocumentList();
  setupModalHandlers();
  document.getElementById("qr-panel-close")?.addEventListener("click", hideQrPanel);
});

// ============================================================
// Sidebar — collapsible accordion, one group per category
// ============================================================

const FILE_ICON = `
  <svg width="16" height="16" viewBox="0 0 24 24" fill="none"
       stroke="currentColor" stroke-width="2">
    <path d="M14 2H6a2 2 0 0 0-2 2v16a2 2 0 0 0 2 2h12a2 2 0 0 0 2-2V8z"/>
    <polyline points="14 2 14 8 20 8"/>
  </svg>`;

const CHEVRON_ICON = `
  <svg class="chevron" width="16" height="16" viewBox="0 0 24 24" fill="none"
       stroke="currentColor" stroke-width="2">
    <polyline points="9 18 15 12 9 6"/>
  </svg>`;

async function loadDocumentList() {
  const container = document.getElementById("document-list");
  container.innerHTML = "";

  let grouped = {};
  try {
    const res = await fetch("/api/documents");
    grouped = await res.json();
  } catch (err) {
    showToast("Failed to load document list", "error");
    console.error(err);
    return;
  }

  // First group expanded by default, the rest collapsed.
  CATEGORIES.forEach((cat, idx) => {
    const docs = grouped[cat.id] || [];

    const group = document.createElement("div");
    group.className = "doc-group" + (idx === 0 ? " open" : "");

    const header = document.createElement("button");
    header.type = "button";
    header.className = "doc-group-header";
    header.innerHTML = `${CHEVRON_ICON}<span>${cat.label}</span><span class="doc-count">${docs.length}</span>`;

    const ul = document.createElement("ul");
    ul.className = "doc-group-list";

    if (docs.length === 0) {
      const empty = document.createElement("li");
      empty.className = "doc-empty";
      empty.textContent = "No documents";
      ul.appendChild(empty);
    } else {
      docs.forEach((doc) => {
        const li = document.createElement("li");
        li.innerHTML = `${FILE_ICON}<span>${doc.name}</span>`;
        li.addEventListener("click", () => selectDocument(doc, li));
        ul.appendChild(li);
      });
    }

    header.addEventListener("click", () => group.classList.toggle("open"));

    group.appendChild(header);
    group.appendChild(ul);
    container.appendChild(group);
  });
}

async function selectDocument(doc, listItem) {
  // Update sidebar selection across all groups
  document.querySelectorAll("#document-list li").forEach((li) => li.classList.remove("active"));
  listItem.classList.add("active");

  currentDocName = doc.name;
  currentDocCategory = doc.category || "td3";
  hideQrPanel(); // new document — clear any previous QR readout
  await loadDocumentInViewer(doc.url, doc.name);
}

// ============================================================
// QR data side panel
// ============================================================

function showQrPanel(title, fields) {
  const panel = document.getElementById("qr-panel");
  const body = document.getElementById("qr-panel-body");
  document.getElementById("qr-panel-title").textContent = title || "QR Data";

  body.innerHTML = fields.map((f) => `
    <div class="qr-row">
      ${f.label ? `<div class="qr-label">${escapeHtml(f.label)}</div>` : ""}
      <div class="qr-value">${escapeHtml(f.value)}</div>
    </div>`).join("");

  panel.style.display = "flex";
}

function hideQrPanel() {
  const panel = document.getElementById("qr-panel");
  if (panel) panel.style.display = "none";
}

// Map a parsed MRZ object to ordered label/value rows for the panel.
function mrzToFields(mrz) {
  const map = [
    ["surname", "Surname"],
    ["givenNames", "Given Names"],
    ["documentNumber", "Document Number"],
    ["nationality", "Nationality"],
    ["issuingState", "Issuing State"],
    ["dateOfBirth", "Date of Birth"],
    ["sex", "Sex"],
    ["expiryDate", "Expiry Date"],
  ];
  return map
    .filter(([key]) => mrz[key] != null && String(mrz[key]).trim() !== "")
    .map(([key, label]) => ({ label, value: String(mrz[key]).trim() }));
}

function escapeHtml(s) {
  return String(s ?? "")
    .replace(/&/g, "&amp;").replace(/</g, "&lt;").replace(/>/g, "&gt;")
    .replace(/"/g, "&quot;").replace(/'/g, "&#39;");
}

// ============================================================
// Nutrient Viewer
// ============================================================

async function loadDocumentInViewer(url, docName) {
  const container = document.querySelector(VIEWER_CONTAINER);
  document.getElementById("empty-state").style.display = "none";

  // Always try to unload any previous instance
  try {
    PSPDFKit.unload(container);
  } catch (_) {}
  viewerInstance = null;

  try {
    // Custom passport redaction toolbar button
    const passportButton = {
      type: "custom",
      id: "passport-redact",
      title: "Redact Passport",
      icon: PASSPORT_ICON,
      onPress: () => openRedactionModal(),
    };

    const toolbarItems = [
      ...PSPDFKit.defaultToolbarItems,
      { type: "spacer" },
      passportButton,
    ];

    // Reset the apply button when loading a new document
    document.getElementById("redaction-action-bar")?.remove();

    const loadConfig = {
      container,
      document: url,
      baseUrl: NUTRIENT_CDN_BASE_URL,
      toolbarItems,
      theme: PSPDFKit.Theme.DARK,
    };

    viewerInstance = await PSPDFKit.load(loadConfig);

    console.log("Document loaded:", docName);
  } catch (err) {
    console.error("Failed to load document:", err);
    showToast("Failed to load document: " + err.message, "error");
  }
}

// ============================================================
// Redaction modal
// ============================================================

// Build the tab bar + one field-grid panel per category from CATEGORIES.
function buildModal() {
  const tabBar = document.getElementById("modal-tabs");
  const panels = document.getElementById("modal-tab-panels");
  tabBar.innerHTML = "";
  panels.innerHTML = "";

  CATEGORIES.forEach((cat) => {
    const tab = document.createElement("button");
    tab.type = "button";
    tab.className = "modal-tab";
    tab.dataset.tab = cat.id;
    tab.textContent = cat.label;
    tab.addEventListener("click", () => setActiveTab(cat.id));
    tabBar.appendChild(tab);

    const panel = document.createElement("div");
    panel.className = "field-grid tab-panel";
    panel.dataset.tab = cat.id;
    panel.innerHTML = cat.fields.map((f) => `
      <label class="field-option">
        <input type="checkbox" value="${f.value}" ${f.checked ? "checked" : ""} />
        <span class="checkmark"></span>
        <span class="field-label">${f.label}</span>
      </label>`).join("");
    panels.appendChild(panel);
  });

  setActiveTab(activeTab);
}

// Show one tab's panel, mark its tab active. Resets the Select All label.
function setActiveTab(tabId) {
  if (!CATEGORY_BY_ID[tabId]) tabId = "td3";
  activeTab = tabId;
  document.querySelectorAll(".modal-tab").forEach((t) =>
    t.classList.toggle("active", t.dataset.tab === tabId));
  document.querySelectorAll(".tab-panel").forEach((p) =>
    p.classList.toggle("active", p.dataset.tab === tabId));

  const btnSelectAll = document.getElementById("btn-select-all");
  if (btnSelectAll) btnSelectAll.textContent = "Select All";
}

// Return the checkboxes belonging to the currently active tab panel only.
function activePanelCheckboxes() {
  const panel = document.querySelector(`.tab-panel[data-tab="${activeTab}"]`);
  return panel ? Array.from(panel.querySelectorAll('input[type="checkbox"]')) : [];
}

function setupModalHandlers() {
  const modal = document.getElementById("redaction-modal");
  const btnCancel = document.getElementById("btn-cancel");
  const btnRedact = document.getElementById("btn-redact");
  const btnSelectAll = document.getElementById("btn-select-all");

  btnCancel.addEventListener("click", () => (modal.style.display = "none"));

  // Close on overlay click
  modal.addEventListener("click", (e) => {
    if (e.target === modal) modal.style.display = "none";
  });

  // Select all / deselect all toggle — scoped to the active tab
  btnSelectAll.addEventListener("click", () => {
    const checkboxes = activePanelCheckboxes();
    const allChecked = checkboxes.every((cb) => cb.checked);
    checkboxes.forEach((cb) => (cb.checked = !allChecked));
    btnSelectAll.textContent = allChecked ? "Select All" : "Deselect All";
  });

  // Redact button — uses the active tab's fields + that tab as the docType
  btnRedact.addEventListener("click", () => {
    const selectedFields = activePanelCheckboxes()
      .filter((cb) => cb.checked)
      .map((cb) => cb.value);

    if (selectedFields.length === 0) {
      showToast("Please select at least one field to redact.", "info");
      return;
    }

    modal.style.display = "none";
    performRedaction(selectedFields, activeTab);
  });

  // ESC key to close
  document.addEventListener("keydown", (e) => {
    if (e.key === "Escape") modal.style.display = "none";
  });
}

function openRedactionModal() {
  if (!currentDocName) {
    showToast("No document loaded.", "info");
    return;
  }
  // Default to the tab matching the loaded document's category.
  setActiveTab(currentDocCategory);
  document.getElementById("redaction-modal").style.display = "flex";
}

// ============================================================
// Redaction API call
// ============================================================

async function performRedaction(fields, docType = "td3") {
  showLoading("Detecting redaction zones...");

  try {
    const res = await fetch("/api/redaction/preview", {
      method: "POST",
      headers: { "Content-Type": "application/json" },
      body: JSON.stringify({
        fileName: currentDocName,
        fields: fields,
        docType: docType,
      }),
    });

    const result = await res.json();
    hideLoading();

    if (!result.success) {
      showToast(result.message || "Redaction failed.", "error", 5000);
      return;
    }

    // Show parsed document data in the right panel: QR contents for
    // Aadhaar, or the parsed MRZ for passports.
    if (result.qrData && result.qrData.length) {
      showQrPanel(result.qrFormat || "QR Data", result.qrData);
    } else if (result.mrzData) {
      const mrzFields = mrzToFields(result.mrzData);
      if (mrzFields.length) showQrPanel("Passport MRZ", mrzFields);
      else hideQrPanel();
    } else {
      hideQrPanel();
    }

    // Load the processed (deskewed/rotated) PDF in the viewer, then place
    // moveable RedactionAnnotation markers at the detected coordinates.
    await loadDocumentInViewer(result.previewDocumentUrl, currentDocName);
    await placeRedactionMarkers(result.redactions);
    showApplyRedactionsButton();
    showToast(
      `${result.redactions.length} markers placed. Drag to adjust, then click "Apply Redactions".`,
      "success", 6000);
  } catch (err) {
    hideLoading();
    console.error("Redaction error:", err);
    showToast("An error occurred during redaction. Check the console.", "error", 5000);
  }
}

// IDs of marker rectangles we placed — used on "Apply Redactions" to convert
// them into real RedactionAnnotations at their (possibly moved) positions.
let redactionMarkerIds = [];

// Place RectangleAnnotations (which are moveable/resizable in trial mode)
// instead of RedactionAnnotations directly. They get converted on Apply.
async function placeRedactionMarkers(redactions) {
  if (!viewerInstance || !redactions?.length) return;
  redactionMarkerIds = [];
  const annotations = redactions.map((r) => new PSPDFKit.Annotations.RectangleAnnotation({
    pageIndex: r.pageIndex,
    boundingBox: new PSPDFKit.Geometry.Rect({
      left: r.x, top: r.y, width: r.width, height: r.height,
    }),
    strokeColor: new PSPDFKit.Color({ r: 220, g: 30, b: 30 }),
    strokeWidth: 2,
    fillColor: new PSPDFKit.Color({ r: 220, g: 30, b: 30 }),
    opacity: 0.35,
    customData: { passportRedactionMarker: true, field: r.field },
  }));
  const created = await viewerInstance.create(annotations);
  redactionMarkerIds = created.map((a) => a.id);
}

// Floating action bar bottom-right: "Add Rectangle" + "Apply Redactions".
function showApplyRedactionsButton() {
  if (document.getElementById("redaction-action-bar")) return;

  const bar = document.createElement("div");
  bar.id = "redaction-action-bar";
  bar.style.cssText =
    "position:fixed;bottom:20px;right:20px;z-index:10000;display:flex;gap:8px;";

  const addBtn = document.createElement("button");
  addBtn.id = "add-rect-btn";
  addBtn.textContent = "+ Add Rectangle";
  addBtn.className = "btn";
  addBtn.style.cssText =
    "padding:12px 20px;font-size:14px;box-shadow:0 4px 12px rgba(0,0,0,0.3);background:#fff;color:#222;border:1px solid #ccc;border-radius:6px;cursor:pointer;";
  addBtn.addEventListener("click", () => addManualRectangle());

  const btn = document.createElement("button");
  btn.id = "apply-redactions-btn";
  btn.textContent = "Apply Redactions";
  btn.className = "btn btn-primary";
  btn.style.cssText =
    "padding:12px 20px;font-size:14px;box-shadow:0 4px 12px rgba(0,0,0,0.3);";
  btn.addEventListener("click", async () => {
    if (!viewerInstance) return;
    btn.disabled = true;
    btn.textContent = "Applying...";
    try {
      // Read the (possibly moved/resized) marker rectangles back from the viewer
      // and convert each to a RedactionAnnotation at its current position.
      const allAnnotations = [];
      const totalPages = viewerInstance.totalPageCount;
      for (let p = 0; p < totalPages; p++) {
        const pageAnnots = await viewerInstance.getAnnotations(p);
        pageAnnots.forEach((a) => allAnnotations.push(a));
      }
      const markers = allAnnotations.filter((a) =>
        redactionMarkerIds.includes(a.id)
        || (a.customData && a.customData.passportRedactionMarker)
      );

      const redactions = markers.map((m) => new PSPDFKit.Annotations.RedactionAnnotation({
        pageIndex: m.pageIndex,
        boundingBox: m.boundingBox,
        rects: PSPDFKit.Immutable.List([m.boundingBox]),
        fillColor: PSPDFKit.Color.BLACK,
        outlineColor: PSPDFKit.Color.RED,
      }));

      // Replace markers with redactions, then commit them.
      if (markers.length > 0)
        await viewerInstance.delete(markers.map((m) => m.id));
      if (redactions.length > 0)
        await viewerInstance.create(redactions);
      await viewerInstance.applyRedactions();

      redactionMarkerIds = [];
      showToast("Redactions applied permanently.", "success");
      document.getElementById("redaction-action-bar")?.remove();
    } catch (e) {
      console.error("applyRedactions failed:", e);
      showToast("Failed to apply redactions: " + e.message, "error", 5000);
      btn.disabled = false;
      btn.textContent = "Apply Redactions";
    }
  });
  bar.appendChild(addBtn);
  bar.appendChild(btn);
  document.body.appendChild(bar);
}

// Add a moveable rectangle marker at the centre of the current page so the
// user can drag/resize it before applying.
async function addManualRectangle() {
  if (!viewerInstance) return;
  const pageIndex = viewerInstance.viewState.currentPageIndex;
  const pageInfo = viewerInstance.pageInfoForIndex(pageIndex);
  const w = Math.min(120, pageInfo.width * 0.25);
  const h = Math.min(40, pageInfo.height * 0.06);
  const left = (pageInfo.width - w) / 2;
  const top = (pageInfo.height - h) / 2;
  const rect = new PSPDFKit.Annotations.RectangleAnnotation({
    pageIndex,
    boundingBox: new PSPDFKit.Geometry.Rect({ left, top, width: w, height: h }),
    strokeColor: new PSPDFKit.Color({ r: 220, g: 30, b: 30 }),
    strokeWidth: 2,
    fillColor: new PSPDFKit.Color({ r: 220, g: 30, b: 30 }),
    opacity: 0.35,
    customData: { passportRedactionMarker: true, field: "manual" },
  });
  const created = await viewerInstance.create([rect]);
  if (created && created.length > 0)
    redactionMarkerIds.push(created[0].id);
}

// ============================================================
// Toast notifications
// ============================================================

function showToast(message, type = "info", duration = 3500) {
  const toast = document.getElementById("toast");
  toast.textContent = message;
  toast.className = `toast ${type}`;
  toast.style.display = "block";
  clearTimeout(toast._timeout);
  toast._timeout = setTimeout(() => (toast.style.display = "none"), duration);
}

// ============================================================
// Loading overlay
// ============================================================

function showLoading(text) {
  document.getElementById("loading-text").textContent = text || "Processing...";
  document.getElementById("loading-overlay").style.display = "flex";
}

function hideLoading() {
  document.getElementById("loading-overlay").style.display = "none";
}

